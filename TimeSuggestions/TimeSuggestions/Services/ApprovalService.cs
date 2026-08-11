using Microsoft.EntityFrameworkCore;
using TimeSuggestions.Contracts;
using TimeSuggestions.Data;
using TimeSuggestions.Models;

namespace TimeSuggestions.Services;

public enum ApprovalOutcome
{
    Success,
    SuggestionNotFound,
    SuggestionNotPending,
    SuggestionNotRejected,
    CaseNotFound,
    TimeEntryNotFound,

    /// <summary>Równoległe żądanie zdążyło utworzyć wpis dla tej samej sugestii — konflikt, nie duplikat.</summary>
    AlreadyApproved,

    /// <summary>Wpis rozliczony (zarchiwizowany) — cofnięcie zatwierdzenia jest zablokowane.</summary>
    TimeEntryArchived,

    /// <summary>Nowy wpis nachodziłby na istniejący — niezmiennik "minuta należy do najwyżej jednego wpisu".</summary>
    OverlapConflict,
}

/// <summary>Jawny wynik zamiast wyjątków — kontroler tłumaczy go na kody HTTP.</summary>
public record ApprovalResult(ApprovalOutcome Outcome, TimeEntry? CreatedEntry = null, string? Error = null);

/// <summary>
/// Zatwierdzanie i odrzucanie sugestii. Odrzucenie zmienia tylko status (bez usuwania
/// z bazy) — fizyczne usunięcie sprawiłoby, że kolejna synchronizacja przywróci sugestię.
/// </summary>
public class ApprovalService(AppDbContext db)
{
    public async Task<ApprovalResult> ApproveAsync(
        int suggestionId,
        ApproveSuggestionRequest request,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var suggestion = await db.Suggestions
            .FirstOrDefaultAsync(candidate => candidate.Id == suggestionId, cancellationToken);
        if (suggestion is null)
        {
            return new ApprovalResult(ApprovalOutcome.SuggestionNotFound);
        }

        if (suggestion.Status != SuggestionStatus.Pending)
        {
            return new ApprovalResult(ApprovalOutcome.SuggestionNotPending);
        }

        var selectedCase = await db.Cases
            .FirstOrDefaultAsync(legalCase => legalCase.Id == request.CaseId && legalCase.IsActive, cancellationToken);
        if (selectedCase is null)
        {
            return new ApprovalResult(ApprovalOutcome.CaseNotFound);
        }

        // Niezmiennik nakładania: nowy wpis nie może zachodzić na żaden istniejący
        // (dowolnego źródła, także rozliczony — rozliczona minuta to nadal minuta).
        // 409 z nazwą blokującego wpisu zamiast cichego nakładu. Dzień ±1, bo
        // spotkanie kalendarzowe może przechodzić przez północ.
        var newStart = suggestion.StartedAt;
        var newEnd = suggestion.StartedAt.AddMinutes(request.DurationMinutes);
        var neighborFrom = suggestion.EntryDate.AddDays(-1);
        var neighborTo = suggestion.EntryDate.AddDays(1);
        var blocking = await db.TimeEntries
            .Where(entry => entry.EntryDate >= neighborFrom && entry.EntryDate <= neighborTo
                && entry.StartedAt < newEnd && newStart < entry.EndedAt)
            .OrderBy(entry => entry.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (blocking is not null)
        {
            // Blokujący wpis powstał z TEJ SAMEJ sugestii = przegrany wyścig podwójnego
            // zatwierdzenia (nasz odczyt statusu był stary) — to inny komunikat niż
            // kolizja z cudzym wpisem. AsNoTracking: nasz kontekst śledzi sugestię
            // ze STARYM TimeEntryId, a tu potrzebujemy świeżej wartości z bazy.
            var linkedToSameSuggestion = await db.Suggestions.AsNoTracking()
                .AnyAsync(fresh => fresh.Id == suggestion.Id && fresh.TimeEntryId == blocking.Id, cancellationToken);
            if (linkedToSameSuggestion)
            {
                return new ApprovalResult(ApprovalOutcome.AlreadyApproved);
            }

            return new ApprovalResult(
                ApprovalOutcome.OverlapConflict,
                Error: $"Ten czas nachodzi na istniejący wpis „{blocking.Description}\" " +
                    $"({blocking.StartedAt:HH\\:mm}–{blocking.EndedAt:HH\\:mm}).");
        }

        var timeEntry = new TimeEntry
        {
            CaseId = selectedCase.Id,
            Case = selectedCase,
            EntryDate = suggestion.EntryDate,
            // Godziny wpisu ze StartedAt sugestii + finalnego czasu — od tej pory wpis
            // sam zna swoje położenie na osi dnia (niezmiennik nakładania, oś czasu).
            StartedAt = suggestion.StartedAt,
            EndedAt = suggestion.StartedAt.AddMinutes(request.DurationMinutes),
            DurationMinutes = request.DurationMinutes,
            Description = request.Description,
            CreatedFromSuggestion = true,
            Source = suggestion.Source,
            // Wykryte przerwy jadą na wpis — "Odejmij przerwę" waliduje zakres wobec
            // tej listy (danych z sesji), a nie wobec wartości przysłanych przez klienta.
            DetectedGapsJson = suggestion.DetectedGapsJson,
            CreatedAt = nowUtc,
        };

        suggestion.Status = SuggestionStatus.Approved;
        suggestion.TimeEntry = timeEntry;
        db.TimeEntries.Add(timeEntry);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Token współbieżności na statusie sugestii: równoległe zatwierdzenie
            // zdążyło zmienić status między naszym odczytem a zapisem — UPDATE trafił
            // zero wierszy, transakcja (łącznie z INSERT wpisu) wycofana.
            return new ApprovalResult(ApprovalOutcome.AlreadyApproved);
        }

        return new ApprovalResult(ApprovalOutcome.Success, timeEntry);
    }

