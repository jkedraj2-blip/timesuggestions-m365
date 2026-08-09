using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TimeSuggestions.Data;
using TimeSuggestions.Models;
using TimeSuggestions.Services;

namespace TimeSuggestions.Tests;

/// <summary>
/// Archiwizacja hurtowa wpisów czasu: tylko aktywne wpisy w zakresie dat,
/// idempotentnie (pierwotna data rozliczenia jest wartością audytową).
/// </summary>
public sealed class ArchiveServiceTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 8, 9, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection connection;
    private readonly AppDbContext db;
    private readonly ArchiveService archiveService;

    public ArchiveServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        db = new AppDbContext(options);
        db.Database.EnsureCreated();

        archiveService = new ArchiveService(db);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }

    private TimeEntry SeedEntry(DateOnly entryDate, int minutes, DateTime? archivedAt = null)
    {
        // Wpis wymaga sugestii źródłowej (FK) — tworzymy minimalną parę.
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
            Suggestion = suggestion,
            CreatedAt = Now,
            ArchivedAt = archivedAt,
        };
        db.TimeEntries.Add(entry);
        db.SaveChanges();
        return entry;
    }

    [Fact]
    public async Task ArchiveTimeEntriesAsync_ArchiwizujeTylkoWpisyWZakresie()
    {
        var inRange = SeedEntry(new DateOnly(2026, 8, 5), minutes: 60);
        var beforeRange = SeedEntry(new DateOnly(2026, 8, 1), minutes: 30);
        var afterRange = SeedEntry(new DateOnly(2026, 8, 9), minutes: 45);

        var result = await archiveService.ArchiveTimeEntriesAsync(
            new DateOnly(2026, 8, 3), new DateOnly(2026, 8, 7), Now, CancellationToken.None);

        Assert.Equal(1, result.ArchivedCount);
        Assert.Equal(60, result.TotalMinutes);
        Assert.Equal(Now, (await db.TimeEntries.SingleAsync(entry => entry.Id == inRange.Id)).ArchivedAt);
        Assert.Null((await db.TimeEntries.SingleAsync(entry => entry.Id == beforeRange.Id)).ArchivedAt);
        Assert.Null((await db.TimeEntries.SingleAsync(entry => entry.Id == afterRange.Id)).ArchivedAt);
    }

    [Fact]
    public async Task ArchiveTimeEntriesAsync_ZakresDomknietyObejmujeObieGranice()
    {
        SeedEntry(new DateOnly(2026, 8, 3), minutes: 10);
        SeedEntry(new DateOnly(2026, 8, 7), minutes: 20);

        var result = await archiveService.ArchiveTimeEntriesAsync(
            new DateOnly(2026, 8, 3), new DateOnly(2026, 8, 7), Now, CancellationToken.None);

        Assert.Equal(2, result.ArchivedCount);
        Assert.Equal(30, result.TotalMinutes);
    }

    [Fact]
    public async Task ArchiveTimeEntriesAsync_PomijaJuzZarchiwizowaneINieNadpisujeZnacznika()
    {
        var originalArchivedAt = Now.AddDays(-3);
        var archived = SeedEntry(new DateOnly(2026, 8, 5), minutes: 60, archivedAt: originalArchivedAt);
        SeedEntry(new DateOnly(2026, 8, 5), minutes: 30);

        var result = await archiveService.ArchiveTimeEntriesAsync(
            new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 9), Now, CancellationToken.None);

        // Pierwotna data rozliczenia to wartość audytowa — ponowna archiwizacja jej nie rusza.
        Assert.Equal(1, result.ArchivedCount);
        Assert.Equal(30, result.TotalMinutes);
        Assert.Equal(originalArchivedAt, (await db.TimeEntries.SingleAsync(entry => entry.Id == archived.Id)).ArchivedAt);
    }

    [Fact]
    public async Task ArchiveTimeEntriesAsync_PonowneWywolanieNaTymSamymZakresieZwracaZero()
    {
        SeedEntry(new DateOnly(2026, 8, 5), minutes: 60);
        await archiveService.ArchiveTimeEntriesAsync(
            new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 9), Now, CancellationToken.None);

        var second = await archiveService.ArchiveTimeEntriesAsync(
            new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 9), Now.AddHours(1), CancellationToken.None);

        Assert.Equal(0, second.ArchivedCount);
        Assert.Equal(0, second.TotalMinutes);
        Assert.Equal(Now, (await db.TimeEntries.SingleAsync()).ArchivedAt);
    }

    [Fact]
    public async Task ArchiveTimeEntriesAsync_PustyZakresToSukcesZZerami()
    {
        var result = await archiveService.ArchiveTimeEntriesAsync(
            new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 9), Now, CancellationToken.None);

        Assert.Equal(0, result.ArchivedCount);
        Assert.Equal(0, result.TotalMinutes);
    }

    // --- Archiwizacja odrzuconych sugestii ---

    private Suggestion SeedSuggestion(SuggestionStatus status)
    {
        var suggestion = new Suggestion
        {
            Source = SuggestionSource.Calendar,
            ExternalId = $"event-{Guid.NewGuid()}",
            Title = "Spotkanie",
            StartedAt = Now.AddDays(-1),
            EntryDate = new DateOnly(2026, 8, 8),
            DurationMinutes = 60,
            ProposedDescription = "Spotkanie",
            Status = status,
            CreatedAt = Now,
        };
        db.Suggestions.Add(suggestion);
        db.SaveChanges();
        return suggestion;
    }

    [Fact]
    public async Task ArchiveRejectedSuggestionsAsync_ArchiwizujeWszystkieOdrzuconeNieDotykaPozostalych()
    {
        SeedSuggestion(SuggestionStatus.Rejected);
        SeedSuggestion(SuggestionStatus.Rejected);
        var pending = SeedSuggestion(SuggestionStatus.Pending);
        var approved = SeedSuggestion(SuggestionStatus.Approved);

        var result = await archiveService.ArchiveRejectedSuggestionsAsync(CancellationToken.None);

        Assert.Equal(2, result.ArchivedCount);
        Assert.Equal(2, await db.Suggestions.CountAsync(s => s.Status == SuggestionStatus.Archived));
        Assert.Equal(SuggestionStatus.Pending, (await db.Suggestions.SingleAsync(s => s.Id == pending.Id)).Status);
        Assert.Equal(SuggestionStatus.Approved, (await db.Suggestions.SingleAsync(s => s.Id == approved.Id)).Status);
    }

    [Fact]
    public async Task ArchiveRejectedSuggestionsAsync_PonowneWywolanieZwracaZero()
    {
        SeedSuggestion(SuggestionStatus.Rejected);
        await archiveService.ArchiveRejectedSuggestionsAsync(CancellationToken.None);

        var second = await archiveService.ArchiveRejectedSuggestionsAsync(CancellationToken.None);

        Assert.Equal(0, second.ArchivedCount);
    }

    [Fact]
    public async Task ArchiveSuggestionAsync_ArchiwizujeOdrzucona()
    {
        var rejected = SeedSuggestion(SuggestionStatus.Rejected);

        var result = await archiveService.ArchiveSuggestionAsync(rejected.Id, CancellationToken.None);

        Assert.Equal(ApprovalOutcome.Success, result.Outcome);
        Assert.Equal(SuggestionStatus.Archived, (await db.Suggestions.SingleAsync()).Status);
    }

    [Theory]
    [InlineData(SuggestionStatus.Pending)]
    [InlineData(SuggestionStatus.Approved)]
    [InlineData(SuggestionStatus.Archived)]
    public async Task ArchiveSuggestionAsync_OdmawiaGdyStatusInnyNizOdrzucona(SuggestionStatus status)
    {
        var suggestion = SeedSuggestion(status);

        var result = await archiveService.ArchiveSuggestionAsync(suggestion.Id, CancellationToken.None);

        Assert.Equal(ApprovalOutcome.SuggestionNotRejected, result.Outcome);
        Assert.Equal(status, (await db.Suggestions.SingleAsync()).Status);
    }

    [Fact]
    public async Task ArchiveSuggestionAsync_ZwracaBrakDlaNieistniejacejSugestii()
    {
        var result = await archiveService.ArchiveSuggestionAsync(999, CancellationToken.None);

        Assert.Equal(ApprovalOutcome.SuggestionNotFound, result.Outcome);
    }
}
