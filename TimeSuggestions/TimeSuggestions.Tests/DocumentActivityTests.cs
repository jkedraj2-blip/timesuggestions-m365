using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TimeSuggestions.Configuration;
using TimeSuggestions.Contracts;
using TimeSuggestions.Data;
using TimeSuggestions.Services;

namespace TimeSuggestions.Tests;

/// <summary>
/// Append-only dziennik historii (DocumentActivity) na bazie SQLite in-memory —
/// dedup faktów opiera się na indeksie unikalnym (ExternalId, VersionId, OccurredAt),
/// więc testy muszą przejść przez prawdziwy zapis, nie przez atrapę kontekstu.
/// </summary>
public sealed class DocumentActivityTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection connection;
    private readonly AppDbContext db;
    private readonly SyncService syncService;

    public DocumentActivityTests()
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

    /// <summary>
    /// Domyślnie znacznik pliku pokrywa się z najnowszą wersją — tak wygląda plik,
    /// nad którym nikt akurat nie pracuje. Testy próbkowania podają go jawnie
    /// (modifiedAt), bo to właśnie ROZJAZD obu wartości oznacza trwającą edycję.
    /// </summary>
    private static DriveFileDto CreateFile(
        string id = "file-1",
        List<DriveFileVersionDto>? versions = null,
        DateTime? modifiedAt = null) => new()
    {
        Id = id,
        Name = "Umowa_NovaTech.docx",
        LastModifiedDateTime = modifiedAt
            ?? versions?.Max(version => version.LastModifiedDateTime)
            ?? Now.AddHours(-2),
        LastModifiedByMe = true,
        Versions = versions,
    };

    private static DriveFileVersionDto CreateVersion(string versionId, DateTime occurredAt, long size = 1000) => new()
    {
        VersionId = versionId,
        LastModifiedDateTime = occurredAt,
        Size = size,
    };

    [Fact]
    public async Task SyncAsync_ZapisujeFaktyWersjiDoDziennika()
    {
        var request = new SyncRequest
        {
            DriveFiles =
            [
                CreateFile(versions:
                [
                    CreateVersion("1.0", Now.AddHours(-3), size: 500),
                    CreateVersion("2.0", Now.AddHours(-2), size: 800),
                ]),
            ],
        };

        var report = await syncService.SyncAsync(request, Now, CancellationToken.None);

        var activities = await db.DocumentActivities.OrderBy(activity => activity.OccurredAt).ToListAsync();
        Assert.Equal(2, activities.Count);
        Assert.All(activities, activity => Assert.Equal("file-1", activity.ExternalId));
        Assert.Equal(["1.0", "2.0"], activities.Select(activity => activity.VersionId));
        Assert.Equal([500L, 800L], activities.Select(activity => activity.Size));
        Assert.All(activities, activity => Assert.Equal(Now, activity.RecordedAt));
        Assert.Equal(2, report.Versions.NewActivities);
        Assert.Equal(1, report.Versions.FilesWithHistory);
        Assert.Equal(0, report.Versions.FilesWithoutHistory);
    }

    [Fact]
    public async Task SyncAsync_PowtornySyncNieDuplikujeFaktow()
    {
        SyncRequest CreateRequest() => new()
        {
            DriveFiles = [CreateFile(versions: [CreateVersion("1.0", Now.AddHours(-3))])],
        };

        await syncService.SyncAsync(CreateRequest(), Now, CancellationToken.None);
        var rerun = await syncService.SyncAsync(CreateRequest(), Now, CancellationToken.None);

        Assert.Equal(1, await db.DocumentActivities.CountAsync());
        Assert.Equal(0, rerun.Versions.NewActivities);
    }

    [Fact]
    public async Task SyncAsync_DuplikatWersjiWJednymZadaniuZapisywanyRaz()
    {
        var request = new SyncRequest
        {
            DriveFiles =
            [
                CreateFile(versions:
                [
                    CreateVersion("1.0", Now.AddHours(-3)),
                    CreateVersion("1.0", Now.AddHours(-3)),
                ]),
            ],
        };

        var report = await syncService.SyncAsync(request, Now, CancellationToken.None);

        Assert.Equal(1, await db.DocumentActivities.CountAsync());
        Assert.Equal(1, report.Versions.NewActivities);
    }

    [Fact]
    public async Task SyncAsync_FaktyZapisywaneTakzeDlaPlikuOdfiltrowanegoZSugestii()
    {
        // Plik zmodyfikowany przez kogoś innego nie tworzy sugestii, ale jego historia
        // to nadal fakty — dziennik jest źródłem prawdy, nie pochodną reguł sugestii.
        var file = CreateFile(versions: [CreateVersion("1.0", Now.AddHours(-3))]);
        file.LastModifiedByMe = false;
        var request = new SyncRequest { DriveFiles = [file] };

        var report = await syncService.SyncAsync(request, Now, CancellationToken.None);

        Assert.Equal(0, report.Created);
        Assert.Equal(1, await db.DocumentActivities.CountAsync());
    }

    [Fact]
    public async Task SyncAsync_PlikBezWersjiZachowujeFallbackIDoliczaSieDoRaportu()
    {
        // versions=null (błąd pobrania po stronie klienta) → sugestia dostaje domyślny
        // czas dokumentu, a raport pokazuje plik bez historii i licznik błędów klienta.
        var request = new SyncRequest
        {
            DriveFiles = [CreateFile(versions: null)],
            DriveFileVersionFetchErrors = 1,
        };

        var report = await syncService.SyncAsync(request, Now, CancellationToken.None);

        var suggestion = await db.Suggestions.SingleAsync();
        Assert.Equal(new SuggestionOptions().MinimumSessionMinutes, suggestion.DurationMinutes);
        Assert.Equal(0, await db.DocumentActivities.CountAsync());
        Assert.Equal(0, report.Versions.FilesWithHistory);
        Assert.Equal(1, report.Versions.FilesWithoutHistory);
        Assert.Equal(1, report.Versions.FetchErrors);
    }
}
