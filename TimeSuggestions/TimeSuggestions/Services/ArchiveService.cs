using Microsoft.EntityFrameworkCore;
using TimeSuggestions.Data;
using TimeSuggestions.Models;

namespace TimeSuggestions.Services;

/// <summary>Wynik archiwizacji hurtowej — liczby do komunikatu w UI.</summary>
public record ArchiveTimeEntriesResult(int ArchivedCount, int TotalMinutes);

public record ArchiveSuggestionsResult(int ArchivedCount);

/// <summary>
/// Rozliczanie (archiwizacja) wpisów czasu. Celowo osobny serwis od ApprovalService:
/// tamten obsługuje decyzje o pojedynczej sugestii, archiwizacja to operacje hurtowe
/// o innym kształcie wyniku. Archiwum jest jednokierunkowe — nie ma unarchive.
/// </summary>
public class ArchiveService(AppDbContext db)
{
    /// <summary>
    /// Archiwizuje aktywne wpisy z EntryDate w domkniętym przedziale [from, to].
    /// Idempotentna: już zarchiwizowane pomija i nie nadpisuje ich znacznika czasu —
    /// pierwotna data rozliczenia jest wartością audytową. Pusty zakres to sukces (0, 0).
    /// </summary>
    public async Task<ArchiveTimeEntriesResult> ArchiveTimeEntriesAsync(
        DateOnly from,
        DateOnly to,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var activeEntries = await db.TimeEntries
            .Where(entry => entry.ArchivedAt == null
                && entry.EntryDate >= from
                && entry.EntryDate <= to)
            .ToListAsync(cancellationToken);

        foreach (var entry in activeEntries)
        {
            entry.ArchivedAt = nowUtc;
        }

        await db.SaveChangesAsync(cancellationToken);

        return new ArchiveTimeEntriesResult(
            activeEntries.Count,
            activeEntries.Sum(entry => entry.DurationMinutes));
    }

    /// <summary>
    /// Rozliczenie POJEDYNCZEGO wpisu. Rozliczanie całego dnia zakłada, że dzień jest
    /// zamknięty, a to nieprawda w połowie sytuacji: część pracy jest gotowa do faktury,
    /// część czeka na rozstrzygnięcie przerw albo na wskazanie sprawy. Bez tej operacji
    /// prawnik musiał albo rozliczyć razem z gotowym wpisem coś, czego jeszcze nie
    /// sprawdził, albo nie rozliczyć nic.
    ///
    /// Zwraca wpis, żeby interfejs pokazał to, co naprawdę zapisano. Kształt wyniku jak
    /// w pozostałych operacjach na wpisie: ten sam mapper błędów w kontrolerze.
    /// </summary>
    public async Task<TimeEntryOperationResult> ArchiveTimeEntryAsync(
        int timeEntryId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var entry = await db.TimeEntries
            .Include(candidate => candidate.Case)
            .Include(candidate => candidate.Suggestions)
            .Include(candidate => candidate.Adjustments)
            .FirstOrDefaultAsync(candidate => candidate.Id == timeEntryId, cancellationToken);
        if (entry is null)
        {
            return new TimeEntryOperationResult(TimeEntryOperationStatus.NotFound, "Wpis czasu nie istnieje.");
        }

        // Data rozliczenia jest wartością audytową — powtórzone kliknięcie nie może jej
        // przesunąć, więc druga próba jest konfliktem, a nie cichym sukcesem.
        if (entry.ArchivedAt is not null)
        {
            return new TimeEntryOperationResult(
                TimeEntryOperationStatus.Archived, "Ten wpis jest już rozliczony.");
        }

        entry.ArchivedAt = nowUtc;
        await db.SaveChangesAsync(cancellationToken);

        return new TimeEntryOperationResult(TimeEntryOperationStatus.Success, Entries: [entry]);
    }

    /// <summary>
    /// Wszystkie odrzucone → zarchiwizowane. Zero odrzuconych to sukces z zerem.
    /// Rejected → Archived to jedyna droga do archiwum sugestii: Pending czeka na
    /// decyzję, a „archiwizacją" Approved jest archiwizacja powiązanego wpisu czasu.
    /// </summary>
    public async Task<ArchiveSuggestionsResult> ArchiveRejectedSuggestionsAsync(CancellationToken cancellationToken)
    {
        var rejected = await db.Suggestions
            .Where(suggestion => suggestion.Status == SuggestionStatus.Rejected)
            .ToListAsync(cancellationToken);

        foreach (var suggestion in rejected)
        {
            suggestion.Status = SuggestionStatus.Archived;
        }

        await db.SaveChangesAsync(cancellationToken);

        return new ArchiveSuggestionsResult(rejected.Count);
    }

    /// <summary>Wariant pojedynczy — symetria z reject/restore, ten sam kształt wyniku.</summary>
    public async Task<ApprovalResult> ArchiveSuggestionAsync(int suggestionId, CancellationToken cancellationToken)
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

        suggestion.Status = SuggestionStatus.Archived;
        await db.SaveChangesAsync(cancellationToken);

        return new ApprovalResult(ApprovalOutcome.Success);
    }
}