    public async Task<ApprovalResult> RejectAsync(
        int suggestionId,
        CancellationToken cancellationToken)
    {
        var suggestion = await db.Suggestions
            .FirstOrDefaultAsync(candidate => candidate.Id == suggestionId, cancellationToken);
        if (suggestion is null)
        {
            return new ApprovalResult(ApprovalOutcome.SuggestionNotFound);
        }

        if (suggestion.Status != SuggestionStatus.Pending)
        {
            return new ApprovalResult(ApprovalOutcome.SuggestionNotPending);
        }

        suggestion.Status = SuggestionStatus.Rejected;
        await db.SaveChangesAsync(cancellationToken);

        return new ApprovalResult(ApprovalOutcome.Success);
    }

    /// <summary>Przywraca odrzuconą sugestię na listę oczekujących — pomyłka nie może być nieodwracalna.</summary>
    public async Task<ApprovalResult> RestoreAsync(int suggestionId, CancellationToken cancellationToken)
    {
        var suggestion = await db.Suggestions
            .FirstOrDefaultAsync(candidate => candidate.Id == suggestionId, cancellationToken);
        if (suggestion is null)
        {
            return new ApprovalResult(ApprovalOutcome.SuggestionNotFound);
        }

        if (suggestion.Status != SuggestionStatus.Rejected)
        {
            return new ApprovalResult(ApprovalOutcome.SuggestionNotRejected);
        }

        suggestion.Status = SuggestionStatus.Pending;
        await db.SaveChangesAsync(cancellationToken);

        return new ApprovalResult(ApprovalOutcome.Success);
    }

    /// <summary>
    /// Usuwa wpis czasu i przywraca powiązane sugestie do oczekujących
    /// (po scaleniu sesji wpis może mieć ich kilka — wracają wszystkie).
    /// Jedno SaveChanges = jedna transakcja: albo wszystkie kroki, albo żaden.
    /// </summary>
    public async Task<ApprovalResult> DeleteTimeEntryAsync(int timeEntryId, CancellationToken cancellationToken)
    {
        var timeEntry = await db.TimeEntries
            .Include(entry => entry.Suggestions)
            .FirstOrDefaultAsync(entry => entry.Id == timeEntryId, cancellationToken);
        if (timeEntry is null)
        {
            return new ApprovalResult(ApprovalOutcome.TimeEntryNotFound);
        }

        // Rozliczonego czasu nie wolno po cichu zmieniać — archiwum blokuje edycję.
        if (timeEntry.ArchivedAt is not null)
        {
            return new ApprovalResult(ApprovalOutcome.TimeEntryArchived);
        }

        foreach (var suggestion in timeEntry.Suggestions)
        {
            suggestion.Status = SuggestionStatus.Pending;
            suggestion.TimeEntryId = null;
        }

        db.TimeEntries.Remove(timeEntry);
        await db.SaveChangesAsync(cancellationToken);

        return new ApprovalResult(ApprovalOutcome.Success);
    }
}
