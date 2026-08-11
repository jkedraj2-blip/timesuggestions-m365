using TimeSuggestions.Configuration;
using TimeSuggestions.Contracts;
using TimeSuggestions.Models;

namespace TimeSuggestions.Services;

/// <summary>
/// Wynik budowy sugestii z dokumentów wraz z licznikami odrzuceń i agregacji —
/// zasilają raport synchronizacji widoczny dla użytkownika. SessionBased wskazuje
/// kandydatów wyliczonych z historii wersji (silnik sesji): merge stosuje do nich
/// regułę "scalania w dół" kotwicy, a do kandydatów fallbackowych nie.
/// </summary>
public record DocumentBuildResult(
    List<Suggestion> Suggestions,
    int NotModifiedByUserCount,
    int OutsideWindowCount,
    int NotOfficeDocumentCount,
    int AggregatedCount,
    IReadOnlySet<Suggestion> SessionBased);

/// <summary>
/// Składa przefiltrowane dane z obu źródeł w jednolite obiekty sugestii.
/// Czysta logika: konfiguracja przez konstruktor, czas bieżący parametrem —
/// dzięki temu klasa jest deterministyczna i w pełni testowalna bez DI.
/// </summary>
public class SuggestionBuilder(SuggestionOptions options)
{
    private static readonly string[] AllowedDocumentExtensions = [".docx", ".doc", ".xlsx", ".xls"];

    /// <summary>
    /// Strefa biznesowa do przeliczania czasów UTC dokumentów. ID IANA działa też na
    /// Windows (konwersja przez ICU) — potwierdzone testem.
    /// </summary>
    private readonly TimeZoneInfo businessTimeZone = TimeZoneInfo.FindSystemTimeZoneById(options.BusinessTimeZoneId);

    public List<Suggestion> BuildFromCalendar(
        IEnumerable<CalendarEventDto> billableEvents,
        IReadOnlyList<Case> activeCases,
        DateTime createdAt)
        => billableEvents.Select(calendarEvent => BuildCalendarSuggestion(calendarEvent, activeCases, createdAt)).ToList();

    public DocumentBuildResult BuildFromDocuments(
        IEnumerable<DriveFileDto> files,
        IReadOnlyList<Case> activeCases,
        DateTime windowStart,
        DateTime windowEnd,
        DateTime createdAt,
        IReadOnlyDictionary<string, IReadOnlyList<DocumentActivity>>? activitiesByFile = null)
    {
        var eligibleFiles = new List<DriveFileDto>();
        var notModifiedByUserCount = 0;
        var outsideWindowCount = 0;
        var notOfficeDocumentCount = 0;

        // Kategoryzacja odrzuceń po pierwszym niespełnionym warunku — liczniki
        // trafiają do raportu synchronizacji pokazywanego użytkownikowi.
        // Okno czasu sprawdzane na oryginalnym UTC z Graph; konwersja na strefę
        // biznesową następuje dopiero przy budowie sugestii.
        foreach (var file in files)
        {
            if (!file.LastModifiedByMe)
            {
                notModifiedByUserCount++;
                continue;
            }

            var modifiedAtUtc = AsUtc(file.LastModifiedDateTime);
            if (modifiedAtUtc < windowStart || modifiedAtUtc > windowEnd)
            {
                outsideWindowCount++;
                continue;
            }

            if (!HasAllowedExtension(file.Name))
            {
                notOfficeDocumentCount++;
                continue;
            }

            eligibleFiles.Add(file);
        }

        // Rozdział na dwa tory: plik z historią wersji w dzienniku → sesje z silnika
        // (realny czas pracy zamiast sztywnych 30 min); plik bez historii (stary sync,
        // błąd Graph dla pojedynczego pliku) → dotychczasowy fallback z domyślnym
        // czasem. Błąd wersji jednego pliku nie wywala syncu — po prostu ląduje w torze
        // fallbackowym, a raport wersji liczy go osobno.
        var sessionEngine = new DocumentSessionEngine(options);
        var sessionBased = new HashSet<Suggestion>();
        var suggestions = new List<Suggestion>();
        var fallbackFiles = new List<DriveFileDto>();

        foreach (var file in eligibleFiles)
        {
            if (activitiesByFile is not null
                && activitiesByFile.TryGetValue(file.Id, out var activities)
                && activities.Count > 0)
            {
                foreach (var session in sessionEngine.BuildSessions(activities))
                {
                    // Okno na osi UTC (kotwica i przybliżony koniec sesji): sesje sprzed
                    // okna mają już swoje sugestie z poprzednich synców — odtwarzanie ich
                    // rozszerzałoby okno tylnymi drzwiami.
                    var sessionEndUtc = session.AnchorUtc.AddMinutes(session.GrossMinutes);
                    if (sessionEndUtc < windowStart || session.AnchorUtc > windowEnd)
                    {
                        continue;
                    }

                    var suggestion = BuildSessionSuggestion(file, session, activeCases, createdAt);
                    suggestions.Add(suggestion);
                    sessionBased.Add(suggestion);
                }
            }
            else
            {
                fallbackFiles.Add(file);
            }
        }

        // Agregacja (tylko tor fallbackowy): kilka modyfikacji tego samego pliku jednego
        // dnia = jedna sugestia (bez historii wersji Graph mówi tylko KIEDY plik zmieniono).
        // Dzień liczony w strefie biznesowej — edycja 22:30 UTC to już następny dzień lokalny.
        var oneFilePerDay = fallbackFiles
            .GroupBy(file => (file.Id, Date: DateOnly.FromDateTime(ToBusinessLocal(file.LastModifiedDateTime))))
            .Select(group => group.OrderBy(file => file.LastModifiedDateTime).First())
            .ToList();

        suggestions.AddRange(oneFilePerDay.Select(file => BuildDocumentSuggestion(file, activeCases, createdAt)));

        return new DocumentBuildResult(
            suggestions,
            notModifiedByUserCount,
            outsideWindowCount,
            notOfficeDocumentCount,
            AggregatedCount: fallbackFiles.Count - oneFilePerDay.Count,
            SessionBased: sessionBased);
    }

