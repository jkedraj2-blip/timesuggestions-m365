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
/// Integracja silnika sesji z synchronizacją: sugestie per sesja zamiast per dzień,
/// stabilność kotwicy między syncami, scalanie sesji "w dół" po zaległej wersji
/// i przejęcie dawnej sugestii fallbackowej, gdy plik zyskał historię wersji.
/// </summary>
public sealed class DocumentSessionSyncTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection connection;
    private readonly AppDbContext db;
    private readonly SyncService syncService;

    public DocumentSessionSyncTests()
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

    private static SyncRequest RequestWithVersions(params DateTime[] versionTimesUtc) => new()
    {
        DriveFiles =
        [
            new DriveFileDto
            {
                Id = "file-1",
                Name = "Umowa_NovaTech.docx",
                LastModifiedDateTime = versionTimesUtc.Max(),
                LastModifiedByMe = true,
                // Id wersji pochodne od czasu — stabilne między wywołaniami w teście,
                // jak realne id wersji Graph dla tej samej wersji pliku.
                Versions = versionTimesUtc
                    .Select(occurredAt => new DriveFileVersionDto
                    {
                        VersionId = $"v-{occurredAt:yyyyMMddHHmmss}",
                        LastModifiedDateTime = occurredAt,
                        Size = 1000,
                    })
                    .ToList(),
            },
        ],
    };

    [Fact]
    public async Task Sync_DwieSesjeTegoSamegoDniaDajaDwieSugestie()
    {
        // Rano i po południu — przerwa znacznie powyżej progu 30 min.
        var request = RequestWithVersions(
            Now.AddHours(-5), Now.AddHours(-5).AddMinutes(10),
            Now.AddHours(-1), Now.AddHours(-1).AddMinutes(20));

        var report = await syncService.SyncAsync(request, Now, CancellationToken.None);

        Assert.Equal(2, report.Created);
        var suggestions = await db.Suggestions.OrderBy(s => s.SessionAnchor).ToListAsync();
        Assert.Equal(2, suggestions.Count);
        Assert.All(suggestions, s => Assert.Equal("file-1", s.ExternalId));
        // Czas z sesji (10 i 20 min miedzy zapisami), nie sztywne 30 min.
        Assert.Equal(10, suggestions[0].DurationMinutes);
        Assert.Equal(20, suggestions[1].DurationMinutes);
    }

    [Fact]
    public async Task Sync_SesjaRosnacaNaKoncuAktualizujeSugestieZamiastDuplikowac()
    {
        await syncService.SyncAsync(
            RequestWithVersions(Now.AddMinutes(-60), Now.AddMinutes(-50)), Now, CancellationToken.None);

        // Nowa wersja 10 min po ostatniej: ta sama sesja, kotwica bez zmian.
        var rerun = await syncService.SyncAsync(
            RequestWithVersions(Now.AddMinutes(-60), Now.AddMinutes(-50), Now.AddMinutes(-40)),
            Now, CancellationToken.None);

        Assert.Equal(0, rerun.Created);
        Assert.Equal(1, rerun.Updated);
        var suggestion = await db.Suggestions.SingleAsync();
        Assert.Equal(Now.AddMinutes(-60), suggestion.SessionAnchor);
        Assert.Equal(20, suggestion.DurationMinutes); // 20 min miedzy zapisami
    }

    [Fact]
    public async Task Sync_ZaleglaWersjaStarszaNizKotwicaScalaSesjeWDolIPrzesuwaKotwice()
    {
        await syncService.SyncAsync(
            RequestWithVersions(Now.AddMinutes(-60), Now.AddMinutes(-50)), Now, CancellationToken.None);
        var originalAnchor = (await db.Suggestions.SingleAsync()).SessionAnchor;

        // Sync doniósł wersję 10 min PRZED dotychczasową kotwicą (bliżej niż próg sesji):
        // sesja scalana w dół — stara sugestia aktualizowana, kotwica przesunięta.
        var rerun = await syncService.SyncAsync(
            RequestWithVersions(Now.AddMinutes(-70), Now.AddMinutes(-60), Now.AddMinutes(-50)),
            Now, CancellationToken.None);

        Assert.Equal(0, rerun.Created);
        Assert.Equal(1, rerun.Updated);
        var suggestion = await db.Suggestions.SingleAsync();
        Assert.Equal(Now.AddMinutes(-70), suggestion.SessionAnchor);
        Assert.True(suggestion.SessionAnchor < originalAnchor);
        Assert.Equal(20, suggestion.DurationMinutes);
    }

    [Fact]
    public async Task Sync_ZaleglaWersjaMostkujacaDwieSesjeScalaJeWJedna()
    {
        // Dwie sesje odległe o 50 min...
        await syncService.SyncAsync(
            RequestWithVersions(Now.AddMinutes(-80), Now.AddMinutes(-30)), Now, CancellationToken.None);
        Assert.Equal(2, await db.Suggestions.CountAsync());

        // ...a zaległa wersja w środku (25 min od obu) mostkuje je w jedną sesję:
        // jedna sugestia zaktualizowana, druga (nadmiarowa, oczekująca) znika.
        var rerun = await syncService.SyncAsync(
            RequestWithVersions(Now.AddMinutes(-80), Now.AddMinutes(-55), Now.AddMinutes(-30)),
            Now, CancellationToken.None);

        Assert.Equal(0, rerun.Created);
        Assert.Equal(1, rerun.Removed);
        var suggestion = await db.Suggestions.SingleAsync();
        Assert.Equal(Now.AddMinutes(-80), suggestion.SessionAnchor);
        // 50 min wersji + 10 rozbiegu; dwie wykryte przerwy po 25 min.
        Assert.Equal(50, suggestion.DurationMinutes);
        Assert.Equal(2, DetectedGaps.Deserialize(suggestion.DetectedGapsJson).Count);
    }

    [Fact]
    public async Task Sync_SugestiaFallbackowaJestPrzejmowanaGdyPlikZyskaHistorie()
    {
        // Pierwszy sync bez wersji (np. błąd Graph) → sugestia fallbackowa: samo minimum.
        var withoutVersions = new SyncRequest
        {
            DriveFiles =
            [
                new DriveFileDto
                {
                    Id = "file-1",
                    Name = "Umowa_NovaTech.docx",
                    LastModifiedDateTime = Now.AddHours(-1),
                    LastModifiedByMe = true,
                },
            ],
        };
        await syncService.SyncAsync(withoutVersions, Now, CancellationToken.None);
        var fallback = await db.Suggestions.SingleAsync();
        Assert.Equal(new SuggestionOptions().MinimumSessionMinutes, fallback.DurationMinutes);
        Assert.True(fallback.NeedsTimeReview);

        // Drugi sync z historią: sugestia sesyjna przejmuje fallbackową w miejscu
        // (bez duplikatu), kotwica przechodzi na czas pierwszej wersji.
        var rerun = await syncService.SyncAsync(
            RequestWithVersions(Now.AddMinutes(-60), Now.AddMinutes(-40)), Now, CancellationToken.None);

        Assert.Equal(0, rerun.Created);
        Assert.Equal(1, rerun.Updated);
        var suggestion = await db.Suggestions.SingleAsync();
        Assert.Equal(Now.AddMinutes(-60), suggestion.SessionAnchor);
        Assert.Equal(20, suggestion.DurationMinutes); // 20 min miedzy zapisami
        Assert.Single(DetectedGaps.Deserialize(suggestion.DetectedGapsJson)); // luka 20 min
    }

    [Fact]
    public async Task Sync_RozstrzygnietaSesjaNieWracaPodNowaKotwica()
    {
        await syncService.SyncAsync(
            RequestWithVersions(Now.AddMinutes(-60), Now.AddMinutes(-50)), Now, CancellationToken.None);
        var suggestion = await db.Suggestions.SingleAsync();
        suggestion.Status = SuggestionStatus.Rejected;
        await db.SaveChangesAsync();

        // Zaległa wersja przesuwa kotwicę sesji, ale sesja została już odrzucona —
        // odrzucenie jest lepkie także wobec ruchu kotwicy.
        var rerun = await syncService.SyncAsync(
            RequestWithVersions(Now.AddMinutes(-70), Now.AddMinutes(-60), Now.AddMinutes(-50)),
            Now, CancellationToken.None);

        Assert.Equal(0, rerun.Created);
        var survivor = await db.Suggestions.SingleAsync();
        Assert.Equal(SuggestionStatus.Rejected, survivor.Status);
    }

    /// <summary>
    /// Po zatwierdzeniu sugestii praca nad tym samym plikiem trwa dalej.    /// <summary>
    /// Po zatwierdzeniu sugestii praca nad tym samym plikiem trwa dalej. Zatwierdzony
    /// przedział jest rozliczony wpisem, więc kolejne zapisy muszą dać NOWĄ sugestię —
    /// wcześniej ginęły, bo sesja odtwarzała się pod kotwicą zajętą przez zatwierdzoną
    /// sugestię i merge ją pomijał.
    /// </summary>
    [Fact]
    public async Task Sync_DalszaEdycjaPoZatwierdzeniuTworzyNowaSugestie()
    {
        await syncService.SyncAsync(
            RequestWithVersions(Now.AddMinutes(-120), Now.AddMinutes(-110)), Now, CancellationToken.None);

        var approved = await db.Suggestions.SingleAsync();
        approved.Status = SuggestionStatus.Approved;
        await db.SaveChangesAsync();

        // Praca wraca po godzinie — nowa sesja, poza zasięgiem zatwierdzonej.
        var rerun = await syncService.SyncAsync(
            RequestWithVersions(
                Now.AddMinutes(-120), Now.AddMinutes(-110), Now.AddMinutes(-40), Now.AddMinutes(-30)),
            Now,
            CancellationToken.None);

        Assert.Equal(1, rerun.Created);
        var fresh = await db.Suggestions.SingleAsync(suggestion => suggestion.Status == SuggestionStatus.Pending);
        Assert.Equal(Now.AddMinutes(-40), fresh.SessionAnchor);
        Assert.Equal(Now.AddMinutes(-30), fresh.LastActivityAt);
        // Zatwierdzona zostaje nietknięta — czasu rozliczonego nie przeliczamy.
        Assert.Equal(10, (await db.Suggestions.SingleAsync(s => s.Status == SuggestionStatus.Approved)).DurationMinutes);
    }

    /// <summary>
    /// Ręczna poprawka czasu (scalenie, doliczona luka) zamraża sugestię: sync nie
    /// przelicza jej z historii wersji i nie odtwarza sesji z jej zasięgu.
    /// </summary>
    [Fact]
    public async Task Sync_NiePrzeliczaSugestiiPoprawionejRecznie()
    {
        await syncService.SyncAsync(
            RequestWithVersions(Now.AddMinutes(-60), Now.AddMinutes(-50)), Now, CancellationToken.None);

        var suggestion = await db.Suggestions.SingleAsync();
        suggestion.DurationMinutes = 90;
        suggestion.IsUserAdjusted = true;
        await db.SaveChangesAsync();

        var rerun = await syncService.SyncAsync(
            RequestWithVersions(Now.AddMinutes(-60), Now.AddMinutes(-50)), Now, CancellationToken.None);

        Assert.Equal(0, rerun.Created);
        Assert.Equal(0, rerun.Updated);
        Assert.Equal(90, (await db.Suggestions.SingleAsync()).DurationMinutes);
    }

    [Fact]
    public async Task Sync_DziennikPamietaWersjeWycieteZRetencjiOneDrive()
    {
        // Pierwszy sync zapisuje fakty do dziennika.
        await syncService.SyncAsync(
            RequestWithVersions(Now.AddMinutes(-60), Now.AddMinutes(-50)), Now, CancellationToken.None);

        // Drugi sync: OneDrive "zapomniał" starszą wersję (retencja) — payload niesie
        // tylko nowszą, ale sesja liczy się z UNII dziennika i payloadu.
        var trimmed = RequestWithVersions(Now.AddMinutes(-40));
        var rerun = await syncService.SyncAsync(trimmed, Now, CancellationToken.None);

        Assert.Equal(0, rerun.Created);
        var suggestion = await db.Suggestions.SingleAsync();
        Assert.Equal(Now.AddMinutes(-60), suggestion.SessionAnchor);
        Assert.Equal(20, suggestion.DurationMinutes); // pelna sesja: 20 min miedzy zapisami
    }
}
