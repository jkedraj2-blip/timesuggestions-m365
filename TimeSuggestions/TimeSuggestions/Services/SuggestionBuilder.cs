using TimeSuggestions.Configuration;
using TimeSuggestions.Contracts;
using TimeSuggestions.Models;

namespace TimeSuggestions.Services;

/// <summary>
/// Wynik budowy sugestii z dokumentów wraz z licznikami odrzuceń i agregacji —
/// zasilają raport synchronizacji widoczny dla użytkownika.
/// </summary>
public record DocumentBuildResult(
    List<Suggestion> Suggestions,
    int NotModifiedByUserCount,
    int OutsideWindowCount,
    int NotOfficeDocumentCount,
    int AggregatedCount);

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
        DateTime createdAt)
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

        // Agregacja: kilka modyfikacji tego samego pliku jednego dnia = jedna sugestia
        // (Graph mówi tylko KIEDY plik zmieniono, nie JAK DŁUGO nad nim pracowano).
        // Dzień liczony w strefie biznesowej — edycja 22:30 UTC to już następny dzień lokalny.
        var oneFilePerDay = eligibleFiles
            .GroupBy(file => (file.Id, Date: DateOnly.FromDateTime(ToBusinessLocal(file.LastModifiedDateTime))))
            .Select(group => group.OrderBy(file => file.LastModifiedDateTime).First())
            .ToList();

        var suggestions = oneFilePerDay
            .Select(file => BuildDocumentSuggestion(file, activeCases, createdAt))
            .ToList();

        return new DocumentBuildResult(
            suggestions,
            notModifiedByUserCount,
            outsideWindowCount,
            notOfficeDocumentCount,
            AggregatedCount: eligibleFiles.Count - oneFilePerDay.Count);
    }

    private Suggestion BuildCalendarSuggestion(
        CalendarEventDto calendarEvent,
        IReadOnlyList<Case> activeCases,
        DateTime createdAt)
    {
        var title = string.IsNullOrWhiteSpace(calendarEvent.Subject) ? "(bez tytułu)" : calendarEvent.Subject;
        var match = CaseMatcher.Match(calendarEvent.Subject, activeCases, MatchTextSource.MeetingTitle);

        return new Suggestion
        {
            Source = SuggestionSource.Calendar,
            ExternalId = calendarEvent.Id,
            Title = title,
            StartedAt = calendarEvent.StartDateTime,
            EntryDate = DateOnly.FromDateTime(calendarEvent.StartDateTime),
            DurationMinutes = CalendarEventFilter.GetDurationMinutes(calendarEvent),
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
