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

        // Preferencje użytkownika z żądania (zwalidowane na granicy API) nadpisują
        // konfigurację; same reguły biznesowe zostają w backendzie.
        var effectiveOptions = new SuggestionOptions
        {
            MinimumEventDurationMinutes = options.MinimumEventDurationMinutes,
            SyncDaysBack = request.SyncDaysBack ?? options.SyncDaysBack,
            BusinessTimeZoneId = options.BusinessTimeZoneId,
            SessionContinuationGapMinutes = options.SessionContinuationGapMinutes,
            SessionFlaggedGapMinutes = options.SessionFlaggedGapMinutes,
            MinimumSessionMinutes = options.MinimumSessionMinutes,
        };
        var builder = new SuggestionBuilder(effectiveOptions);

        // Backend nie ufa filtrom frontendu i powtarza własne (granica systemu):
        // dokumenty walidowane w oryginalnym UTC z Graph, kalendarz — bezpośrednio
        // w strefie biznesowej, bo czasy wydarzeń przychodzą już lokalne
        // (Prefer: outlook.timezone).
        var businessTimeZone = TimeZoneInfo.FindSystemTimeZoneById(options.BusinessTimeZoneId);
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, businessTimeZone);

        // Okno "N dni" = dziś plus N-1 poprzednich pełnych dób lokalnych: początek dnia
        // lokalnego w strefie biznesowej, nie "N×24h wstecz" — odejmowanie godzin
        // rozjeżdża się z dobami lokalnymi o godzinę przy zmianie czasu. Frontend
        // pobiera z Graph z dobą zapasu, a jedynym źródłem prawdy FILTRU jest backend.
        // Rozjazd konfiguracji okien (frontend vs Suggestions:SyncDaysBack) nie jest
        // przez to groźny dla filtrowania; kasowanie ma dodatkowo własną granicę —
        // przecięcie z zakresem zadeklarowanym przez klienta (niżej).
        var windowStartLocal = nowLocal.Date.AddDays(-(effectiveOptions.SyncDaysBack - 1));

        var eventFilterResult = CalendarEventFilter.FilterBillable(
            request.CalendarEvents,
            options.MinimumEventDurationMinutes,
            windowStart: windowStartLocal,
            windowEnd: nowLocal,
            businessTimeZone);

        // Sesje liczą się z UNII dziennika i bieżącego payloadu: dziennik pamięta fakty,
        // które OneDrive mógł już wyciąć z historii wersji (ograniczona retencja),
        // a payload niesie fakty jeszcze niezapisane (INSERT dzieje się dopiero w pętli
        // zapisu niżej) — dopiero razem dają pełną znaną historię pliku.
        var activitiesByFile = await LoadActivitiesUnionAsync(request.DriveFiles, nowUtc, cancellationToken);

        // Praca już rozliczona nie może wrócić jako ta sama sesja — a praca PO niej
        // musi dać nową sugestię. Jedno i drugie załatwia wycięcie rozstrzygniętych
        // przedziałów z wejścia silnika sesji.
        var settledRangesByFile = await LoadSettledRangesAsync(request.DriveFiles, cancellationToken);

        // To samo okno dla dokumentów — granica lokalna przeliczona na instant UTC,
        // bo czasy modyfikacji plików z Graph są w UTC.
        var documentResult = builder.BuildFromDocuments(
            request.DriveFiles,
            activeCases,
            BusinessTime.ToUtcInstant(windowStartLocal, businessTimeZone),
            nowUtc,
            nowUtc,
            activitiesByFile,
            settledRangesByFile);

        var rawCandidates = builder
            .BuildFromCalendar(eventFilterResult.Accepted, activeCases, nowUtc)
            .Concat(documentResult.Suggestions)
            .ToList();

        // Duplikaty w obrębie jednego żądania nie mogą kończyć się naruszeniem
        // indeksu unikalnego — wygrywa ostatnie wystąpienie klucza. Liczba scalonych
        // duplikatów trafia do raportu (Deduplicated) — bez niej "Pobrano" przestaje
        // się sumować z resztą liczników (Graph potrafi zduplikować wydarzenie
        // między stronami przy zmieniającej się kolekcji).
        var candidates = rawCandidates
            .GroupBy(candidate => (candidate.Source, candidate.ExternalId, candidate.SessionAnchor))
            .Select(group => group.Last())
            .ToList();
        var deduplicatedCount = rawCandidates.Count - candidates.Count;

        // Destrukcyjna rekonsyliacja kalendarza działa wyłącznie w PRZECIĘCIU okna
        // backendu z zakresem zadeklarowanym przez klienta: kompletność snapshotu
        // dowodzi nieobecności spotkania tylko w dniach, które klient faktycznie
        // pobrał. Starszy klient (kompletność bez zakresu) nie kasuje niczego.
        DateOnly? destructiveWindowStart =
            request.CalendarSnapshotComplete && request.CalendarSnapshotDaysBack is int clientDaysBack
                ? DateOnly.FromDateTime(
                    nowLocal.Date.AddDays(-(Math.Min(clientDaysBack, effectiveOptions.SyncDaysBack) - 1)))
                : null;

        // Konflikt unikalności = równoległa synchronizacja zdążyła zapisać te same klucze.
        // Po nieudanym SaveChanges kontekst nadal śledzi encje z tej próby, więc proste
        // ponowienie dodałoby je drugi raz — dlatego ChangeTracker.Clear() i CAŁY merge
        // od nowa (z ponownym odczytem istniejących). Maksymalnie jedno ponowienie;
        // inne błędy zapisu propagują bez maskowania.
        MergeOutcome merge;
        int newActivitiesCount;
        for (var attempt = 0; ; attempt++)
        {
            merge = await MergeWithExistingAsync(
                candidates,
                documentResult.SessionBased,
                DateOnly.FromDateTime(windowStartLocal),
                DateOnly.FromDateTime(nowLocal),
                request.DeletedDriveFileIds,
                destructiveWindowStart,
                cancellationToken);

            // Dziennik wersji wewnątrz tej samej pętli ponowień: po ChangeTracker.Clear()
            // trzeba przeliczyć brakujące fakty od nowa, a indeks (ExternalId, VersionId)
            // domyka wyścig równoległych synchronizacji tak samo jak klucz sugestii.
            newActivitiesCount = await RecordDocumentActivitiesAsync(request.DriveFiles, nowUtc, cancellationToken);

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

        // Liczniki filtrów klienckich doliczane do raportu: backend celowo nie widzi
        // np. tytułów prywatnych wydarzeń, więc bez tych liczb raport zaniżałby odrzucenia.
        var clientCounts = request.ClientFilteredCounts;

        return new SyncReport(
            // "Pobrano" = kandydaci do rozliczenia: pozycje przekazane do backendu
            // PLUS odfiltrowane już w przeglądarce (per źródło), MINUS te spoza okna.
            // Zapas pobierany ponad okno nie jest ani kandydatem, ani pominiętą pracą —
            // odjęcie go po obu stronach zachowuje niezmiennik sumy raportu.
            new SyncFetchedCounts(
                request.CalendarEvents.Count + clientCounts.Private + clientCounts.Cancelled
                    - eventFilterResult.OutsideWindowCount,
                request.DriveFiles.Count + clientCounts.DocumentsNotOfficeDocument
                    - documentResult.OutsideWindowCount),
            new SyncFilteredOutCounts(
                Private: eventFilterResult.PrivateCount + clientCounts.Private,
                TooShort: eventFilterResult.TooShortCount,
                AllDay: eventFilterResult.AllDayCount,
                Cancelled: eventFilterResult.CancelledCount + clientCounts.Cancelled,
                InvalidDates: eventFilterResult.InvalidDatesCount,
                NotOfficeDocument: documentResult.NotOfficeDocumentCount + clientCounts.DocumentsNotOfficeDocument,
                NotModifiedByUser: documentResult.NotModifiedByUserCount),
            Aggregated: documentResult.AggregatedCount,
            Deduplicated: deduplicatedCount,
            Created: merge.NewSuggestions.Count,
            Updated: merge.UpdatedCount,
            SkippedExisting: candidates.Count - merge.NewSuggestions.Count - merge.UpdatedCount,
            Removed: merge.RemovedCount,
            Matched: CountMatches(merge.NewSuggestions),
            WindowDays: effectiveOptions.SyncDaysBack,
            Versions: new SyncVersionCounts(
                FilesWithHistory: request.DriveFiles.Count(file => file.Versions is { Count: > 0 }),
                FilesWithoutHistory: request.DriveFiles.Count(file => file.Versions is not { Count: > 0 }),
                FetchErrors: request.DriveFileVersionFetchErrors,
                NewActivities: newActivitiesCount));
    }

    /// <summary>
    /// Dopisuje do append-only dziennika DocumentActivity fakty, których jeszcze nie ma
    /// (klucz naturalny: plik + wersja + moment). Fakty rejestrujemy dla WSZYSTKICH plików
    /// z payloadu, także tych, które filtry sugestii odrzucą — dziennik jest źródłem
    /// prawdy o historii, nie pochodną reguł sugestii. Rekordów nigdy nie modyfikujemy.
    ///
    /// Oprócz wersji zapisujemy PRÓBKĘ z samego pliku (lastModifiedDateTime elementu).
    /// To jedyny ślad pracy trwającej wewnątrz wersji, której Word online jeszcze nie
    /// zapieczętował: przy ciągłej edycji lista wersji stoi w miejscu godzinami,
    /// a znacznik pliku idzie do przodu. Każdy sync jest więc pomiarem — im częściej
    /// prawnik synchronizuje, tym gęstsza historia (uzasadnienie przy DocumentActivity).
    /// </summary>
    private async Task<int> RecordDocumentActivitiesAsync(
        IReadOnlyList<DriveFileDto> files,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        // Dedup w obrębie żądania (Graph może powtórzyć wersję między stronami) —
        // wygrywa pierwsze wystąpienie klucza, bo fakt jest niezmienny.
        var incoming = new Dictionary<ActivityKey, DocumentActivity>();
        foreach (var file in files)
        {
            var versionInstants = new HashSet<DateTime>();
            foreach (var version in file.Versions ?? [])
            {
                var occurredAt = AsUtc(version.LastModifiedDateTime);
                versionInstants.Add(occurredAt);
                incoming.TryAdd(
                    new ActivityKey(file.Id, version.VersionId, occurredAt),
                    new DocumentActivity
                    {
                        ExternalId = file.Id,
                        VersionId = version.VersionId,
                        OccurredAt = occurredAt,
                        Size = version.Size,
                        RecordedAt = nowUtc,
                    });
            }

            // Próbkę z pliku bierzemy tylko wtedy, gdy klient FAKTYCZNIE odpytał o wersje
            // (Versions != null) i gdy wnosi moment, którego nie ma żadna wersja. Przy
            // nieudanym pobraniu historii nie wiemy, czy próbka nie dubluje wersji,
            // której nie zobaczyliśmy — plik zostaje wtedy na torze fallbackowym.
            var fileModifiedAt = AsUtc(file.LastModifiedDateTime);
            if (file.Versions is not null && !versionInstants.Contains(fileModifiedAt))
            {
                incoming.TryAdd(
                    new ActivityKey(file.Id, DocumentActivity.ItemObservationLabel, fileModifiedAt),
                    new DocumentActivity
                    {
                        ExternalId = file.Id,
                        VersionId = DocumentActivity.ItemObservationLabel,
                        OccurredAt = fileModifiedAt,
                        Size = file.Size,
                        RecordedAt = nowUtc,
                    });
            }
        }

        if (incoming.Count == 0)
        {
            return 0;
        }

        var fileIds = incoming.Keys.Select(key => key.ExternalId).Distinct().ToList();
        var existingKeys = (await db.DocumentActivities
                .Where(activity => fileIds.Contains(activity.ExternalId))
                .Select(activity => new { activity.ExternalId, activity.VersionId, activity.OccurredAt })
                .ToListAsync(cancellationToken))
            .Select(existing => new ActivityKey(existing.ExternalId, existing.VersionId, AsUtc(existing.OccurredAt)))
            .ToHashSet();

        var newActivities = incoming
            .Where(pair => !existingKeys.Contains(pair.Key))
            .Select(pair => pair.Value)
            .ToList();

        db.DocumentActivities.AddRange(newActivities);
        return newActivities.Count;
    }

    /// <summary>Klucz naturalny faktu w dzienniku — moment jest jego częścią, patrz DocumentActivity.</summary>
    private readonly record struct ActivityKey(string ExternalId, string VersionId, DateTime OccurredAt);

    /// <summary>
    /// lastModifiedDateTime wersji z Graph jest w UTC, ale po deserializacji Kind bywa
    /// Unspecified (JSON bez "Z") — jawnie oznaczamy wartość jako UTC.
    /// </summary>
    private static DateTime AsUtc(DateTime dateTime) => dateTime.Kind switch
    {
        DateTimeKind.Utc => dateTime,
        DateTimeKind.Local => dateTime.ToUniversalTime(),
        _ => DateTime.SpecifyKind(dateTime, DateTimeKind.Utc),
    };

    /// <summary>
    /// Scala kandydatów z istniejącymi sugestiami. Dokumenty: klucz
    /// (źródło, id, dzień) — delta jest przyrostowa, więc nieobecność pliku w feedzie
    /// niczego nie dowodzi i dokumentów NIE czyścimy na tej podstawie (usuwają je
    /// wyłącznie jawne tombstone'y). Kalendarz: rekonsyliacja per spotkanie (ExternalId);
    /// część destrukcyjna działa tylko od <paramref name="destructiveWindowStart"/>
    /// (przecięcie okna backendu z zadeklarowanym zakresem snapshotu; null = brak
    /// kompletnego snapshotu albo nieznany zakres — nic nie kasujemy).
    /// Rozstrzygniętych (zatwierdzone/odrzucone) nie ruszamy.
    /// </summary>
    private async Task<MergeOutcome> MergeWithExistingAsync(
        List<Suggestion> candidates,
        IReadOnlySet<Suggestion> sessionCandidates,
        DateOnly windowStartDate,
        DateOnly windowEndDate,
        IReadOnlyCollection<string> deletedDriveFileIds,
        DateOnly? destructiveWindowStart,
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
            var existingDocuments = await db.Suggestions
                .Where(suggestion => suggestion.Source == SuggestionSource.Document
                    && documentIds.Contains(suggestion.ExternalId))
                .ToListAsync(cancellationToken);
            var existingByKey = existingDocuments
                .ToDictionary(suggestion => (suggestion.ExternalId, suggestion.SessionAnchor));
            var existingByFile = existingDocuments
                .GroupBy(suggestion => suggestion.ExternalId)
                .ToDictionary(group => group.Key, group => group.ToList());
            var alreadyRemoved = new HashSet<Suggestion>();

            foreach (var candidate in documentCandidates)
            {
                existingByKey.TryGetValue((candidate.ExternalId, candidate.SessionAnchor), out var target);

                // Przypadek brzegowy sesji: sync doniósł ZALEGŁĄ wersję. Starsza niż
                // kotwica, bliżej niż próg sesji → sesja scalana "w dół": kotwica
                // kandydata leży PRZED kotwicą starej sugestii, a stara kotwica mieści
                // się wewnątrz nowej sesji — starej sugestii nie duplikujemy, tylko
                // aktualizujemy w miejscu (kotwica się przesuwa). Wersja W ŚRODKU między
                // dwiema sesjami mostkuje je w jedną: pierwsza sugestia jest celem
                // aktualizacji, nadmiarowe oczekujące znikają. Ta sama reguła przejmuje
                // dawną sugestię fallbackową (kotwica = północ dnia), gdy plik zyskał
                // historię wersji.
                List<Suggestion> swallowed = [];
                if (sessionCandidates.Contains(candidate))
                {
                    var sessionEndUtc = candidate.SessionAnchor.AddMinutes(candidate.DurationMinutes);
                    swallowed = existingByFile.GetValueOrDefault(candidate.ExternalId, [])
                        .Where(suggestion => !alreadyRemoved.Contains(suggestion)
                            && !ReferenceEquals(suggestion, target)
                            && ((suggestion.SessionAnchor > candidate.SessionAnchor
                                    && suggestion.SessionAnchor <= sessionEndUtc)
                                || (IsFallbackAnchor(suggestion) && suggestion.EntryDate == candidate.EntryDate)))
                        .OrderBy(suggestion => suggestion.SessionAnchor)
                        .ToList();
                }

                if (target is null)
                {
                    // Rozstrzygnięta (zatwierdzona/odrzucona) sugestia wewnątrz sesji:
                    // sesję już rozpatrzono — nie odtwarzamy jej pod nową kotwicą
                    // (odrzucona nie wraca, zatwierdzonej nie rozliczamy drugi raz).
                    if (swallowed.Any(suggestion => suggestion.Status != SuggestionStatus.Pending))
                    {
                        continue;
                    }

                    target = swallowed.FirstOrDefault();
                    swallowed = swallowed.Skip(1).ToList();
                }

                if (target is null)
                {
                    newSuggestions.Add(candidate);
                    continue;
                }

                // Licznik rośnie tylko przy faktycznej zmianie — raport nie może kłamać.
                // Sugestii poprawionej ręcznie nie przeliczamy: decyzja prawnika o czasie
                // jest ważniejsza niż nasze wyliczenie z historii wersji.
                if (target.Status == SuggestionStatus.Pending
                    && !target.IsUserAdjusted
                    && RefreshFromSource(target, candidate))
                {
                    updatedCount++;
                }

                // Zmostkowane sesje: znikają wyłącznie OCZEKUJĄCE nadmiarowe sugestie —
                // rozstrzygniętych sync nie dotyka (mogą chwilowo współistnieć z szerszą
                // sesją; użytkownik rozstrzygnął je świadomie).
                foreach (var extra in swallowed.Where(s => s.Status == SuggestionStatus.Pending))
                {
                    db.Suggestions.Remove(extra);
                    alreadyRemoved.Add(extra);
                    removedCount++;
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

        // Usuwanie: Pending, których spotkanie zniknęło ze snapshotu albo przestało
        // być rozliczalne — oraz osierocone duplikaty tego samego spotkania.
        // Okno odnosi się do EntryDate sugestii i zaczyna się od destructiveWindowStart
        // (przecięcie okna backendu z zakresem klienta): nieobecność spotkania
        // w dniach, których klient nie pobrał — jak i w niepełnym payloadzie —
        // niczego nie dowodzi i nie może kasować prawidłowych sugestii.
        if (destructiveWindowStart is DateOnly deletionStartDate)
        {
            var stalePending = existingCalendar
                .Where(suggestion => suggestion.Status == SuggestionStatus.Pending
                    && suggestion.EntryDate >= deletionStartDate
                    && suggestion.EntryDate <= windowEndDate
                    && !retained.Contains(suggestion))
                .ToList();
            db.Suggestions.RemoveRange(stalePending);
            removedCount += stalePending.Count;
        }

        return new MergeOutcome(newSuggestions, updatedCount, removedCount);
    }

    /// <summary>Nadpisuje wartości pochodzące ze źródła; zwraca true, gdy coś się realnie zmieniło.</summary>
    private static bool RefreshFromSource(Suggestion existing, Suggestion fromSource)
    {
        var changed = existing.Title != fromSource.Title
            || existing.StartedAt != fromSource.StartedAt
            || existing.SessionAnchor != fromSource.SessionAnchor
            || existing.LastActivityAt != fromSource.LastActivityAt
            || existing.EntryDate != fromSource.EntryDate
            || existing.DurationMinutes != fromSource.DurationMinutes
            || existing.DetectedGapsJson != fromSource.DetectedGapsJson
            || existing.NeedsTimeReview != fromSource.NeedsTimeReview
            || existing.CaseId != fromSource.CaseId
            || existing.IsAmbiguous != fromSource.IsAmbiguous
            || existing.ProposedDescription != fromSource.ProposedDescription;

        if (!changed)
        {
            return false;
        }

        existing.Title = fromSource.Title;
        existing.StartedAt = fromSource.StartedAt;
        existing.SessionAnchor = fromSource.SessionAnchor;
        existing.LastActivityAt = fromSource.LastActivityAt;
        existing.EntryDate = fromSource.EntryDate;
        existing.DurationMinutes = fromSource.DurationMinutes;
        existing.DetectedGapsJson = fromSource.DetectedGapsJson;
        existing.NeedsTimeReview = fromSource.NeedsTimeReview;
        existing.CaseId = fromSource.CaseId;
        existing.IsAmbiguous = fromSource.IsAmbiguous;
        existing.ProposedDescription = fromSource.ProposedDescription;
        return true;
    }

    /// <summary>
    /// Kotwica fallbackowa (plik bez historii wersji) = północ dnia biznesowego.
    /// Rozpoznanie po kształcie wartości: sesyjne kotwice to czasy pierwszych wersji
    /// w UTC — trafienie dokładnie w lokalną północ jest praktycznie wykluczone,
    /// a fałszywe trafienie skończyłoby się co najwyżej odświeżeniem w miejscu.
    /// </summary>
    private static bool IsFallbackAnchor(Suggestion suggestion)
        => suggestion.SessionAnchor.TimeOfDay == TimeSpan.Zero
            && DateOnly.FromDateTime(suggestion.SessionAnchor) == suggestion.EntryDate;

    /// <summary>
    /// Unia historii: fakty już zapisane w dzienniku + fakty z bieżącego payloadu
    /// (jeszcze niezapisane — INSERT dzieje się w pętli zapisu). Dziennik pamięta wersje,
    /// które OneDrive mógł już wyciąć z historii (retencja), payload niesie najnowsze —
    /// łącznie z próbką z samego pliku, dzięki której trwająca edycja liczy się już
    /// w tym przebiegu, a nie dopiero po zapieczętowaniu wersji przez Worda.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<DocumentActivity>>> LoadActivitiesUnionAsync(
        IReadOnlyList<DriveFileDto> files,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var fileIds = files.Select(file => file.Id).Distinct().ToList();
        if (fileIds.Count == 0)
        {
            return new Dictionary<string, IReadOnlyList<DocumentActivity>>();
        }

        var union = (await db.DocumentActivities
                .AsNoTracking()
                .Where(activity => fileIds.Contains(activity.ExternalId))
                .ToListAsync(cancellationToken))
            .ToDictionary(activity =>
                new ActivityKey(activity.ExternalId, activity.VersionId, AsUtc(activity.OccurredAt)));

        foreach (var file in files)
        {
            foreach (var version in file.Versions ?? [])
            {
                var occurredAt = AsUtc(version.LastModifiedDateTime);
                union.TryAdd(new ActivityKey(file.Id, version.VersionId, occurredAt), new DocumentActivity
                {
                    ExternalId = file.Id,
                    VersionId = version.VersionId,
                    OccurredAt = occurredAt,
                    Size = version.Size,
                    RecordedAt = nowUtc,
                });
            }

            if (file.Versions is null)
            {
                continue;
            }

            var fileModifiedAt = AsUtc(file.LastModifiedDateTime);
            union.TryAdd(
                new ActivityKey(file.Id, DocumentActivity.ItemObservationLabel, fileModifiedAt),
                new DocumentActivity
                {
                    ExternalId = file.Id,
                    VersionId = DocumentActivity.ItemObservationLabel,
                    OccurredAt = fileModifiedAt,
                    Size = file.Size,
                    RecordedAt = nowUtc,
                });
        }

        return union.Values
            .GroupBy(activity => activity.ExternalId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<DocumentActivity>)group.ToList());
    }

    /// <summary>
    /// Przedziały pracy, których nie wolno budować od nowa, per plik. Są dwa powody:
    /// sugestia została ZATWIERDZONA (praca jest już rozliczona wpisem, a dalsza edycja
    /// pliku ma dać NOWĄ sugestię, nie ginąć na kluczu dedupu istniejącej), albo prawnik
    /// POPRAWIŁ ją ręcznie (scalenie sesji, doliczona luka — odtworzenie sesji składowych
    /// cofnęłoby jego decyzję).
    ///
    /// Odrzuconych i zarchiwizowanych tu NIE MA celowo: one nie mówią „to już rozliczone",
    /// tylko „tej pracy nie rozliczam". Ich lepkość — także wobec zaległej wersji
    /// przesuwającej kotwicę — załatwia reguła w scalaniu, która nie odtwarza sesji
    /// obejmującej rozstrzygniętą sugestię.
    ///
    /// Bierzemy wyłącznie sugestie sesyjne: kotwica fallbackowa (północ dnia) opisuje
    /// cały dzień, więc jako przedział wycięłaby z historii wszystko, co tego dnia
    /// zrobiono.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<SettledRange>>> LoadSettledRangesAsync(
        IReadOnlyList<DriveFileDto> files,
        CancellationToken cancellationToken)
    {
        var fileIds = files.Select(file => file.Id).Distinct().ToList();
        if (fileIds.Count == 0)
        {
            return new Dictionary<string, IReadOnlyList<SettledRange>>();
        }

        var settled = await db.Suggestions
            .AsNoTracking()
            .Where(suggestion => suggestion.Source == SuggestionSource.Document
                && (suggestion.Status == SuggestionStatus.Approved
                    || (suggestion.Status == SuggestionStatus.Pending && suggestion.IsUserAdjusted))
                && fileIds.Contains(suggestion.ExternalId))
            .ToListAsync(cancellationToken);

        return settled
            .Where(suggestion => !IsFallbackAnchor(suggestion))
            .GroupBy(suggestion => suggestion.ExternalId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<SettledRange>)group
                    .Select(suggestion => new SettledRange(
                        AsUtc(suggestion.SessionAnchor),
                        AsUtc(suggestion.LastActivityAt)))
                    .ToList());
    }

    private static SyncMatchedCounts CountMatches(IReadOnlyList<Suggestion> suggestions) => new(
        Single: suggestions.Count(suggestion => suggestion.CaseId is not null),
        None: suggestions.Count(suggestion => suggestion.CaseId is null && !suggestion.IsAmbiguous),
        Ambiguous: suggestions.Count(suggestion => suggestion.IsAmbiguous));
}
