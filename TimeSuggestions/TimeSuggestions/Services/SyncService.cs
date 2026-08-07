using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TimeSuggestions.Configuration;
using TimeSuggestions.Contracts;
using TimeSuggestions.Data;
using TimeSuggestions.Models;

namespace TimeSuggestions.Services;

/// <summary>
/// Trwały konflikt unikalności mimo ponowienia — kontroler tłumaczy go na 409
/// zamiast pozwolić na nieczytelny błąd 500.
/// </summary>
public class SyncConflictException()
    : Exception("Synchronizacja koliduje z inną trwającą synchronizacją — spróbuj ponownie.");

/// <summary>Wynik scalania kandydatów z istniejącymi sugestiami.</summary>
internal record MergeOutcome(List<Suggestion> NewSuggestions, int UpdatedCount, int RemovedCount);

/// <summary>
/// Serwis aplikacyjny synchronizacji: skleja logikę czystą z bazą.
/// Filtruje surowe dane, buduje sugestie, scala je z istniejącymi
/// (klucz: źródło + id z Graph + dzień), rekonsyliuje kalendarz
/// i składa pełny raport dla UI.
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
            BusinessTimeZoneId = options.BusinessTimeZoneId,
        };
        var builder = new SuggestionBuilder(effectiveOptions);

        // Backend nie ufa filtrom frontendu i powtarza własne (granica systemu):
        // dokumenty walidowane w oryginalnym UTC z Graph, kalendarz — bezpośrednio
        // w strefie biznesowej, bo czasy wydarzeń przychodzą już lokalne
        // (Prefer: outlook.timezone).
        var businessTimeZone = TimeZoneInfo.FindSystemTimeZoneById(options.BusinessTimeZoneId);
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, businessTimeZone);
        var windowStartLocal = nowLocal.AddDays(-options.SyncDaysBack);

        var eventFilterResult = CalendarEventFilter.FilterBillable(
            request.CalendarEvents,
            options.MinimumEventDurationMinutes,
            windowStart: windowStartLocal,
            windowEnd: nowLocal);

        var documentResult = builder.BuildFromDocuments(
            request.DriveFiles, activeCases, nowUtc.AddDays(-options.SyncDaysBack), nowUtc, nowUtc);

        var candidates = builder
            .BuildFromCalendar(eventFilterResult.Accepted, activeCases, nowUtc)
            .Concat(documentResult.Suggestions)
            // Duplikaty w obrębie jednego żądania nie mogą kończyć się naruszeniem
            // indeksu unikalnego — wygrywa ostatnie wystąpienie klucza.
            .GroupBy(candidate => (candidate.Source, candidate.ExternalId, candidate.EntryDate))
            .Select(group => group.Last())
            .ToList();

        // Konflikt unikalności = równoległa synchronizacja zdążyła zapisać te same klucze.
        // Po nieudanym SaveChanges kontekst nadal śledzi encje z tej próby, więc proste
        // ponowienie dodałoby je drugi raz — dlatego ChangeTracker.Clear() i CAŁY merge
        // od nowa (z ponownym odczytem istniejących). Maksymalnie jedno ponowienie;
        // inne błędy zapisu propagują bez maskowania.
        MergeOutcome merge;
        for (var attempt = 0; ; attempt++)
        {
            merge = await MergeWithExistingAsync(
                candidates,
                DateOnly.FromDateTime(windowStartLocal),
                DateOnly.FromDateTime(nowLocal),
                request.DeletedDriveFileIds,
                cancellationToken);

            db.Suggestions.AddRange(merge.NewSuggestions);

            // Zapis przebiegu w tej samej transakcji co sugestie — historia synchronizacji
            // zasila kafelek "ostatnia synchronizacja" w UI. Ta sama formuła
            // SkippedExisting co w raporcie: kandydaci - nowe - zaktualizowane.
            db.SyncRuns.Add(new SyncRun
            {
                RunAt = nowUtc,
                Created = merge.NewSuggestions.Count,
                SkippedExisting = candidates.Count - merge.NewSuggestions.Count - merge.UpdatedCount,
            });

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                break;
            }
            catch (DbUpdateException exception) when (SqliteErrors.IsUniqueConstraintViolation(exception))
            {
                db.ChangeTracker.Clear();
                if (attempt > 0)
                {
                    throw new SyncConflictException();
                }
            }
        }

        return new SyncReport(
            new SyncFetchedCounts(request.CalendarEvents.Count, request.DriveFiles.Count),
            new SyncFilteredOutCounts(
                Private: eventFilterResult.PrivateCount,
                TooShort: eventFilterResult.TooShortCount,
                AllDay: eventFilterResult.AllDayCount,
                Cancelled: eventFilterResult.CancelledCount,
                InvalidDates: eventFilterResult.InvalidDatesCount,
                NotOfficeDocument: documentResult.NotOfficeDocumentCount,
                OutsideWindow: documentResult.OutsideWindowCount + eventFilterResult.OutsideWindowCount,
                NotModifiedByUser: documentResult.NotModifiedByUserCount),
            Aggregated: documentResult.AggregatedCount,
            Created: merge.NewSuggestions.Count,
            Updated: merge.UpdatedCount,
            SkippedExisting: candidates.Count - merge.NewSuggestions.Count - merge.UpdatedCount,
            Removed: merge.RemovedCount,
            Matched: CountMatches(merge.NewSuggestions));
    }

    /// <summary>
    /// Scala kandydatów z istniejącymi sugestiami. Dokumenty: klucz
    /// (źródło, id, dzień) — delta jest przyrostowa, więc nieobecność pliku w feedzie
    /// niczego nie dowodzi i dokumentów NIE czyścimy na tej podstawie (usuwają je
    /// wyłącznie jawne tombstone'y). Kalendarz: pełny snapshot okna → rekonsyliacja
    /// per spotkanie (ExternalId). Rozstrzygniętych (zatwierdzone/odrzucone) nie ruszamy.
    /// </summary>
    private async Task<MergeOutcome> MergeWithExistingAsync(
        List<Suggestion> candidates,
        DateOnly windowStartDate,
        DateOnly windowEndDate,
        IReadOnlyCollection<string> deletedDriveFileIds,
        CancellationToken cancellationToken)
    {
        var newSuggestions = new List<Suggestion>();
        var updatedCount = 0;
        var removedCount = 0;

        var documentCandidates = candidates
            .Where(candidate => candidate.Source == SuggestionSource.Document)
            .ToList();
        if (documentCandidates.Count > 0)
        {
            var documentIds = documentCandidates.Select(candidate => candidate.ExternalId).ToList();
            var existingByKey = (await db.Suggestions
                    .Where(suggestion => suggestion.Source == SuggestionSource.Document
                        && documentIds.Contains(suggestion.ExternalId))
                    .ToListAsync(cancellationToken))
                .ToDictionary(suggestion => (suggestion.ExternalId, suggestion.EntryDate));

            foreach (var candidate in documentCandidates)
            {
                if (!existingByKey.TryGetValue((candidate.ExternalId, candidate.EntryDate), out var existing))
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
        }

        // Tombstone'y z delta: plik usunięty z OneDrive → jego OCZEKUJĄCE sugestie
        // znikają (dowolny dzień); zatwierdzone i odrzucone zostają.
        if (deletedDriveFileIds.Count > 0)
        {
            var deletedPending = await db.Suggestions
                .Where(suggestion => suggestion.Source == SuggestionSource.Document
                    && suggestion.Status == SuggestionStatus.Pending
                    && deletedDriveFileIds.Contains(suggestion.ExternalId))
                .ToListAsync(cancellationToken);

            db.Suggestions.RemoveRange(deletedPending);
            removedCount += deletedPending.Count;
        }

        // Kalendarz: zbiorem odniesienia są kandydaci pozostali PO WSZYSTKICH filtrach
        // (rozliczalni). Spotkanie, które nadal istnieje w Graph, ale stało się anulowane,
        // prywatne czy całodniowe, traktujemy jak usunięte — jego Pending znika.
        // Założenie: frontend przerywa synchronizację przy błędzie pobierania strony,
        // więc backend dostaje wyłącznie KOMPLETNE snapshoty okna (api.service.ts).
        var calendarCandidates = candidates
            .Where(candidate => candidate.Source == SuggestionSource.Calendar)
            .ToList();
        var candidateIds = calendarCandidates.Select(candidate => candidate.ExternalId).ToHashSet();
        var existingCalendar = await db.Suggestions
            .Where(suggestion => suggestion.Source == SuggestionSource.Calendar
                && (candidateIds.Contains(suggestion.ExternalId)
                    || (suggestion.EntryDate >= windowStartDate && suggestion.EntryDate <= windowEndDate)))
            .ToListAsync(cancellationToken);
        var existingByExternalId = existingCalendar
            .GroupBy(suggestion => suggestion.ExternalId)
            .ToDictionary(group => group.Key, group => group.ToList());

        // Sugestie "zagospodarowane" przez kandydatów — pozostałe Pending w oknie znikną.
        var retained = new HashSet<Suggestion>();

        foreach (var candidate in calendarCandidates)
        {
            if (!existingByExternalId.TryGetValue(candidate.ExternalId, out var existingForMeeting))
            {
                newSuggestions.Add(candidate);
                continue;
            }

            // Spotkanie już rozstrzygnięte (dowolna data) → nie tworzymy duplikatu pod
            // nową datą: odrzucenie jest "lepkie" per spotkanie, a zatwierdzonego
            // spotkania nie rozliczamy drugi raz po przesunięciu (świadoma decyzja —
            // wpis czasu już istnieje). Liczy się jako SkippedExisting.
            if (existingForMeeting.Any(suggestion => suggestion.Status != SuggestionStatus.Pending))
            {
                continue;
            }

            // Oczekująca: aktualizacja w miejscu — po przesunięciu spotkania zmieniają
            // się też EntryDate i StartedAt, zamiast powstawać nowa sugestia-duch.
            var pending = existingForMeeting.FirstOrDefault(suggestion => suggestion.EntryDate == candidate.EntryDate)
                ?? existingForMeeting[0];
            if (RefreshFromSource(pending, candidate))
            {
                updatedCount++;
            }

            retained.Add(pending);
        }

        // Usuwanie: Pending w oknie syncu, których spotkanie zniknęło ze snapshotu albo
        // przestało być rozliczalne — oraz osierocone duplikaty tego samego spotkania.
        // Okno odnosi się do EntryDate sugestii. Approved/Rejected nie ruszamy.
        var stalePending = existingCalendar
            .Where(suggestion => suggestion.Status == SuggestionStatus.Pending
                && suggestion.EntryDate >= windowStartDate
                && suggestion.EntryDate <= windowEndDate
                && !retained.Contains(suggestion))
            .ToList();
        db.Suggestions.RemoveRange(stalePending);
        removedCount += stalePending.Count;

        return new MergeOutcome(newSuggestions, updatedCount, removedCount);
    }

    /// <summary>Nadpisuje wartości pochodzące ze źródła; zwraca true, gdy coś się realnie zmieniło.</summary>
    private static bool RefreshFromSource(Suggestion existing, Suggestion fromSource)
    {
        var changed = existing.Title != fromSource.Title
            || existing.StartedAt != fromSource.StartedAt
            || existing.EntryDate != fromSource.EntryDate
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
        existing.EntryDate = fromSource.EntryDate;
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
