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
/// Bieżąca wersja pliku. Graph nie wydaje jej treści spod adresu wersji (odpowiada
/// 400 — to źródło błędu „Nie udało się pobrać treści wersji"), bo treścią bieżącej
/// wersji jest po prostu aktualna treść pliku. Chronologia musi więc jawnie wskazać,
/// która pozycja jest bieżąca. Osobno: część dysków opisuje najnowszą wersję etykietą
/// "current" zamiast numerem — etykieta opisuje POZYCJĘ, nie tożsamość, więc jako fakt
/// ma sens dopiero razem z momentem (klucz naturalny obejmuje OccurredAt).
/// </summary>
public sealed class CurrentVersionTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection connection;
    private readonly AppDbContext db;
    private readonly SyncService syncService;
    private readonly TimelineService timelineService;

    public CurrentVersionTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        db = new AppDbContext(options);
        db.Database.EnsureCreated();

        syncService = new SyncService(db, Options.Create(new SuggestionOptions()));
        timelineService = TestHelpers.Timeline(db);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }

    private static SyncRequest Request(params (string VersionId, DateTime OccurredAt)[] versions) => new()
    {
        DriveFiles =
        [
            new DriveFileDto
            {
                Id = "file-1",
                Name = "Umowa_NovaTech.docx",
                LastModifiedDateTime = versions.Max(version => version.OccurredAt),
                LastModifiedByMe = true,
                Versions = versions
                    .Select(version => new DriveFileVersionDto
                    {
                        VersionId = version.VersionId,
                        LastModifiedDateTime = version.OccurredAt,
                        Size = 1000,
                    })
                    .ToList(),
            },
        ],
    };

    [Fact]
    public async Task Chronologia_OznaczaJakoBiezacaWylacznieNajnowszaPozycje()
    {
        await syncService.SyncAsync(
            Request(("1.0", Now.AddMinutes(-60)), ("2.0", Now.AddMinutes(-50)), ("3.0", Now.AddMinutes(-40))),
            Now,
            CancellationToken.None);

        var chronology = await timelineService.GetDocumentActivityAsync("file-1", CancellationToken.None);

        Assert.Equal(3, chronology.Count);
        Assert.False(chronology[0].IsCurrent);
        Assert.False(chronology[1].IsCurrent);
        // Tylko ostatnia — po tym znaczniku frontend wie, że treść trzeba wziąć
        // z endpointu pliku, a nie wersji.
        Assert.True(chronology[2].IsCurrent);
    }

    /// <summary>
    /// Sedno błędu 400 invalidRequest przy podglądzie zmian: wersja, której Word online
    /// jeszcze nie zapieczętował, trafia do dziennika wiele razy (ten sam numer, kolejne
    /// momenty). Znacznik bieżącej wersji musi objąć KAŻDĄ jej próbkę — inaczej
    /// wcześniejsze próbki dostają adres /versions/{id}/content, spod którego Graph
    /// treści bieżącej wersji nie wydaje.
    /// </summary>
    [Fact]
    public async Task Chronologia_ObejmujeZnacznikiemBiezacejWszystkieProbkiTejSamejWersji()
    {
        await syncService.SyncAsync(
            Request(("4.0", Now.AddMinutes(-30)), ("5.0", Now.AddMinutes(-20))),
            Now,
            CancellationToken.None);
        // Kolejne synchronizacje w trakcie nieprzerwanej pracy: numer wersji stoi,
        // przesuwa się tylko jej znacznik czasu.
        await syncService.SyncAsync(
            Request(("4.0", Now.AddMinutes(-30)), ("5.0", Now.AddMinutes(-10))),
            Now,
            CancellationToken.None);
        await syncService.SyncAsync(
            Request(("4.0", Now.AddMinutes(-30)), ("5.0", Now.AddMinutes(-5))),
            Now,
            CancellationToken.None);

        var chronology = await timelineService.GetDocumentActivityAsync("file-1", CancellationToken.None);

        Assert.Equal(4, chronology.Count);
        Assert.False(chronology[0].IsCurrent); // 4.0 — wersja historyczna
        Assert.True(chronology[1].IsCurrent);
        Assert.True(chronology[2].IsCurrent);
        Assert.True(chronology[3].IsCurrent);
    }

    /// <summary>
    /// Dwie próbki tej samej wersji nie mają czego pokazać: OneDrive trzyma jeden
    /// snapshot na numer wersji, stanu pośredniego nigdy nie zapisał.
    /// </summary>
    [Fact]
    public async Task Chronologia_OznaczaPowtorzenieTegoSamegoNumeruWersji()
    {
        await syncService.SyncAsync(
            Request(("4.0", Now.AddMinutes(-30)), ("5.0", Now.AddMinutes(-20))),
            Now,
            CancellationToken.None);
        await syncService.SyncAsync(
            Request(("4.0", Now.AddMinutes(-30)), ("5.0", Now.AddMinutes(-10))),
            Now,
            CancellationToken.None);

        var chronology = await timelineService.GetDocumentActivityAsync("file-1", CancellationToken.None);

        Assert.False(chronology[0].IsSameVersionAsPrevious); // pierwsza pozycja nie ma poprzedniej
        Assert.False(chronology[1].IsSameVersionAsPrevious); // 4.0 → 5.0: jest co porównać
        Assert.True(chronology[2].IsSameVersionAsPrevious);  // 5.0 → 5.0: nie ma
    }

    [Fact]
    public async Task Chronologia_PustaHistoriaNieOznaczaNiczego()
    {
        var chronology = await timelineService.GetDocumentActivityAsync("nieznany", CancellationToken.None);

        Assert.Empty(chronology);
    }

    [Fact]
    public async Task Sync_EtykietaCurrentJestFaktemILiczySieDoSesji()
    {
        await syncService.SyncAsync(
            Request(("1.0", Now.AddMinutes(-60)), ("current", Now.AddMinutes(-40))),
            Now,
            CancellationToken.None);

        var facts = await db.DocumentActivities.OrderBy(activity => activity.OccurredAt).ToListAsync();
        Assert.Equal(["1.0", "current"], facts.Select(activity => activity.VersionId));

        // Sesja zna oba zapisy: 20 min miedzy pierwszym a ostatnim.
        var suggestion = await db.Suggestions.SingleAsync();
        Assert.Equal(20, suggestion.DurationMinutes);
        Assert.False(suggestion.NeedsTimeReview);
    }

    /// <summary>
    /// Etykieta "current" przeskakuje z zapisu na zapis, ale MOMENT jest niezmienny.
    /// Ten sam zapis widziany raz pod etykietą, raz pod numerem, zostaje jednym
    /// punktem sesji — inaczej każdy sync sztucznie wydłużałby pracę o duplikat.
    /// </summary>
    [Fact]
    public async Task Sync_TenSamZapisPodEtykietaIPodNumeremNieDuplikujeSieWCzasie()
    {
        await syncService.SyncAsync(
            Request(("current", Now.AddMinutes(-60))), Now, CancellationToken.None);

        await syncService.SyncAsync(
            Request(("1.0", Now.AddMinutes(-60)), ("current", Now.AddMinutes(-40))),
            Now,
            CancellationToken.None);

        var moments = await db.DocumentActivities
            .Select(activity => activity.OccurredAt)
            .Distinct()
            .ToListAsync();
        Assert.Equal(2, moments.Count);

        var suggestion = await db.Suggestions.SingleAsync();
        Assert.Equal(20, suggestion.DurationMinutes);
    }

    /// <summary>
    /// Sedno naprawy ciagłości: Word online przez cały czas pracy dopisuje do TEJ SAMEJ
    /// wersji i przesuwa wyłącznie lastModifiedDateTime pliku. Kolejny sync musi zamienić
    /// ten przesunięty znacznik w nowy punkt sesji, a nie odrzucić go jako duplikat —
    /// inaczej godziny nieprzerwanej pracy zostawiają jeden punkt i prośbę o wpisanie
    /// czasu ręcznie.
    /// </summary>
    [Fact]
    public async Task Sync_PrzesunietyZnacznikPlikuWydluzaSesjeMimoJednejWersji()
    {
        await syncService.SyncAsync(
            Request(("1.0", Now.AddMinutes(-60))), Now, CancellationToken.None);

        // Praca trwa: lista wersji stoi w miejscu, znacznik pliku poszedł o 20 min naprzód.
        var later = Request(("1.0", Now.AddMinutes(-60)));
        later.DriveFiles[0].LastModifiedDateTime = Now.AddMinutes(-40);
        await syncService.SyncAsync(later, Now, CancellationToken.None);

        var suggestion = await db.Suggestions.SingleAsync();
        // Dwa punkty w czasie dajace 20 min pracy, bez proszenia o czas.
        Assert.Equal(20, suggestion.DurationMinutes);
        Assert.False(suggestion.NeedsTimeReview);
        Assert.Equal(Now.AddMinutes(-40), suggestion.LastActivityAt);
    }

    [Fact]
    public async Task Sync_JedenZapisDajeMinimumIOznaczenieCzasuDoUzupelnienia()
    {
        await syncService.SyncAsync(
            Request(("1.0", Now.AddMinutes(-60))), Now, CancellationToken.None);

        var suggestion = await db.Suggestions.SingleAsync();
        // Bez rozbiegu: z jednego zapisu nie wynika, ile trwała praca.
        Assert.Equal(5, suggestion.DurationMinutes);
        Assert.True(suggestion.NeedsTimeReview);
    }
}
