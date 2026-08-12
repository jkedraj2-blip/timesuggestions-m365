using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TimeSuggestions.Configuration;
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
}

/// <summary>
/// Jawny wynik zamiast wyjątków — kontroler tłumaczy go na kody HTTP.
/// <paramref name="Notice"/> to informacja o tym, co zrobiliśmy z zasięgiem wpisu
/// (przycięcie do sąsiada, pozostałe nakładanie). NIE jest błędem: zatwierdzenie się
/// udało, a prawnik ma wiedzieć, czemu godziny wyglądają inaczej, niż się spodziewał.
/// </summary>
public record ApprovalResult(
    ApprovalOutcome Outcome,
    TimeEntry? CreatedEntry = null,
    string? Error = null,
    string? Notice = null);

/// <summary>
/// Zatwierdzanie i odrzucanie sugestii. Odrzucenie zmienia tylko status (bez usuwania
/// z bazy) — fizyczne usunięcie sprawiłoby, że kolejna synchronizacja przywróci sugestię.
/// </summary>
public class ApprovalService(AppDbContext db, IOptions<SuggestionOptions> optionsAccessor)
{
    private readonly TimeZoneInfo businessTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById(optionsAccessor.Value.BusinessTimeZoneId);

    /// <summary>Sąsiad na osi dnia: wpis albo oczekująca sugestia; Label w narzędniku do zdania Notice.</summary>
    private sealed record NeighborItem(DateTime StartedAt, DateTime EndedAt, string Label);

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

        // NAKŁADANIE NIE ODRZUCA ZATWIERDZENIA. Niezmiennik „minuta należy do najwyżej
        // jednego wpisu" trzymamy PRZYCINAJĄC ZASIĘG do najbliższej pozycji, a nie
        // odmawiając rozliczenia. Powód jest twardy: koniec zasięgu liczony z minut to
        // wartość POCHODNA (prawnik mógł poprawić czas ręcznie, sesja mogła zostać
        // scalona), a nie fakt z historii pliku — i potrafił zachodzić na sąsiada
        // o kilkanaście SEKUND, których interfejs w ogóle nie pokazuje. Tak powstała
        // odmowa „Ten czas nachodzi na wpis (00:07–03:31)" dla sugestii 23:53–00:02,
        // czyli pracy skończonej pięć minut przed tamtym wpisem: komunikat był
        // nielogiczny, bo porównywał wyświetlane minuty, a decyzja zapadała na sekundach.
        // W rozliczaniu czasu odmowa jest najgorszym możliwym wyjściem — zostawia pracę,
        // której nie da się rozliczyć. Przycięcie nie rusza rozliczanych minut (zasięg to
        // teren, czas to rachunek) i jest nazwane w komunikacie.
        // W niezmienniku uczestniczą wpisy KAŻDEGO źródła oraz OCZEKUJĄCE sugestie
        // (rezerwacje minut do rozstrzygnięcia) — dokładnie ten sam zbiór sąsiadów,
        // który ładują serwisy operacji (LoadDayItemsAsync). Zatwierdzenie widzące
        // wyłącznie wpisy nakrywało oczekującą sugestię bez przycięcia i bez Notice,
        // a ona dostawała później odmowę „Scalenie blokuje pozycja: wpis …" za blokadę,
        // o której nikt nie uprzedził.
        var neighborFrom = suggestion.EntryDate.AddDays(-1);
        var neighborTo = suggestion.EntryDate.AddDays(1);
        var entryNeighbors = await db.TimeEntries
            .AsNoTracking()
            .Where(entry => entry.EntryDate >= neighborFrom && entry.EntryDate <= neighborTo)
            .Select(entry => new { entry.StartedAt, entry.EndedAt, entry.Description })
            .ToListAsync(cancellationToken);

        var pendingNeighbors = await db.Suggestions
            .AsNoTracking()
            .Where(candidate => candidate.Status == SuggestionStatus.Pending
                && candidate.Id != suggestion.Id
                && candidate.EntryDate >= neighborFrom && candidate.EntryDate <= neighborTo)
            .Select(candidate => new
            {
                candidate.StartedAt,
                candidate.DurationMinutes,
                candidate.LastActivityAt,
                candidate.Title,
            })
            .ToListAsync(cancellationToken);

        var neighbors = entryNeighbors
            .Select(entry => new NeighborItem(entry.StartedAt, entry.EndedAt, $"wpisem „{entry.Description}\""))
            .Concat(pendingNeighbors.Select(candidate => new NeighborItem(
                candidate.StartedAt,
                SuggestionSpan.EndOf(
                    candidate.StartedAt, candidate.DurationMinutes, candidate.LastActivityAt, businessTimeZone),
                $"oczekującą sugestią „{candidate.Title}\"")))
            .ToList();

        var newStart = suggestion.StartedAt;
        // Koniec wpisu = koniec ZASIĘGU sugestii, a nie koniec liczonego czasu. Po
        // scaleniu sesji te dwie granice to co innego (patrz SuggestionSpan) i liczenie
        // po czasie kończyło wpis w środku pracy: druga, krótsza sesja wypadała poza
        // wpis — nie było jej w podświetleniu historii wersji, a jej minuty uchodziły
        // za wolne, choć wpis już je rozliczał.
        var wantedEnd = SuggestionSpan.EndOf(
            suggestion.StartedAt, request.DurationMinutes, suggestion.LastActivityAt, businessTimeZone);

        var nextStart = neighbors
            .Where(entry => entry.StartedAt >= newStart && entry.StartedAt < wantedEnd)
            .Select(entry => (DateTime?)entry.StartedAt)
            .Min();
        var newEnd = nextStart ?? wantedEnd;
        if (newEnd < newStart)
        {
            newEnd = newStart;
        }

        // Pozycja zaczynająca się PRZED nami i sięgająca za nasz początek to jedyne
        // nakładanie, którego przycięciem końca nie da się usunąć (praca nad dokumentem
        // w trakcie rozliczonego spotkania to realny przypadek, nie błąd danych).
        // Wpis i tak powstaje: dane mówią, że obie rzeczy się wydarzyły, a decyzja
        // o rozliczeniu należy do prawnika. Mówimy mu o tym wprost.
        var overlapping = neighbors
            .Where(entry => entry.StartedAt < newEnd && newStart < entry.EndedAt)
            .OrderBy(entry => entry.StartedAt)
            .FirstOrDefault();

        string? notice = null;
        if (overlapping is not null)
        {
            notice = $"Uwaga: ten czas pokrywa się z {overlapping.Label} "
                + $"({BusinessMoment.Format(overlapping.StartedAt)}–{BusinessMoment.Format(overlapping.EndedAt)}). "
                + "Wpis powstał, ale sprawdź, czy tych minut nie liczysz dwa razy.";
        }
        else if (nextStart is not null && wantedEnd > newEnd)
        {
            notice = $"Godziny wpisu kończą się o {BusinessMoment.Format(newEnd)}, bo tam zaczyna się "
                + $"kolejna pozycja. Rozliczany czas ({request.DurationMinutes} min) został bez zmian.";
        }

        var timeEntry = new TimeEntry
        {
            CaseId = selectedCase.Id,
            Case = selectedCase,
            EntryDate = suggestion.EntryDate,
            // Godziny wpisu z zasięgu sugestii — od tej pory wpis sam zna swoje
            // położenie na osi dnia (niezmiennik nakładania, oś czasu).
            StartedAt = newStart,
            EndedAt = newEnd,
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

        return new ApprovalResult(ApprovalOutcome.Success, timeEntry, Notice: notice);
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
