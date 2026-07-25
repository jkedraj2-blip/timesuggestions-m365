using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TimeSuggestions.Configuration;
using TimeSuggestions.Contracts;
using TimeSuggestions.Data;
using TimeSuggestions.Models;
using TimeSuggestions.Services;

namespace TimeSuggestions.Tests;

/// <summary>
/// Ochrona przed duplikatami na bazie SQLite in-memory — jedyne testy dotykające bazy,
/// bo dedup opiera się na indeksie unikalnym i zapytaniu o istniejące klucze.
/// </summary>
public sealed class SyncDeduplicationTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection connection;
    private readonly AppDbContext db;
    private readonly SyncService syncService;

    public SyncDeduplicationTests()
    {
        // Połączenie musi pozostać otwarte przez cały test — zamknięcie kasuje bazę in-memory.
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        db = new AppDbContext(options);
        db.Database.EnsureCreated();

        syncService = new SyncService(db, Options.Create(new SuggestionOptions()));
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }

    private static SyncRequest CreateRequestWithOneEvent() => new()
    {
        CalendarEvents =
        [
            new CalendarEventDto
            {
                Id = "event-1",
                Subject = "Spotkanie z Kowalski",
                StartDateTime = Now.AddDays(-1),
                EndDateTime = Now.AddDays(-1).AddHours(1),
            },
        ],
    };

    [Fact]
    public async Task SyncAsync_PowtornaSynchronizacjaNieTworzyDuplikatu()
    {
        var firstRun = await syncService.SyncAsync(CreateRequestWithOneEvent(), Now, CancellationToken.None);
        var secondRun = await syncService.SyncAsync(CreateRequestWithOneEvent(), Now, CancellationToken.None);

        Assert.Equal(1, firstRun.Created);
        Assert.Equal(0, secondRun.Created);
        Assert.Equal(1, secondRun.SkippedExisting);
        Assert.Equal(1, await db.Suggestions.CountAsync());
    }

    [Fact]
    public async Task SyncAsync_OdrzuconaSugestiaNieWracaPoPowtornejSynchronizacji()
    {
        await syncService.SyncAsync(CreateRequestWithOneEvent(), Now, CancellationToken.None);

        var suggestion = await db.Suggestions.SingleAsync();
        suggestion.Status = SuggestionStatus.Rejected;
        await db.SaveChangesAsync();

        var rerun = await syncService.SyncAsync(CreateRequestWithOneEvent(), Now, CancellationToken.None);

        Assert.Equal(0, rerun.Created);
        var suggestions = await db.Suggestions.ToListAsync();
        var survivor = Assert.Single(suggestions);
        Assert.Equal(SuggestionStatus.Rejected, survivor.Status);
    }

    [Fact]
    public async Task SyncAsync_TenSamPlikWDwaRozneDniTworzyDwieSugestie()
    {
        var request = new SyncRequest
        {
            DriveFiles =
            [
                new DriveFileDto { Id = "file-1", Name = "Umowa_NovaTech.docx", LastModifiedDateTime = Now.AddDays(-2), LastModifiedByMe = true },
                new DriveFileDto { Id = "file-1", Name = "Umowa_NovaTech.docx", LastModifiedDateTime = Now.AddDays(-1), LastModifiedByMe = true },
            ],
        };

        var result = await syncService.SyncAsync(request, Now, CancellationToken.None);

        Assert.Equal(2, result.Created);
    }
}
