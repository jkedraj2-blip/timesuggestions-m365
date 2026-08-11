using Microsoft.EntityFrameworkCore;
using TimeSuggestions.Contracts;
using TimeSuggestions.Data;
using TimeSuggestions.Models;

namespace TimeSuggestions.Services;

/// <summary>
/// Oś czasu: agregacja per dzień dla paska miesiąca i lista pozycji jednego dnia.
/// Pasek miesiąca zasilają DWA zapytania grupujące (sugestie i wpisy) na cały zakres —
/// nie 31 osobnych żądań; scalanie wyników w pamięci jest tanie (maks. 31 kluczy).
/// </summary>
public class TimelineService(AppDbContext db)
{
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
                Status: "pending"))
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
                Status: entry.ArchivedAt == null ? "active" : "archived")))
            .OrderBy(item => item.StartedAt)
            .ThenBy(item => item.Id)
            .ToList();
    }
}
