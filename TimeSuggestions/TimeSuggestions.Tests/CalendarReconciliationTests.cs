using System.Text.Json;
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
/// Rekonsyliacja kalendarza (pełny snapshot okna) i tombstone'y dokumentów.
/// Kalendarz: przeniesione spotkanie aktualizuje istniejącą sugestię w miejscu,
/// zniknięte/nierozliczalne usuwa TYLKO oczekujące. Dokumenty: delta jest
/// przyrostowa, więc czyszczą je wyłącznie jawne tombstone'y.
/// </summary>
public sealed class CalendarReconciliationTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection connection;
    private readonly AppDbContext db;
    private readonly SyncService syncService;

    public CalendarReconciliationTests()
    {
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

    private static CalendarEventDto CreateEvent(
        string id = "event-1",
        string subject = "Spotkanie robocze",
        int daysAgo = 1,
        string? sensitivity = "normal",
        bool isCancelled = false) => new()
    {
        Id = id,
        Subject = subject,
        StartDateTime = Now.AddDays(-daysAgo),
        EndDateTime = Now.AddDays(-daysAgo).AddHours(1),
        Sensitivity = sensitivity,
        IsCancelled = isCancelled,
    };

    private static SyncRequest CalendarRequest(params CalendarEventDto[] events)
        => new() { CalendarEvents = [.. events] };

    private static SyncRequest DocumentRequest(params DriveFileDto[] files)
        => new() { DriveFiles = [.. files] };

    private static DriveFileDto CreateFile(string id = "file-1", string name = "Umowa_NovaTech.docx") => new()
    {
        Id = id,
        Name = name,
        LastModifiedDateTime = Now.AddDays(-1),
        LastModifiedByMe = true,
    };

    [Fact]
    public async Task SyncAsync_PrzeniesioneSpotkanieAktualizujeIstniejacaSugestieWMiejscu()
    {
        await syncService.SyncAsync(CalendarRequest(CreateEvent(daysAgo: 1)), Now, CancellationToken.None);

        // To samo spotkanie przeniesione na inny dzień — bez tworzenia "ducha" pod starą datą.
        var report = await syncService.SyncAsync(CalendarRequest(CreateEvent(daysAgo: 2)), Now, CancellationToken.None);

        Assert.Equal(0, report.Created);
        Assert.Equal(1, report.Updated);
        Assert.Equal(0, report.Removed);
        var suggestion = await db.Suggestions.SingleAsync();
        Assert.Equal(DateOnly.FromDateTime(Now.AddDays(-2)), suggestion.EntryDate);
        Assert.Equal(Now.AddDays(-2), suggestion.StartedAt);
    }

    [Fact]
    public async Task SyncAsync_PrzeniesioneOdrzuconeSpotkanieNieWracaPodNowaData()
    {
        await syncService.SyncAsync(CalendarRequest(CreateEvent(daysAgo: 1)), Now, CancellationToken.None);
        var suggestion = await db.Suggestions.SingleAsync();
        suggestion.Status = SuggestionStatus.Rejected;
        await db.SaveChangesAsync();

        // Odrzucenie jest "lepkie" per spotkanie — zmiana daty nie przywraca sugestii.
        var report = await syncService.SyncAsync(CalendarRequest(CreateEvent(daysAgo: 2)), Now, CancellationToken.None);

        Assert.Equal(0, report.Created);
        Assert.Equal(1, report.SkippedExisting);
        var survivor = await db.Suggestions.SingleAsync();
        Assert.Equal(SuggestionStatus.Rejected, survivor.Status);
        Assert.Equal(DateOnly.FromDateTime(Now.AddDays(-1)), survivor.EntryDate);
    }

    [Fact]
    public async Task SyncAsync_PrzeniesioneZatwierdzoneSpotkanieNieTworzyDuplikatu()
    {
        await syncService.SyncAsync(CalendarRequest(CreateEvent(daysAgo: 1)), Now, CancellationToken.None);
        var suggestion = await db.Suggestions.SingleAsync();
        suggestion.Status = SuggestionStatus.Approved;
        await db.SaveChangesAsync();

        var report = await syncService.SyncAsync(CalendarRequest(CreateEvent(daysAgo: 2)), Now, CancellationToken.None);

        Assert.Equal(0, report.Created);
        Assert.Equal(1, report.SkippedExisting);
        Assert.Equal(1, await db.Suggestions.CountAsync());
    }

    [Fact]
    public async Task SyncAsync_SpotkanieZnikleZeSnapshotuUsuwaTylkoPending()
    {
        await syncService.SyncAsync(
            CalendarRequest(CreateEvent("event-approved", daysAgo: 2), CreateEvent("event-pending", daysAgo: 1)),
            Now, CancellationToken.None);
        var approved = await db.Suggestions.SingleAsync(suggestion => suggestion.ExternalId == "event-approved");
        approved.Status = SuggestionStatus.Approved;
        await db.SaveChangesAsync();

        // Pusty snapshot kalendarza: oba spotkania zniknęły z Graph.
        var report = await syncService.SyncAsync(CalendarRequest(), Now, CancellationToken.None);

        Assert.Equal(1, report.Removed);
        var survivor = await db.Suggestions.SingleAsync();
        Assert.Equal("event-approved", survivor.ExternalId);
    }

    [Fact]
    public async Task SyncAsync_SpotkanieAnulowaneLubSprywatyzowaneUsuwaPending()
    {
        await syncService.SyncAsync(
            CalendarRequest(CreateEvent("event-cancelled", daysAgo: 1), CreateEvent("event-private", daysAgo: 2)),
            Now, CancellationToken.None);
        Assert.Equal(2, await db.Suggestions.CountAsync());

        // Spotkania nadal istnieją w Graph, ale przestały być rozliczalne —
        // traktowane jak usunięte.
        var report = await syncService.SyncAsync(
            CalendarRequest(
                CreateEvent("event-cancelled", daysAgo: 1, isCancelled: true),
                CreateEvent("event-private", daysAgo: 2, sensitivity: "private")),
            Now, CancellationToken.None);

        Assert.Equal(2, report.Removed);
        Assert.Equal(0, await db.Suggestions.CountAsync());
    }

    [Fact]
    public async Task SyncAsync_TombstoneDokumentuUsuwaTylkoPending()
    {
        await syncService.SyncAsync(
            DocumentRequest(CreateFile("file-pending"), CreateFile("file-approved", "Raport_Kowalski.xlsx")),
            Now, CancellationToken.None);
        var approved = await db.Suggestions.SingleAsync(suggestion => suggestion.ExternalId == "file-approved");
        approved.Status = SuggestionStatus.Approved;
        await db.SaveChangesAsync();

        var request = new SyncRequest { DeletedDriveFileIds = ["file-pending", "file-approved"] };
        var report = await syncService.SyncAsync(request, Now, CancellationToken.None);

        Assert.Equal(1, report.Removed);
        var survivor = await db.Suggestions.SingleAsync();
        Assert.Equal("file-approved", survivor.ExternalId);
        Assert.Equal(SuggestionStatus.Approved, survivor.Status);
    }

    [Fact]
    public async Task SyncAsync_NieobecnoscDokumentuWDelcieNieUsuwaSugestii()
    {
        // Delta jest przyrostowa — brak pliku w feedzie niczego nie dowodzi.
        await syncService.SyncAsync(DocumentRequest(CreateFile()), Now, CancellationToken.None);

        var report = await syncService.SyncAsync(DocumentRequest(), Now, CancellationToken.None);

        Assert.Equal(0, report.Removed);
        Assert.Equal(1, await db.Suggestions.CountAsync());
    }

    [Fact]
    public async Task SyncRequest_BezDeletedDriveFileIdsDzialaJakDotychczas()
    {
        // Stary kontrakt (frontend sprzed tombstone'ów) — pole nieobecne w JSON
        // deserializuje się do pustej listy i nic nie usuwa.
        var json = """{"calendarEvents":[],"driveFiles":[]}""";
        var request = JsonSerializer.Deserialize<SyncRequest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(request);
        Assert.NotNull(request.DeletedDriveFileIds);
        Assert.Empty(request.DeletedDriveFileIds);

        await syncService.SyncAsync(DocumentRequest(CreateFile()), Now, CancellationToken.None);
        var report = await syncService.SyncAsync(request, Now, CancellationToken.None);

        Assert.Equal(0, report.Removed);
        Assert.Equal(1, await db.Suggestions.CountAsync());
    }
}