    /// <summary>Sugestia z jednej sesji pracy — czas i godziny z historii wersji, nie z domyślnych 30 min.</summary>
    private Suggestion BuildSessionSuggestion(
        DriveFileDto file,
        DocumentSession session,
        IReadOnlyList<Case> activeCases,
        DateTime createdAt)
    {
        var match = CaseMatcher.Match(file.Name, activeCases, MatchTextSource.DocumentName);

        return new Suggestion
        {
            Source = SuggestionSource.Document,
            ExternalId = file.Id,
            Title = file.Name,
            StartedAt = session.StartAt,
            // Kotwica sesji = czas pierwszej wersji (UTC): stabilna, gdy sesja rośnie
            // "od tyłu" (kolejne wersje przesuwają tylko koniec). Przypadek zaległej
            // wersji STARSZEJ niż kotwica obsługuje merge w SyncService (scalanie w dół).
            SessionAnchor = session.AnchorUtc,
            EntryDate = DateOnly.FromDateTime(session.StartAt),
            DurationMinutes = session.GrossMinutes,
            DetectedGapsJson = DetectedGaps.Serialize(session.DetectedGaps),
            CaseId = match.MatchedCase?.Id,
            IsAmbiguous = match.Kind == MatchKind.Multiple,
            ProposedDescription = $"Praca nad dokumentem: {file.Name}",
            Status = SuggestionStatus.Pending,
            CreatedAt = createdAt,
        };
    }

    private Suggestion BuildCalendarSuggestion(
        CalendarEventDto calendarEvent,
        IReadOnlyList<Case> activeCases,
        DateTime createdAt)
    {
        var title = string.IsNullOrWhiteSpace(calendarEvent.Subject) ? "(bez tytułu)" : calendarEvent.Subject;
        var match = CaseMatcher.Match(calendarEvent.Subject, activeCases, MatchTextSource.MeetingTitle);

        // Czasy z Graph przychodzą lokalne (Prefer: outlook.timezone), ale JSON z "Z"
        // lub offsetem też musi dać spójny czas lokalny strefy biznesowej.
        var startedAtLocal = BusinessTime.ToBusinessLocal(calendarEvent.StartDateTime, businessTimeZone);

        return new Suggestion
        {
            Source = SuggestionSource.Calendar,
            ExternalId = calendarEvent.Id,
            Title = title,
            StartedAt = startedAtLocal,
            // Kotwica kalendarza = lokalny początek spotkania: dedup działa per
            // ExternalId, kotwica domyka tylko wyścig równoległych synchronizacji.
            SessionAnchor = startedAtLocal,
            EntryDate = DateOnly.FromDateTime(startedAtLocal),
            DurationMinutes = CalendarEventFilter.GetDurationMinutes(calendarEvent, businessTimeZone),
            CaseId = match.MatchedCase?.Id,
            IsAmbiguous = match.Kind == MatchKind.Multiple,
            ProposedDescription = title,
            Status = SuggestionStatus.Pending,
            CreatedAt = createdAt,
        };
    }

    private Suggestion BuildDocumentSuggestion(
        DriveFileDto file,
        IReadOnlyList<Case> activeCases,
        DateTime createdAt)
    {
        var match = CaseMatcher.Match(file.Name, activeCases, MatchTextSource.DocumentName);

        // StartedAt i EntryDate z tego samego czasu lokalnego — dokumenty i kalendarz
        // (przychodzący już w strefie lokalnej) mają wspólną podstawę czasu.
        var startedAtLocal = ToBusinessLocal(file.LastModifiedDateTime);

        return new Suggestion
        {
            Source = SuggestionSource.Document,
            ExternalId = file.Id,
            Title = file.Name,
            StartedAt = startedAtLocal,
            // Fallback bez historii wersji: kotwica = początek dnia biznesowego,
            // czyli odpowiednik dawnego klucza po EntryDate — ostatnia modyfikacja
            // pliku zmienia się między syncami, więc nie nadaje się na stabilny klucz.
            // Silnik sesji (dla plików Z historią) podmienia kotwicę na czas
            // pierwszej wersji sesji.
            SessionAnchor = startedAtLocal.Date,
            EntryDate = DateOnly.FromDateTime(startedAtLocal),
            DurationMinutes = options.DefaultDocumentDurationMinutes,
            CaseId = match.MatchedCase?.Id,
            IsAmbiguous = match.Kind == MatchKind.Multiple,
            ProposedDescription = $"Praca nad dokumentem: {file.Name}",
            Status = SuggestionStatus.Pending,
            CreatedAt = createdAt,
        };
    }

    private static bool HasAllowedExtension(string fileName)
        => AllowedDocumentExtensions.Any(extension =>
            fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    private DateTime ToBusinessLocal(DateTime dateTime)
        => TimeZoneInfo.ConvertTimeFromUtc(AsUtc(dateTime), businessTimeZone);

    /// <summary>
    /// lastModifiedDateTime z Graph jest w UTC, ale po deserializacji Kind bywa
    /// Unspecified (JSON bez "Z") — jawnie oznaczamy wartość jako UTC.
    /// </summary>
    private static DateTime AsUtc(DateTime dateTime) => dateTime.Kind switch
    {
        DateTimeKind.Utc => dateTime,
        DateTimeKind.Local => dateTime.ToUniversalTime(),
        _ => DateTime.SpecifyKind(dateTime, DateTimeKind.Utc),
    };
}
