using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TimeSuggestions.Data;
using TimeSuggestions.Models;
using TimeSuggestions.Services;

namespace TimeSuggestions.Tests;

/// <summary>
/// Agregacja osi czasu: liczniki per dzień i lista dnia. Kluczowy przypadek —
/// zatwierdzona sugestia NIE liczy się podwójnie (raz jako sugestia, raz jako wpis).
/// </summary>
public sealed class TimelineServiceTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 8, 6, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Day = new(2026, 8, 6);

    private readonly SqliteConnection connection;
    private readonly AppDbContext db;
    private readonly TimelineService timeline;

    public TimelineServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        db = new AppDbContext(options);
        db.Database.EnsureCreated();

        timeline = new TimelineService(db);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }

    private Suggestion SeedSuggestion(SuggestionStatus status, DateTime startedAt, int minutes = 30)
    {
        var suggestion = new Suggestion
        {
            Source = SuggestionSource.Calendar,
            ExternalId = $"event-{Guid.NewGuid()}",
            Title = "Spotkanie",
            StartedAt = startedAt,
            SessionAnchor = startedAt,
            EntryDate = DateOnly.FromDateTime(startedAt),
            DurationMinutes = minutes,
            ProposedDescription = "Spotkanie",
            Status = status,
            CreatedAt = Now,
        };
        db.Suggestions.Add(suggestion);
        db.SaveChanges();
        return suggestion;
    }

    private TimeEntry SeedEntry(DateTime startedAt, int minutes, DateTime? archivedAt = null, Suggestion? suggestion = null)
    {
        var entry = new TimeEntry
        {
            CaseId = 1,
            EntryDate = DateOnly.FromDateTime(startedAt),
            StartedAt = startedAt,
            EndedAt = startedAt.AddMinutes(minutes),
            DurationMinutes = minutes,
            Description = "Praca",
            CreatedFromSuggestion = true,
            Source = SuggestionSource.Calendar,
            Suggestions = suggestion is null ? [] : [suggestion],
            CreatedAt = Now,
            ArchivedAt = archivedAt,
        };
        db.TimeEntries.Add(entry);
        db.SaveChanges();
        return entry;
    }

    private static DateTime At(int day, int hour) => new(2026, 8, day, hour, 0, 0);

    [Fact]
    public async Task GetRangeAsync_ZatwierdzonaSugestiaLiczonaRazJakoWpis()
    {
        // Zatwierdzona sugestia + jej wpis: dzień ma pokazać 1 pozycję aktywną, zero
        // oczekujących — nie 2 pozycje.
        var approved = SeedSuggestion(SuggestionStatus.Approved, At(6, 9));
        SeedEntry(At(6, 9), 60, suggestion: approved);
        SeedSuggestion(SuggestionStatus.Pending, At(6, 11));
        SeedSuggestion(SuggestionStatus.Rejected, At(6, 13)); // odrzucona nie jest pozycją osi

        var days = await timeline.GetRangeAsync(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31), CancellationToken.None);

        var day = Assert.Single(days);
        Assert.Equal(Day, day.Date);
        Assert.Equal(1, day.PendingCount);
        Assert.Equal(1, day.ActiveCount);
        Assert.Equal(0, day.ArchivedCount);
    }

    [Fact]
    public async Task GetRangeAsync_GrupujePoDniachIRozdzielaArchiwum()
    {
        SeedEntry(At(4, 9), 60);
        SeedEntry(At(4, 11), 30, archivedAt: Now);
        SeedEntry(At(5, 9), 45);

        var days = await timeline.GetRangeAsync(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31), CancellationToken.None);

        Assert.Equal(2, days.Count);
        Assert.Equal(new DateOnly(2026, 8, 4), days[0].Date);
        Assert.Equal(1, days[0].ActiveCount);
        Assert.Equal(1, days[0].ArchivedCount);
        Assert.Equal(new DateOnly(2026, 8, 5), days[1].Date);
        Assert.Equal(1, days[1].ActiveCount);
    }

    [Fact]
    public async Task GetRangeAsync_NieZwracaDniSpozaZakresu()
    {
        SeedEntry(At(4, 9), 60);

        var days = await timeline.GetRangeAsync(new DateOnly(2026, 8, 5), new DateOnly(2026, 8, 31), CancellationToken.None);

        Assert.Empty(days);
    }

    [Fact]
    public async Task GetDayAsync_SortujePoGodzinieStartuIEtykietujeStatusy()
    {
        var approved = SeedSuggestion(SuggestionStatus.Approved, At(6, 14));
        SeedEntry(At(6, 14), 60, suggestion: approved);
        SeedEntry(At(6, 8), 30, archivedAt: Now);
        SeedSuggestion(SuggestionStatus.Pending, At(6, 11));

        var items = await timeline.GetDayAsync(Day, CancellationToken.None);

        Assert.Equal(3, items.Count);
        Assert.Equal(["archived", "pending", "active"], items.Select(item => item.Status));
        Assert.Equal([At(6, 8), At(6, 11), At(6, 14)], items.Select(item => item.StartedAt));
        Assert.Equal("suggestion", items[1].Type);
        Assert.Equal("timeEntry", items[2].Type);
    }
}
