using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TimeSuggestions.Configuration;
using TimeSuggestions.Contracts;
using TimeSuggestions.Data;
using TimeSuggestions.Models;

namespace TimeSuggestions.Services;

/// <summary>
/// Serwis aplikacyjny synchronizacji: skleja logikę czystą z bazą.
/// Filtruje surowe dane, buduje sugestie, zapisuje wyłącznie nowe
/// (klucz: źródło + id z Graph + dzień) i składa pełny raport dla UI.
/// </summary>
public class SyncService(AppDbContext db, IOptions<SuggestionOptions> optionsAccessor)
{
    private readonly SuggestionOptions options = optionsAccessor.Value;

    public async Task<SyncReport> SyncAsync(SyncRequest request, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var activeCases = await db.Cases
            .Where(legalCase => legalCase.IsActive)
            .ToListAsync(cancellationToken);

        var builder = new SuggestionBuilder(options);

        var eventFilterResult = CalendarEventFilter.FilterBillable(
            request.CalendarEvents, options.MinimumEventDurationMinutes);

        // Backend powtarza walidację okna czasu po swojej stronie — frontendowi nie ufamy (granica systemu).
        var windowEnd = nowUtc;
        var windowStart = nowUtc.AddDays(-options.SyncDaysBack);

        var documentResult = builder.BuildFromDocuments(
            request.DriveFiles, activeCases, windowStart, windowEnd, nowUtc);

        var candidates = builder
            .BuildFromCalendar(eventFilterResult.Accepted, activeCases, nowUtc)
            .Concat(documentResult.Suggestions)
            .ToList();

        var newSuggestions = await FilterOutExistingAsync(candidates, cancellationToken);

        db.Suggestions.AddRange(newSuggestions);

        // Zapis przebiegu w tej samej transakcji co sugestie — historia synchronizacji
        // zasila kafelek "ostatnia synchronizacja" w UI.
        db.SyncRuns.Add(new SyncRun
        {
            RunAt = nowUtc,
            Created = newSuggestions.Count,
            SkippedExisting = candidates.Count - newSuggestions.Count,
        });

        await db.SaveChangesAsync(cancellationToken);

        return new SyncReport(
            new SyncFetchedCounts(request.CalendarEvents.Count, request.DriveFiles.Count),
            new SyncFilteredOutCounts(
                Private: eventFilterResult.PrivateCount,
                TooShort: eventFilterResult.TooShortCount,
                AllDay: eventFilterResult.AllDayCount,
                NotOfficeDocument: documentResult.NotOfficeDocumentCount,
                OutsideWindow: documentResult.OutsideWindowCount,
                NotModifiedByUser: documentResult.NotModifiedByUserCount),
            Aggregated: documentResult.AggregatedCount,
            Created: newSuggestions.Count,
            SkippedExisting: candidates.Count - newSuggestions.Count,
            Matched: CountMatches(newSuggestions));
    }

    private async Task<List<Suggestion>> FilterOutExistingAsync(
        List<Suggestion> candidates,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        var candidateExternalIds = candidates.Select(candidate => candidate.ExternalId).ToList();
        var existingKeys = (await db.Suggestions
                .Where(suggestion => candidateExternalIds.Contains(suggestion.ExternalId))
                .Select(suggestion => new { suggestion.Source, suggestion.ExternalId, suggestion.EntryDate })
                .ToListAsync(cancellationToken))
            .Select(key => (key.Source, key.ExternalId, key.EntryDate))
            .ToHashSet();

        return candidates
            .Where(candidate => !existingKeys.Contains((candidate.Source, candidate.ExternalId, candidate.EntryDate)))
            .ToList();
    }

    private static SyncMatchedCounts CountMatches(IReadOnlyList<Suggestion> suggestions) => new(
        Single: suggestions.Count(suggestion => suggestion.CaseId is not null),
        None: suggestions.Count(suggestion => suggestion.CaseId is null && !suggestion.IsAmbiguous),
        Ambiguous: suggestions.Count(suggestion => suggestion.IsAmbiguous));
}
