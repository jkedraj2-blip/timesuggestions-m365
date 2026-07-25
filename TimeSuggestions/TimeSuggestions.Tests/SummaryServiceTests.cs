using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TimeSuggestions.Data;
using TimeSuggestions.Models;
using TimeSuggestions.Services;

namespace TimeSuggestions.Tests;

public sealed class SummaryServiceTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 7, 25);

    private readonly SqliteConnection connection;
    private readonly AppDbContext db;
    private readonly SummaryService summaryService;

    public SummaryServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        db = new AppDbContext(options);
        db.Database.EnsureCreated();

        summaryService = new SummaryService(db);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }

    [Fact]
    public async Task GetSummaryAsync_ZwracaZeraDlaPustejBazy()
    {
        var summary = await summaryService.GetSummaryAsync(Today, CancellationToken.None);

        Assert.Equal(0, summary.PendingCount);
        Assert.Equal(0, summary.TotalLoggedMinutes);
        Assert.Null(summary.LastSyncAt);
    }

    [Fact]
    public async Task GetSummaryAsync_SumujeMinutyLacznieIOsobnoDlaDzisiaj()
    {
        db.TimeEntries.AddRange(
            CreateEntry(entryDate: Today, minutes: 60),
            CreateEntry(entryDate: Today, minutes: 30),
            CreateEntry(entryDate: Today.AddDays(-1), minutes: 45));
        await db.SaveChangesAsync();

        var summary = await summaryService.GetSummaryAsync(Today, CancellationToken.None);

        Assert.Equal(135, summary.TotalLoggedMinutes);
        Assert.Equal(90, summary.TodayLoggedMinutes);
    }

    [Fact]
    public async Task GetSummaryAsync_LiczySugestiePerStatusIZwracaOstatniaSynchronizacje()
    {
        db.Suggestions.AddRange(
            CreateSuggestion("event-1", SuggestionStatus.Pending),
            CreateSuggestion("event-2", SuggestionStatus.Pending),
            CreateSuggestion("event-3", SuggestionStatus.Approved),
            CreateSuggestion("event-4", SuggestionStatus.Rejected));
        db.SyncRuns.AddRange(
            new SyncRun { RunAt = new DateTime(2026, 7, 24, 10, 0, 0), Created = 3, SkippedExisting = 0 },
            new SyncRun { RunAt = new DateTime(2026, 7, 25, 9, 0, 0), Created = 1, SkippedExisting = 3 });
        await db.SaveChangesAsync();

        var summary = await summaryService.GetSummaryAsync(Today, CancellationToken.None);

        Assert.Equal(2, summary.PendingCount);
        Assert.Equal(1, summary.ApprovedCount);
        Assert.Equal(1, summary.RejectedCount);
        Assert.Equal(new DateTime(2026, 7, 25, 9, 0, 0), summary.LastSyncAt);
    }

    private static TimeEntry CreateEntry(DateOnly entryDate, int minutes)
    {
        // Wpis wymaga sugestii źródłowej (FK) — tworzymy minimalną parę.
        var suggestion = CreateSuggestion($"event-{Guid.NewGuid()}", SuggestionStatus.Approved);
        return new TimeEntry
        {
            CaseId = 1,
            EntryDate = entryDate,
            DurationMinutes = minutes,
            Description = "Praca",
            CreatedFromSuggestion = true,
            Source = SuggestionSource.Calendar,
            Suggestion = suggestion,
            CreatedAt = DateTime.UtcNow,
        };
    }

    private static Suggestion CreateSuggestion(string externalId, SuggestionStatus status) => new()
    {
        Source = SuggestionSource.Calendar,
        ExternalId = externalId,
        Title = "Spotkanie",
        StartedAt = new DateTime(2026, 7, 24, 10, 0, 0),
        EntryDate = new DateOnly(2026, 7, 24),
        DurationMinutes = 30,
        ProposedDescription = "Spotkanie",
        Status = status,
        CreatedAt = DateTime.UtcNow,
    };
}
