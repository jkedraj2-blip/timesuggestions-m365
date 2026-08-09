using Microsoft.EntityFrameworkCore;
using TimeSuggestions.Data;

namespace TimeSuggestions.Services;

/// <summary>Wynik archiwizacji hurtowej — liczby do komunikatu w UI.</summary>
public record ArchiveTimeEntriesResult(int ArchivedCount, int TotalMinutes);

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
}
