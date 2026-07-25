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

        // Preferencja użytkownika z żądania (zwalidowana na granicy API) nadpisuje
        // konfigurację; sama reguła "co robimy, gdy nie znamy czasu" zostaje w backendzie.
        var effectiveOptions = new SuggestionOptions
        {
            MinimumEventDurationMinutes = options.MinimumEventDurationMinutes,
            DefaultDocumentDurationMinutes = request.DefaultDocumentDurationMinutes ?? options.DefaultDocumentDurationMinutes,
            SyncDaysBack = options.SyncDaysBack,
        };
        var builder = new SuggestionBuilder(effectiveOptions);

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

        var (newSuggestions, updatedCount) = await MergeWithExistingAsync(candidates, cancellationToken);

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
            Updated: updatedCount,
            SkippedExisting: candidates.Count - newSuggestions.Count - updatedCount,
            Matched: CountMatches(newSuggestions));
    }

    /// <summary>
    /// Scala kandydatów z istniejącymi sugestiami. Nowe wracają do wstawienia;
    /// istniejące OCZEKUJĄCE są odświeżane (np. zmiana nazwy pliku → nowy tytuł
    /// i ponowne dopasowanie). Rozstrzygniętych (zatwierdzone/odrzucone) nie ruszamy —
    /// inaczej odrzucona sugestia wróciłaby na listę.
    /// </summary>
    private async Task<(List<Suggestion> NewSuggestions, int UpdatedCount)> MergeWithExistingAsync(
        List<Suggestion> candidates,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return ([], 0);
        }

        var candidateExternalIds = candidates.Select(candidate => candidate.ExternalId).ToList();
        var existingByKey = (await db.Suggestions
                .Where(suggestion => candidateExternalIds.Contains(suggestion.ExternalId))
                .ToListAsync(cancellationToken))
            .ToDictionary(suggestion => (suggestion.Source, suggestion.ExternalId, suggestion.EntryDate));

        var newSuggestions = new List<Suggestion>();
        var updatedCount = 0;

        foreach (var candidate in candidates)
        {
            var key = (candidate.Source, candidate.ExternalId, candidate.EntryDate);
            if (!existingByKey.TryGetValue(key, out var existing))
            {
                newSuggestions.Add(candidate);
                continue;
            }

            // Licznik rośnie tylko przy faktycznej zmianie — raport nie może kłamać.
            if (existing.Status == SuggestionStatus.Pending && RefreshFromSource(existing, candidate))
            {
                updatedCount++;
            }
        }

        return (newSuggestions, updatedCount);
    }

    /// <summary>Nadpisuje wartości pochodzące ze źródła; zwraca true, gdy coś się realnie zmieniło.</summary>
    private static bool RefreshFromSource(Suggestion existing, Suggestion fromSource)
    {
        var changed = existing.Title != fromSource.Title
            || existing.StartedAt != fromSource.StartedAt
            || existing.DurationMinutes != fromSource.DurationMinutes
            || existing.CaseId != fromSource.CaseId
            || existing.IsAmbiguous != fromSource.IsAmbiguous
            || existing.ProposedDescription != fromSource.ProposedDescription;

        if (!changed)
        {
            return false;
        }

        existing.Title = fromSource.Title;
        existing.StartedAt = fromSource.StartedAt;
        existing.DurationMinutes = fromSource.DurationMinutes;
        existing.CaseId = fromSource.CaseId;
        existing.IsAmbiguous = fromSource.IsAmbiguous;
        existing.ProposedDescription = fromSource.ProposedDescription;
        return true;
    }

    private static SyncMatchedCounts CountMatches(IReadOnlyList<Suggestion> suggestions) => new(
        Single: suggestions.Count(suggestion => suggestion.CaseId is not null),
        None: suggestions.Count(suggestion => suggestion.CaseId is null && !suggestion.IsAmbiguous),
        Ambiguous: suggestions.Count(suggestion => suggestion.IsAmbiguous));
}
