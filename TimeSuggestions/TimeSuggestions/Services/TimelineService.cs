using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TimeSuggestions.Configuration;
using TimeSuggestions.Contracts;
using TimeSuggestions.Data;
using TimeSuggestions.Models;

namespace TimeSuggestions.Services;

/// <summary>
/// Oś czasu: agregacja per dzień dla paska miesiąca i lista pozycji jednego dnia.
/// Pasek miesiąca zasilają DWA zapytania grupujące (sugestie i wpisy) na cały zakres —
/// nie 31 osobnych żądań; scalanie wyników w pamięci jest tanie (maks. 31 kluczy).
/// </summary>
public class TimelineService(AppDbContext db, IOptions<SuggestionOptions> optionsAccessor)
{
    private readonly SuggestionOptions options = optionsAccessor.Value;
    public async Task<List<TimelineDayDto>> GetRangeAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        var pendingByDate = await db.Suggestions
            .Where(suggestion => suggestion.Status == SuggestionStatus.Pending
                && suggestion.EntryDate >= from && suggestion.EntryDate <= to)
            .GroupBy(suggestion => suggestion.EntryDate)
            .Select(group => new { Date = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        var entriesByDate = await db.TimeEntries
            .Where(entry => entry.EntryDate >= from && entry.EntryDate <= to)
            .GroupBy(entry => entry.EntryDate)
            .Select(group => new
            {
                Date = group.Key,
                Active = group.Count(entry => entry.ArchivedAt == null),
                Archived = group.Count(entry => entry.ArchivedAt != null),
            })
            .ToListAsync(cancellationToken);

        // Zwracamy tylko dni z pozycjami — puste dni frontend wygasza sam,
        // a odpowiedź nie rośnie z długością zakresu.
        return pendingByDate
            .Select(day => (day.Date, Pending: day.Count, Active: 0, Archived: 0))
            .Concat(entriesByDate.Select(day => (day.Date, Pending: 0, day.Active, day.Archived)))
            .GroupBy(day => day.Date)
            .Select(group => new TimelineDayDto(
                group.Key,
                group.Sum(day => day.Pending),
                group.Sum(day => day.Active),
                group.Sum(day => day.Archived)))
            .OrderBy(day => day.Date)
            .ToList();
    }

    public async Task<List<TimelineItemDto>> GetDayAsync(DateOnly date, CancellationToken cancellationToken)
    {
        var pendingSuggestions = await db.Suggestions
            .Include(suggestion => suggestion.Case)
            .Where(suggestion => suggestion.Status == SuggestionStatus.Pending && suggestion.EntryDate == date)
            .ToListAsync(cancellationToken);

        var entries = await db.TimeEntries
            .Include(entry => entry.Case)
            .Include(entry => entry.Suggestions)
            .Where(entry => entry.EntryDate == date)
            .ToListAsync(cancellationToken);

        return pendingSuggestions
            .Select(suggestion => new TimelineItemDto(
                Type: "suggestion",
                suggestion.Id,
                suggestion.Source,
                suggestion.StartedAt,
                suggestion.StartedAt.AddMinutes(suggestion.DurationMinutes),
                suggestion.DurationMinutes,
                suggestion.Title,
                suggestion.Case?.Name,
                suggestion.Case?.CaseNumber,
                suggestion.Case?.ClientName,
                Status: "pending",
                ExternalId: suggestion.Source == SuggestionSource.Document ? suggestion.ExternalId : null))
            .Concat(entries.Select(entry => new TimelineItemDto(
                Type: "timeEntry",
                entry.Id,
                entry.Source,
                entry.StartedAt,
                entry.EndedAt,
                entry.DurationMinutes,
                entry.Description,
                entry.Case?.Name,
                entry.Case?.CaseNumber,
                entry.Case?.ClientName,
                Status: entry.ArchivedAt == null ? "active" : "archived",
                ExternalId: entry.Source == SuggestionSource.Document
                    ? entry.Suggestions.FirstOrDefault()?.ExternalId
                    : null)))
            .OrderBy(item => item.StartedAt)
            .ThenBy(item => item.Id)
            .ToList();
    }

    /// <summary>
    /// Chronologia modyfikacji pliku z append-only dziennika DocumentActivity:
    /// godzina każdej wersji (strefa biznesowa), rozmiar i przerwa od poprzedniej,
    /// z oznaczeniem przerw w przedziale wykrywanym (15–30 min). Dane już są w bazie —
    /// to czysty odczyt, bez wywołań Graph.
    /// </summary>
    public async Task<List<DocumentActivityDto>> GetDocumentActivityAsync(
        string externalId,
        CancellationToken cancellationToken)
    {
        var businessTimeZone = TimeZoneInfo.FindSystemTimeZoneById(options.BusinessTimeZoneId);

        var activities = (await db.DocumentActivities
                .AsNoTracking()
                .Where(activity => activity.ExternalId == externalId)
                .ToListAsync(cancellationToken))
            .OrderBy(activity => activity.OccurredAt)
            .ToList();

        var result = new List<DocumentActivityDto>(activities.Count);
        DateTime? previousUtc = null;
        foreach (var activity in activities)
        {
            var occurredUtc = DateTime.SpecifyKind(activity.OccurredAt, DateTimeKind.Utc);
            int? gapMinutes = previousUtc is DateTime previous
                ? (int)Math.Round((occurredUtc - previous).TotalMinutes)
                : null;

            result.Add(new DocumentActivityDto(
                activity.VersionId,
                TimeZoneInfo.ConvertTimeFromUtc(occurredUtc, businessTimeZone),
                activity.Size,
                gapMinutes,
                IsDetectedGapRange: gapMinutes > options.SessionContinuationGapMinutes
                    && gapMinutes <= options.SessionFlaggedGapMinutes));
            previousUtc = occurredUtc;
        }

        return result;
    }
}
