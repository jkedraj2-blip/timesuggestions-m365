using Microsoft.EntityFrameworkCore;
using TimeSuggestions.Contracts;
using TimeSuggestions.Data;
using TimeSuggestions.Models;

namespace TimeSuggestions.Services;

/// <summary>
/// Zbiorcze liczniki dla kafelków w nagłówku UI — użytkownik od razu widzi,
/// że system żyje: ile czeka, ile zapisano, ile czasu uzbierano.
/// </summary>
public class SummaryService(AppDbContext db)
{
    /// <summary>
    /// "Dzisiaj" liczone w strefie biznesowej z aktualnego UTC — czas lokalny serwera
    /// mógłby rozjechać kafelek "dzisiaj" o dobę względem dat wpisów.
    /// </summary>
    public static DateOnly GetBusinessToday(DateTime nowUtc, string businessTimeZoneId)
    {
        var businessTimeZone = TimeZoneInfo.FindSystemTimeZoneById(businessTimeZoneId);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(nowUtc, businessTimeZone));
    }

    public async Task<SummaryDto> GetSummaryAsync(DateOnly today, CancellationToken cancellationToken)
    {
        var pendingCount = await db.Suggestions
            .CountAsync(suggestion => suggestion.Status == SuggestionStatus.Pending, cancellationToken);
        var approvedCount = await db.Suggestions
            .CountAsync(suggestion => suggestion.Status == SuggestionStatus.Approved, cancellationToken);
        var rejectedCount = await db.Suggestions
            .CountAsync(suggestion => suggestion.Status == SuggestionStatus.Rejected, cancellationToken);

        // Sum() na pustej tabeli rzuca — stąd wariant z Select i domyślnym zerem.
        var totalLoggedMinutes = await db.TimeEntries
            .Select(entry => (int?)entry.DurationMinutes)
            .SumAsync(cancellationToken) ?? 0;
        var todayLoggedMinutes = await db.TimeEntries
            .Where(entry => entry.EntryDate == today)
            .Select(entry => (int?)entry.DurationMinutes)
            .SumAsync(cancellationToken) ?? 0;

        var lastSyncAt = await db.SyncRuns
            .OrderByDescending(run => run.RunAt)
            .Select(run => (DateTime?)run.RunAt)
            .FirstOrDefaultAsync(cancellationToken);

        return new SummaryDto(
            pendingCount,
            approvedCount,
            rejectedCount,
            totalLoggedMinutes,
            todayLoggedMinutes,
            lastSyncAt);
    }
}
