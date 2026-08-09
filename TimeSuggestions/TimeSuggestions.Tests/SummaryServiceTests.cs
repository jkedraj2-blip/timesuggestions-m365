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
        Assert.Equal(0, summary.UnsettledMinutes);
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

        Assert.Equal(135, summary.UnsettledMinutes);
        Assert.Equal(90, summary.TodayLoggedMinutes);
    }

    [Fact]
    public async Task GetSummaryAsync_PomijaZarchiwizowaneWpisyWObuLicznikach()
    {
        // Wpis aktywny i zarchiwizowany z tego samego dnia — kafelki liczą tylko aktywny:
        // archiwizacja jest jedynym „resetem" nierozliczonego czasu.
        db.TimeEntries.AddRange(
            CreateEntry(entryDate: Today, minutes: 60),
            CreateEntry(entryDate: Today, minutes: 30, archivedAt: DateTime.UtcNow),
            CreateEntry(entryDate: Today.AddDays(-1), minutes: 45, archivedAt: DateTime.UtcNow));
        await db.SaveChangesAsync();

        var summary = await summaryService.GetSummaryAsync(Today, CancellationToken.None);

        Assert.Equal(60, summary.UnsettledMinutes);
        Assert.Equal(60, summary.TodayLoggedMinutes);
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

    [Fact]
    public void GetBusinessToday_PrzeliczaUtcNaStrefeBiznesowa()
    {
        // 22:30 UTC 6 sierpnia = 00:30 CEST 7 sierpnia — "dzisiaj" liczone w strefie
        // biznesowej, nie w UTC ani w czasie lokalnym serwera. Test potwierdza też,
        // że ID IANA "Europe/Warsaw" działa na Windows (konwersja przez ICU).
        var nowUtc = new DateTime(2026, 8, 6, 22, 30, 0, DateTimeKind.Utc);

        var today = SummaryService.GetBusinessToday(nowUtc, "Europe/Warsaw");

        Assert.Equal(new DateOnly(2026, 8, 7), today);
    }

    [Fact]
    public void GetBusinessToday_ZimaUzywaPrzesunieciaCET()
    {
        // Grudzień = CET (UTC+1): 23:30 UTC to już następny dzień lokalny.
        var nowUtc = new DateTime(2026, 12, 6, 23, 30, 0, DateTimeKind.Utc);

        var today = SummaryService.GetBusinessToday(nowUtc, "Europe/Warsaw");

        Assert.Equal(new DateOnly(2026, 12, 7), today);
    }

    private static TimeEntry CreateEntry(DateOnly entryDate, int minutes, DateTime? archivedAt = null)
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
            ArchivedAt = archivedAt,
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
