using TimeSuggestions.Configuration;
using TimeSuggestions.Contracts;
using TimeSuggestions.Models;

namespace TimeSuggestions.Services;

/// <summary>
/// Składa przefiltrowane dane z obu źródeł w jednolite obiekty sugestii.
/// Czysta logika: konfiguracja przez konstruktor, czas bieżący parametrem —
/// dzięki temu klasa jest deterministyczna i w pełni testowalna bez DI.
/// </summary>
public class SuggestionBuilder(SuggestionOptions options)
{
    private static readonly string[] AllowedDocumentExtensions = [".docx", ".doc", ".xlsx", ".xls"];

    public List<Suggestion> BuildFromCalendar(
        IEnumerable<CalendarEventDto> billableEvents,
        IReadOnlyList<Case> activeCases,
        DateTime createdAt)
        => billableEvents.Select(calendarEvent => BuildCalendarSuggestion(calendarEvent, activeCases, createdAt)).ToList();

    public List<Suggestion> BuildFromDocuments(
        IEnumerable<DriveFileDto> files,
        IReadOnlyList<Case> activeCases,
        DateTime windowStart,
        DateTime windowEnd,
        DateTime createdAt)
    {
        var eligibleFiles = files.Where(file => IsEligibleDocument(file, windowStart, windowEnd));

        // Agregacja: kilka modyfikacji tego samego pliku jednego dnia = jedna sugestia
        // (Graph mówi tylko KIEDY plik zmieniono, nie JAK DŁUGO nad nim pracowano).
        var oneFilePerDay = eligibleFiles
            .GroupBy(file => (file.Id, Date: DateOnly.FromDateTime(file.LastModifiedDateTime)))
            .Select(group => group.OrderBy(file => file.LastModifiedDateTime).First());

        return oneFilePerDay
            .Select(file => BuildDocumentSuggestion(file, activeCases, createdAt))
            .ToList();
    }

    private Suggestion BuildCalendarSuggestion(
        CalendarEventDto calendarEvent,
        IReadOnlyList<Case> activeCases,
        DateTime createdAt)
    {
        var title = string.IsNullOrWhiteSpace(calendarEvent.Subject) ? "(bez tytułu)" : calendarEvent.Subject;
        var match = CaseMatcher.Match(calendarEvent.Subject, activeCases);

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
        var match = CaseMatcher.Match(file.Name, activeCases);

        return new Suggestion
        {
            Source = SuggestionSource.Document,
            ExternalId = file.Id,
            Title = file.Name,
            StartedAt = file.LastModifiedDateTime,
            EntryDate = DateOnly.FromDateTime(file.LastModifiedDateTime),
            DurationMinutes = options.DefaultDocumentDurationMinutes,
            CaseId = match.MatchedCase?.Id,
            IsAmbiguous = match.Kind == MatchKind.Multiple,
            ProposedDescription = $"Praca nad dokumentem: {file.Name}",
            Status = SuggestionStatus.Pending,
            CreatedAt = createdAt,
        };
    }

    private static bool IsEligibleDocument(DriveFileDto file, DateTime windowStart, DateTime windowEnd)
    {
        if (!file.LastModifiedByMe)
        {
            return false;
        }

        if (file.LastModifiedDateTime < windowStart || file.LastModifiedDateTime > windowEnd)
        {
            return false;
        }

        return AllowedDocumentExtensions.Any(extension =>
            file.Name.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    }
}
