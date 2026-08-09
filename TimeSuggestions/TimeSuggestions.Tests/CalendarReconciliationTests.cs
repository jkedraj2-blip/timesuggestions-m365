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
        // Kind=Unspecified jak w danych z Graph (czas lokalny bez sufiksu strefy).
        StartDateTime = DateTime.SpecifyKind(Now.AddDays(-daysAgo), DateTimeKind.Unspecified),
        EndDateTime = DateTime.SpecifyKind(Now.AddDays(-daysAgo).AddHours(1), DateTimeKind.Unspecified),
        Sensitivity = sensitivity,
        IsCancelled = isCancelled,
    };

    /// <summary>
    /// Domyślnie kompletny snapshot z zadeklarowanym zakresem pokrywającym okno
    /// backendu — dotychczasowe zachowanie rekonsyliacji.
    /// </summary>
    private static SyncRequest CalendarRequest(params CalendarEventDto[] events)
        => new() { CalendarEvents = [.. events], CalendarSnapshotComplete = true, CalendarSnapshotDaysBack = 7 };

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
    public async Task SyncAsync_NiekompletnySnapshotNieUsuwaZadnejOczekujacejSugestii()
    {
        await syncService.SyncAsync(
            CalendarRequest(CreateEvent("event-1", daysAgo: 1), CreateEvent("event-2", daysAgo: 2)),
            Now, CancellationToken.None);

        // Pusty payload BEZ deklaracji kompletności (np. przerwane stronicowanie) —
        // nieobecność spotkań niczego nie dowodzi, nic nie znika.
        var incomplete = new SyncRequest { CalendarSnapshotComplete = false };
        var report = await syncService.SyncAsync(incomplete, Now, CancellationToken.None);

        Assert.Equal(0, report.Removed);
        Assert.Equal(2, await db.Suggestions.CountAsync());
    }

    [Fact]
    public async Task SyncAsync_NiekompletnySnapshotNadalAktualizujeWMiejscu()
    {
        await syncService.SyncAsync(CalendarRequest(CreateEvent(daysAgo: 1)), Now, CancellationToken.None);

        // Aktualizacje w miejscu działają zawsze — tylko część destrukcyjna wymaga flagi.
        var incomplete = new SyncRequest { CalendarEvents = [CreateEvent(daysAgo: 2)] };
        var report = await syncService.SyncAsync(incomplete, Now, CancellationToken.None);

        Assert.Equal(1, report.Updated);
        Assert.Equal(0, report.Removed);
        var suggestion = await db.Suggestions.SingleAsync();
        Assert.Equal(DateOnly.FromDateTime(Now.AddDays(-2)), suggestion.EntryDate);
    }

    [Fact]
    public async Task SyncRequest_BezPolaCalendarSnapshotCompleteTraktowanyJakNiekompletny()
    {
        // Starszy klient nie przysyła pola — deserializacja daje false i sync
        // nie może destrukcyjnie rekonsyliować kalendarza.
        var json = """{"calendarEvents":[],"driveFiles":[]}""";
        var request = JsonSerializer.Deserialize<SyncRequest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(request);
        Assert.False(request.CalendarSnapshotComplete);

        await syncService.SyncAsync(CalendarRequest(CreateEvent()), Now, CancellationToken.None);
        var report = await syncService.SyncAsync(request, Now, CancellationToken.None);

        Assert.Equal(0, report.Removed);
        Assert.Equal(1, await db.Suggestions.CountAsync());
    }

    [Fact]
    public async Task SyncAsync_KompletnySnapshotBezZakresuNieUsuwaSugestii()
    {
        await syncService.SyncAsync(CalendarRequest(CreateEvent()), Now, CancellationToken.None);

        // Starszy klient: deklaruje kompletność, ale nie mówi, ile dni pobrał —
        // nie wiadomo, czego nieobecność dowodzi, więc nic nie znika.
        var withoutRange = new SyncRequest { CalendarSnapshotComplete = true };
        var report = await syncService.SyncAsync(withoutRange, Now, CancellationToken.None);

        Assert.Equal(0, report.Removed);
        Assert.Equal(1, await db.Suggestions.CountAsync());
    }

    [Fact]
    public async Task SyncAsync_KasowanieOgraniczoneDoZakresuZadeklarowanegoPrzezKlienta()
    {
        // Rozjazd konfiguracji: backend ma okno 14 dni, klient pobrał i zadeklarował 7.
        // Sugestia sprzed 10 dni leży poza zakresem klienta — jego snapshot niczego
        // o niej nie dowodzi i nie może jej skasować; sugestia sprzed 3 dni leży
        // w przecięciu i znika normalnie.
        var wideWindowService = new SyncService(
            db, Options.Create(new SuggestionOptions { SyncDaysBack = 14 }));
        await wideWindowService.SyncAsync(
            CalendarRequest(CreateEvent("event-old", daysAgo: 10), CreateEvent("event-recent", daysAgo: 3)),
            Now, CancellationToken.None);
        Assert.Equal(2, await db.Suggestions.CountAsync());

        var report = await wideWindowService.SyncAsync(
            new SyncRequest { CalendarSnapshotComplete = true, CalendarSnapshotDaysBack = 7 },
            Now, CancellationToken.None);

        Assert.Equal(1, report.Removed);
        var survivor = await db.Suggestions.SingleAsync();
        Assert.Equal("event-old", survivor.ExternalId);
    }

    [Fact]
    public async Task SyncAsync_NadpisanieOknaZZadaniaPoszerzaZakres()
    {
        // Spotkanie sprzed 10 dni: poza domyślnym oknem 7 dni, w oknie 14 dni.
        var oldEvent = CreateEvent("event-old", daysAgo: 10);

        var defaultWindowReport = await syncService.SyncAsync(
            CalendarRequest(oldEvent), Now, CancellationToken.None);
        Assert.Equal(0, defaultWindowReport.Created);
        Assert.Equal(1, defaultWindowReport.FilteredOut.OutsideWindow);
        Assert.Equal(7, defaultWindowReport.WindowDays);

        var request = CalendarRequest(oldEvent);
        request.SyncDaysBack = 14;
        request.CalendarSnapshotDaysBack = 14;
        var wideWindowReport = await syncService.SyncAsync(request, Now, CancellationToken.None);

        Assert.Equal(1, wideWindowReport.Created);
        Assert.Equal(14, wideWindowReport.WindowDays);
    }

    // --- Regresja archiwizacji: sync nie może dotykać zarchiwizowanych sugestii.
    // MergeWithExistingAsync operuje wyłącznie na Pending, a każdy status != Pending
    // liczy jako „rozstrzygnięty" — te testy przybijają to twierdzenie na stałe,
    // żeby przyszła zmiana SyncService nie zepsuła anty-nawrotu archiwum.

    [Fact]
    public async Task SyncAsync_ZarchiwizowaneSpotkanieWSnapshotcieNieTworzyDuplikatuAniNieJestOdswiezane()
    {
        await syncService.SyncAsync(CalendarRequest(CreateEvent(daysAgo: 1)), Now, CancellationToken.None);
        var suggestion = await db.Suggestions.SingleAsync();
        suggestion.Status = SuggestionStatus.Archived;
        await db.SaveChangesAsync();

        // To samo spotkanie, przesunięte na inny dzień — archiwum jest „lepkie" jak odrzucenie.
        var report = await syncService.SyncAsync(CalendarRequest(CreateEvent(daysAgo: 2)), Now, CancellationToken.None);

        Assert.Equal(0, report.Created);
        Assert.Equal(0, report.Updated);
        Assert.Equal(1, report.SkippedExisting);
        var survivor = await db.Suggestions.SingleAsync();
        Assert.Equal(SuggestionStatus.Archived, survivor.Status);
        Assert.Equal(DateOnly.FromDateTime(Now.AddDays(-1)), survivor.EntryDate);
    }

    [Fact]
    public async Task SyncAsync_ZarchiwizowaneSpotkanieNieobecneWKompletnymSnapshotcieNieJestKasowane()
    {
        await syncService.SyncAsync(CalendarRequest(CreateEvent(daysAgo: 1)), Now, CancellationToken.None);
        var suggestion = await db.Suggestions.SingleAsync();
        suggestion.Status = SuggestionStatus.Archived;
        await db.SaveChangesAsync();

        // Pusty kompletny snapshot: kasowane są wyłącznie Pending — archiwum zostaje.
        var report = await syncService.SyncAsync(CalendarRequest(), Now, CancellationToken.None);

        Assert.Equal(0, report.Removed);
        Assert.Equal(SuggestionStatus.Archived, (await db.Suggestions.SingleAsync()).Status);
    }

    [Fact]
    public async Task SyncAsync_ZarchiwizowanaDokumentowaNieDostajeDuplikatuAniOdswiezeniaTytulu()
    {
        await syncService.SyncAsync(DocumentRequest(CreateFile("file-1", "Umowa_NovaTech.docx")), Now, CancellationToken.None);
        var suggestion = await db.Suggestions.SingleAsync();
        suggestion.Status = SuggestionStatus.Archived;
        await db.SaveChangesAsync();

        // Ten sam plik pod nową nazwą w feedzie delta — odświeżane są tylko Pending.
        var report = await syncService.SyncAsync(
            DocumentRequest(CreateFile("file-1", "Umowa_NovaTech_v2.docx")), Now, CancellationToken.None);

        Assert.Equal(0, report.Created);
        Assert.Equal(0, report.Updated);
        Assert.Equal(1, report.SkippedExisting);
        Assert.Equal("Umowa_NovaTech.docx", (await db.Suggestions.SingleAsync()).Title);
    }

    [Fact]
    public async Task SyncAsync_TombstonePlikuNieKasujeZarchiwizowanejSugestii()
    {
        await syncService.SyncAsync(DocumentRequest(CreateFile("file-1")), Now, CancellationToken.None);
        var suggestion = await db.Suggestions.SingleAsync();
        suggestion.Status = SuggestionStatus.Archived;
        await db.SaveChangesAsync();

        var report = await syncService.SyncAsync(
            new SyncRequest { DeletedDriveFileIds = ["file-1"] }, Now, CancellationToken.None);

        Assert.Equal(0, report.Removed);
        Assert.Equal(SuggestionStatus.Archived, (await db.Suggestions.SingleAsync()).Status);
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
