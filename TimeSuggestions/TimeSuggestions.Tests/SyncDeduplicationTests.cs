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

    private static SyncRequest CreateRequestWithOneEvent(string subject = "Spotkanie z Kowalski") => new()
    {
        CalendarEvents =
        [
            new CalendarEventDto
            {
                Id = "event-1",
                Subject = subject,
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
    public async Task SyncAsync_OdswiezaOczekujacaSugestiePoZmianieTytulu()
    {
        // Tytuł bez dopasowania → sugestia "bez sprawy".
        await syncService.SyncAsync(CreateRequestWithOneEvent("Spotkanie robocze"), Now, CancellationToken.None);

        // Ten sam obiekt Graph (to samo ID), nowy tytuł z nazwą klienta.
        var rerun = await syncService.SyncAsync(CreateRequestWithOneEvent("Spotkanie z Kowalski"), Now, CancellationToken.None);

        Assert.Equal(0, rerun.Created);
        Assert.Equal(1, rerun.Updated);
        var suggestion = await db.Suggestions.SingleAsync();
        Assert.Equal("Spotkanie z Kowalski", suggestion.Title);
        Assert.Equal(1, suggestion.CaseId); // ponowne dopasowanie do sprawy z seedu
    }

    [Fact]
    public async Task SyncAsync_NieOdswiezaOdrzuconejAniZatwierdzonejSugestii()
    {
        await syncService.SyncAsync(CreateRequestWithOneEvent("Spotkanie robocze"), Now, CancellationToken.None);
        var suggestion = await db.Suggestions.SingleAsync();
        suggestion.Status = SuggestionStatus.Rejected;
        await db.SaveChangesAsync();

        var rerunAfterReject = await syncService.SyncAsync(CreateRequestWithOneEvent("Spotkanie z Kowalski"), Now, CancellationToken.None);

        Assert.Equal(0, rerunAfterReject.Updated);
        Assert.Equal("Spotkanie robocze", (await db.Suggestions.SingleAsync()).Title);

        suggestion.Status = SuggestionStatus.Approved;
        await db.SaveChangesAsync();

        var rerunAfterApprove = await syncService.SyncAsync(CreateRequestWithOneEvent("Spotkanie z Kowalski"), Now, CancellationToken.None);

        Assert.Equal(0, rerunAfterApprove.Updated);
        Assert.Equal("Spotkanie robocze", (await db.Suggestions.SingleAsync()).Title);
    }

    [Fact]
    public async Task SyncAsync_LicznikUpdatedNieRosnieGdyNicSieNieZmienilo()
    {
        await syncService.SyncAsync(CreateRequestWithOneEvent(), Now, CancellationToken.None);

        var rerun = await syncService.SyncAsync(CreateRequestWithOneEvent(), Now, CancellationToken.None);

        Assert.Equal(0, rerun.Updated);
        Assert.Equal(1, rerun.SkippedExisting);
    }

    [Fact]
    public async Task SyncAsync_UzywaCzasuDokumentuZZadaniaZamiastKonfiguracji()
    {
        var request = new SyncRequest
        {
            DefaultDocumentDurationMinutes = 90,
            DriveFiles =
            [
                new DriveFileDto { Id = "file-1", Name = "Umowa_NovaTech.docx", LastModifiedDateTime = Now.AddDays(-1), LastModifiedByMe = true },
            ],
        };

        await syncService.SyncAsync(request, Now, CancellationToken.None);

        Assert.Equal(90, (await db.Suggestions.SingleAsync()).DurationMinutes);
    }

    [Fact]
    public void SyncRequest_OdrzucaCzasDokumentuPozaZakresem()
    {
        var request = new SyncRequest { DefaultDocumentDurationMinutes = 0 };

        var validationResults = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
        var isValid = System.ComponentModel.DataAnnotations.Validator.TryValidateObject(
            request, new System.ComponentModel.DataAnnotations.ValidationContext(request), validationResults, validateAllProperties: true);

        Assert.False(isValid);
    }

    [Fact]
    public async Task SyncAsync_ZdezaktywowanaSprawaNieBierzeUdzialuWDopasowaniu()
    {
        var kowalskiCase = await db.Cases.SingleAsync(legalCase => legalCase.CaseNumber == "K-2026-001");
        kowalskiCase.IsActive = false;
        await db.SaveChangesAsync();

        await syncService.SyncAsync(CreateRequestWithOneEvent("Spotkanie z Kowalski"), Now, CancellationToken.None);

        Assert.Null((await db.Suggestions.SingleAsync()).CaseId);
    }

    [Fact]
    public async Task SyncAsync_RaportZawieraLicznikiPobranOdrzucenIDopasowan()
    {
        var request = new SyncRequest
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
                new CalendarEventDto
                {
                    Id = "event-2",
                    Subject = "Prywatna wizyta",
                    StartDateTime = Now.AddDays(-1),
                    EndDateTime = Now.AddDays(-1).AddHours(1),
                    Sensitivity = "private",
                },
            ],
            DriveFiles =
            [
                new DriveFileDto { Id = "file-1", Name = "zdjecie.png", LastModifiedDateTime = Now.AddDays(-1), LastModifiedByMe = true },
            ],
        };

        var report = await syncService.SyncAsync(request, Now, CancellationToken.None);

        Assert.Equal(2, report.Fetched.CalendarEvents);
        Assert.Equal(1, report.Fetched.DriveFiles);
        Assert.Equal(1, report.FilteredOut.Private);
        Assert.Equal(1, report.FilteredOut.NotOfficeDocument);
        Assert.Equal(1, report.Created);
        Assert.Equal(1, report.Matched.Single); // "Kowalski" pasuje do sprawy z seedu
    }

    [Fact]
    public async Task SyncAsync_DoliczaLicznikiFiltrowKlienckichDoRaportu()
    {
        // Backend celowo nie widzi pozycji odfiltrowanych w przeglądarce (prywatność) —
        // raport pokazuje prawdę tylko dzięki doliczeniu liczników klienckich.
        var request = new SyncRequest
        {
            ClientFilteredCounts = new ClientFilteredCounts
            {
                Private = 2,
                Cancelled = 1,
                DocumentsOutsideWindow = 3,
                DocumentsNotOfficeDocument = 4,
            },
        };

        var report = await syncService.SyncAsync(request, Now, CancellationToken.None);

        Assert.Equal(2, report.FilteredOut.Private);
        Assert.Equal(1, report.FilteredOut.Cancelled);
        Assert.Equal(3, report.FilteredOut.OutsideWindow);
        Assert.Equal(4, report.FilteredOut.NotOfficeDocument);
        Assert.Equal(10, report.FilteredOut.Total);
    }

    [Fact]
    public async Task SyncAsync_DuplikatKandydataWJednymZadaniuTworzyJednaSugestie()
    {
        // Ten sam obiekt Graph dwa razy w jednym payloadzie — bez deduplikacji
        // w obrębie żądania zapis skończyłby się naruszeniem indeksu i błędem 500.
        var request = new SyncRequest
        {
            CalendarEvents =
            [
                new CalendarEventDto
                {
                    Id = "event-1",
                    Subject = "Spotkanie robocze",
                    StartDateTime = Now.AddDays(-1),
                    EndDateTime = Now.AddDays(-1).AddHours(1),
                },
                new CalendarEventDto
                {
                    Id = "event-1",
                    Subject = "Spotkanie z Kowalski",
                    StartDateTime = Now.AddDays(-1),
                    EndDateTime = Now.AddDays(-1).AddHours(1),
                },
            ],
        };

        var report = await syncService.SyncAsync(request, Now, CancellationToken.None);

        Assert.Equal(1, report.Created);
        var suggestion = await db.Suggestions.SingleAsync();
        // Wygrywa ostatnie wystąpienie klucza w payloadzie.
        Assert.Equal("Spotkanie z Kowalski", suggestion.Title);
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
