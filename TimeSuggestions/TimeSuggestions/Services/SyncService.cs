using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TimeSuggestions.Configuration;
using TimeSuggestions.Contracts;
using TimeSuggestions.Data;
using TimeSuggestions.Services;

namespace TimeSuggestions.Services;

/// <summary>
/// Serwis aplikacyjny synchronizacji: skleja logikę czystą z bazą.
/// Filtruje surowe dane, buduje sugestie i zapisuje wyłącznie te,
/// których jeszcze nie ma (klucz: źródło + id z Graph + dzień).
/// </summary>
public class SyncService(AppDbContext db, IOptions<SuggestionOptions> optionsAccessor)
{
    private readonly SuggestionOptions options = optionsAccessor.Value;

    public async Task<SyncResult> SyncAsync(SyncRequest request, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var activeCases = await db.Cases
            .Where(legalCase => legalCase.IsActive)
            .ToListAsync(cancellationToken);

        var builder = new SuggestionBuilder(options);

        var billableEvents = CalendarEventFilter.FilterBillable(
            request.CalendarEvents, options.MinimumEventDurationMinutes);

        // Backend powtarza walidację okna czasu po swojej stronie — frontendowi nie ufamy (granica systemu).
        var windowEnd = nowUtc;
        var windowStart = nowUtc.AddDays(-options.SyncDaysBack);

        var candidates = builder
            .BuildFromCalendar(billableEvents, activeCases, nowUtc)
            .Concat(builder.BuildFromDocuments(request.DriveFiles, activeCases, windowStart, windowEnd, nowUtc))
            .ToList();

        if (candidates.Count == 0)
        {
            return new SyncResult(0, 0);
        }

        var candidateExternalIds = candidates.Select(candidate => candidate.ExternalId).ToList();
        var existingKeys = (await db.Suggestions
                .Where(suggestion => candidateExternalIds.Contains(suggestion.ExternalId))
                .Select(suggestion => new { suggestion.Source, suggestion.ExternalId, suggestion.EntryDate })
                .ToListAsync(cancellationToken))
            .Select(key => (key.Source, key.ExternalId, key.EntryDate))
            .ToHashSet();

        var newSuggestions = candidates
            .Where(candidate => !existingKeys.Contains((candidate.Source, candidate.ExternalId, candidate.EntryDate)))
            .ToList();

        db.Suggestions.AddRange(newSuggestions);
        await db.SaveChangesAsync(cancellationToken);

        return new SyncResult(newSuggestions.Count, candidates.Count - newSuggestions.Count);
    }
}
