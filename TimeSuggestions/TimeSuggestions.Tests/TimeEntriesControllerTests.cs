using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TimeSuggestions.Configuration;
using TimeSuggestions.Contracts;
using TimeSuggestions.Controllers;
using TimeSuggestions.Data;
using TimeSuggestions.Models;
using TimeSuggestions.Services;

namespace TimeSuggestions.Tests;

/// <summary>
/// Widoki aktywne/archiwum i mapowanie wyników archiwizacji na kody HTTP —
/// zarchiwizowany wpis jest tylko do odczytu (DELETE → 409).
/// </summary>
public sealed class TimeEntriesControllerTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 8, 9, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection connection;
    private readonly AppDbContext db;
    private readonly TimeEntriesController controller;

    public TimeEntriesControllerTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        db = new AppDbContext(options);
        db.Database.EnsureCreated();

        controller = new TimeEntriesController(
            db,
            new ApprovalService(db),
            new ArchiveService(db),
            new TimeEntryOperationsService(db, Options.Create(new SuggestionOptions())));
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }

    private TimeEntry SeedEntry(DateOnly entryDate, int minutes, DateTime? archivedAt = null)
    {
        var suggestion = new Suggestion
        {
            Source = SuggestionSource.Calendar,
            ExternalId = $"event-{Guid.NewGuid()}",
            Title = "Spotkanie",
            StartedAt = Now.AddDays(-1),
            EntryDate = entryDate,
            DurationMinutes = minutes,
            ProposedDescription = "Spotkanie",
            Status = SuggestionStatus.Approved,
            CreatedAt = Now,
        };
        var entry = new TimeEntry
        {
            CaseId = 1,
            EntryDate = entryDate,
            DurationMinutes = minutes,
            Description = "Praca",
            CreatedFromSuggestion = true,
            Source = SuggestionSource.Calendar,
            Suggestions = [suggestion],
            CreatedAt = Now,
            ArchivedAt = archivedAt,
        };
        db.TimeEntries.Add(entry);
        db.SaveChanges();
        return entry;
    }

    [Fact]
    public async Task GetTimeEntries_DomyslnieZwracaTylkoAktywneZIchSuma()
    {
        SeedEntry(new DateOnly(2026, 8, 5), minutes: 60);
        SeedEntry(new DateOnly(2026, 8, 5), minutes: 30, archivedAt: Now);

        var result = await controller.GetTimeEntries(cancellationToken: CancellationToken.None);

        var response = Assert.IsType<TimeEntriesResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        // TotalMinutes dotyczy zwróconego widoku, nie całej tabeli.
        Assert.Equal(60, response.TotalMinutes);
        var entry = Assert.Single(Assert.Single(response.Days).Entries);
        Assert.Null(entry.ArchivedAt);
    }

    [Fact]
    public async Task GetTimeEntries_WidokArchiwumZwracaTylkoZarchiwizowaneZDataRozliczenia()
    {
        SeedEntry(new DateOnly(2026, 8, 5), minutes: 60);
        SeedEntry(new DateOnly(2026, 8, 5), minutes: 30, archivedAt: Now);

        var result = await controller.GetTimeEntries(archived: true, CancellationToken.None);

        var response = Assert.IsType<TimeEntriesResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(30, response.TotalMinutes);
        var entry = Assert.Single(Assert.Single(response.Days).Entries);
        Assert.Equal(Now, entry.ArchivedAt);
    }

    [Fact]
    public async Task Archive_ZwracaLiczbyDlaKomunikatuWUi()
    {
        SeedEntry(new DateOnly(2026, 8, 5), minutes: 60);
        SeedEntry(new DateOnly(2026, 8, 6), minutes: 45);
        var request = new ArchiveTimeEntriesRequest { From = new DateOnly(2026, 8, 1), To = new DateOnly(2026, 8, 9) };

        var result = await controller.Archive(request, CancellationToken.None);

        var archiveResult = Assert.IsType<ArchiveTimeEntriesResult>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(2, archiveResult.ArchivedCount);
        Assert.Equal(105, archiveResult.TotalMinutes);
    }

    [Fact]
    public async Task Delete_ZarchiwizowanegoWpisuZwraca409()
    {
        var entry = SeedEntry(new DateOnly(2026, 8, 5), minutes: 60, archivedAt: Now);

        var result = await controller.Delete(entry.Id, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Contains("rozliczony", conflict.Value!.ToString());
        Assert.Equal(1, await db.TimeEntries.CountAsync());
    }
}
