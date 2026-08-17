using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TimeSuggestions.Data;
using TimeSuggestions.Models;
using TimeSuggestions.Services;

namespace TimeSuggestions.Tests;

/// <summary>
/// Przerwy w zasięgu wpisu i rozliczanie ich jednym kliknięciem. Sedno: po scaleniu sesji
/// wpis obejmuje kilka odcinków pracy, a przerwy MIĘDZY nimi nie były nigdzie zapisane —
/// prawnik widział je w historii wersji, ale nie miał czym ich rozliczyć ani skąd
/// wiedzieć, że nie są liczone. Liczymy je z dziennika wersji, więc działa to także dla
/// wpisów scalonych, zanim ta funkcja powstała.
/// </summary>
public sealed class EntryGapTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection connection;
    private readonly AppDbContext db;
    private readonly EntryGapService gaps;
    private readonly TimeEntryOperationsService operations;

    public EntryGapTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        db = new AppDbContext(options);
        db.Database.EnsureCreated();

        gaps = new EntryGapService(db, TestHelpers.DefaultOptions());
        operations = new TimeEntryOperationsService(db, TestHelpers.DefaultOptions(), gaps);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }

    /// <summary>Godzina lokalna 24 lipca (Warszawa = UTC+2 w lipcu).</summary>
    private static DateTime At(int hour, int minute) => new(2026, 7, 24, hour, minute, 0);

    private static DateTime Utc(int hour, int minute)
        => new DateTime(2026, 7, 24, hour, minute, 0, DateTimeKind.Utc).AddHours(-2);

    /// <summary>Zapis pliku w dzienniku wersji — godzina podawana lokalnie, zapis w UTC.</summary>
    private void AddActivity(string externalId, int hour, int minute, string versionId)
    {
        db.DocumentActivities.Add(new DocumentActivity
        {
            ExternalId = externalId,
            VersionId = versionId,
            OccurredAt = Utc(hour, minute),
            Size = 1000,
            RecordedAt = Now,
        });
        db.SaveChanges();
    }

    /// <summary>
    /// Wpis dokumentowy o zadanym zasięgu i czasie — tak wygląda wynik scalenia sesji:
    /// godziny obejmują obie, czas liczy tylko je same.
    /// </summary>
    private TimeEntry SeedEntry(string externalId, DateTime startedAt, DateTime endedAt, int minutes)
    {
        var suggestion = new Suggestion
        {
            Source = SuggestionSource.Document,
            ExternalId = externalId,
            Title = "Umowa_NovaTech.docx",
            StartedAt = startedAt,
            SessionAnchor = startedAt,
            EntryDate = DateOnly.FromDateTime(startedAt),
            DurationMinutes = minutes,
            ProposedDescription = "Praca nad dokumentem",
            Status = SuggestionStatus.Approved,
            CreatedAt = Now,
        };
        var entry = new TimeEntry
        {
            CaseId = 1,
            EntryDate = DateOnly.FromDateTime(startedAt),
            StartedAt = startedAt,
            EndedAt = endedAt,
            DurationMinutes = minutes,
            Description = "Praca nad dokumentem",
            CreatedFromSuggestion = true,
            Source = SuggestionSource.Document,
            Suggestions = [suggestion],
            CreatedAt = Now,
        };
        db.TimeEntries.Add(entry);
        db.SaveChanges();
        return entry;
    }

    /// <summary>Wpis po scaleniu: dwie sesje i przerwa między nimi, której nikt nie liczy.</summary>
    private TimeEntry SeedMergedEntry()
    {
        // Sesja 09:00–09:30 (zapisy co 10 min), przerwa 50 min, sesja 10:20–10:40.
        AddActivity("file-1", 9, 0, "1.0");
        AddActivity("file-1", 9, 10, "2.0");
        AddActivity("file-1", 9, 20, "3.0");
        AddActivity("file-1", 9, 30, "4.0");
        AddActivity("file-1", 10, 20, "5.0");
        AddActivity("file-1", 10, 30, "6.0");
        AddActivity("file-1", 10, 40, "7.0");
        return SeedEntry("file-1", At(9, 0), At(10, 40), minutes: 50);
    }

    [Fact]
    public async Task Przerwa_MiedzySesjamiJestWidocznaINieJestLiczona()
    {
        var entry = SeedMergedEntry();

        var found = Assert.Single(await gaps.LoadForEntryAsync(entry, CancellationToken.None));

        Assert.Equal(At(9, 30), found.StartAt);
        Assert.Equal(At(10, 20), found.EndAt);
        Assert.Equal(50, found.Minutes);
        Assert.False(found.Counted);
    }

    /// <summary>Przerwa wewnątrz sesji siedzi w czasie brutto, więc jest liczona (do odjęcia).</summary>
    [Fact]
    public async Task Przerwa_WewnatrzSesjiJestLiczona()
    {
        AddActivity("file-1", 9, 0, "1.0");
        AddActivity("file-1", 9, 20, "2.0"); // 20 min: w przedziale wykrywanym
        var entry = SeedEntry("file-1", At(9, 0), At(9, 20), minutes: 20);

        var found = Assert.Single(await gaps.LoadForEntryAsync(entry, CancellationToken.None));

        Assert.Equal(20, found.Minutes);
        Assert.True(found.Counted);
    }

    /// <summary>Odstęp poniżej progu ciągłości to praca bez przerwy, nie pozycja do rozliczenia.</summary>
    [Fact]
    public async Task Przerwa_KrotszaNizProgCiaglosciNieJestPokazywana()
    {
        AddActivity("file-1", 9, 0, "1.0");
        AddActivity("file-1", 9, 10, "2.0");
        var entry = SeedEntry("file-1", At(9, 0), At(9, 10), minutes: 10);

        Assert.Empty(await gaps.LoadForEntryAsync(entry, CancellationToken.None));
    }

    /// <summary>Zapisy spoza godzin wpisu należą do innej pozycji — do tej nic im.</summary>
    [Fact]
    public async Task Przerwa_LiczonaTylkoWGodzinachWpisu()
    {
        AddActivity("file-1", 9, 0, "1.0");
        AddActivity("file-1", 9, 10, "2.0");
        AddActivity("file-1", 12, 0, "3.0"); // inna sesja, poza zasięgiem wpisu
        var entry = SeedEntry("file-1", At(9, 0), At(9, 10), minutes: 10);

        Assert.Empty(await gaps.LoadForEntryAsync(entry, CancellationToken.None));
    }

    [Fact]
    public async Task Doliczenie_PrzerwyPodnosiCzasIZmieniaJejStan()
    {
        var entry = SeedMergedEntry();

        var result = await operations.SetGapCountedAsync(
            entry.Id, At(9, 30), At(10, 20), counted: true, Now, CancellationToken.None);

        Assert.Equal(TimeEntryOperationStatus.Success, result.Status);
        Assert.Equal(100, result.Entries![0].DurationMinutes);
        var updated = Assert.Single(await gaps.LoadForEntryAsync(result.Entries[0], CancellationToken.None));
        Assert.True(updated.Counted);
    }

    /// <summary>Decyzja człowieka jest odwracalna: doliczoną przerwę wolno odjąć z powrotem.</summary>
    [Fact]
    public async Task Doliczona_PrzerwaDaSieOdjacZPowrotem()
    {
        var entry = SeedMergedEntry();
        await operations.SetGapCountedAsync(
            entry.Id, At(9, 30), At(10, 20), counted: true, Now, CancellationToken.None);

        var result = await operations.SetGapCountedAsync(
            entry.Id, At(9, 30), At(10, 20), counted: false, Now.AddMinutes(1), CancellationToken.None);

        Assert.Equal(TimeEntryOperationStatus.Success, result.Status);
        Assert.Equal(50, result.Entries![0].DurationMinutes);
        Assert.False(Assert.Single(await gaps.LoadForEntryAsync(result.Entries[0], CancellationToken.None)).Counted);
    }

    [Fact]
    public async Task Doliczenie_DrugiRazTegoSamegoOdmawia()
    {
        var entry = SeedMergedEntry();
        await operations.SetGapCountedAsync(
            entry.Id, At(9, 30), At(10, 20), counted: true, Now, CancellationToken.None);

        var rerun = await operations.SetGapCountedAsync(
            entry.Id, At(9, 30), At(10, 20), counted: true, Now.AddMinutes(1), CancellationToken.None);

        Assert.Equal(TimeEntryOperationStatus.Conflict, rerun.Status);
        Assert.Equal(100, (await db.TimeEntries.SingleAsync()).DurationMinutes);
    }

    /// <summary>
    /// Przerwa z już wykonaną korektą nie może zniknąć razem ze swoim stanem, gdy
    /// dziennik przestanie ją odtwarzać — inaczej te same minuty dałoby się odjąć drugi raz.
    /// </summary>
    [Fact]
    public async Task Przerwa_ZKorektaZostajeNaLiscieTakzeGdyDziennikJejNieOdtwarza()
    {
        var stored = DetectedGaps.Serialize([new DetectedGap(At(9, 30), At(9, 50))]);
        var entry = SeedEntry("file-1", At(9, 0), At(10, 0), minutes: 60);
        entry.DetectedGapsJson = stored;
        await db.SaveChangesAsync();
        await operations.SetGapCountedAsync(
            entry.Id, At(9, 30), At(9, 50), counted: false, Now, CancellationToken.None);

        // Dziennik dostaje własne zapisy o zupełnie innym rozkładzie niż zapisana przerwa.
        AddActivity("file-1", 9, 0, "1.0");
        AddActivity("file-1", 9, 25, "2.0");
        AddActivity("file-1", 10, 0, "3.0");

        var found = await gaps.LoadForEntryAsync(
            await db.TimeEntries.Include(e => e.Adjustments).SingleAsync(), CancellationToken.None);

        var subtracted = Assert.Single(found, gap => gap.StartAt == At(9, 30));
        Assert.False(subtracted.Counted);
    }

    [Theory]
    [InlineData(14, 30)]  // krócej niż połowa jednostki → i tak jedna jednostka (wpisu na zero nie ma)
    [InlineData(45, 30)]  // dokładna połowa → w dół, nigdy na niekorzyść klienta
    [InlineData(46, 60)]
    [InlineData(100, 90)]
    [InlineData(30, 30)]  // już wielokrotność
    public void Zaokraglenie_IdzieDoNajblizszejJednostkiAPolowaWDol(int minutes, int expected)
        => Assert.Equal(expected, TimeEntryOperationsService.RoundToIncrement(minutes, 30));

    [Fact]
    public async Task Zaokraglenie_ZapisujeKorekteWDzienniku()
    {
        var entry = SeedEntry("file-1", At(9, 0), At(9, 50), minutes: 50);

        var result = await operations.RoundAsync(entry.Id, Now, CancellationToken.None);

        Assert.Equal(TimeEntryOperationStatus.Success, result.Status);
        Assert.Equal(60, result.Entries![0].DurationMinutes);
        var adjustment = Assert.Single(await db.TimeEntryAdjustments.ToListAsync());
        Assert.Equal(AdjustmentKind.Rounding, adjustment.Kind);
        Assert.Equal(10, adjustment.Minutes);
    }

    [Fact]
    public async Task Zaokraglenie_NieRobiNiczegoGdyCzasJuzPasuje()
    {
        var entry = SeedEntry("file-1", At(9, 0), At(9, 30), minutes: 30);

        var result = await operations.RoundAsync(entry.Id, Now, CancellationToken.None);

        Assert.Equal(TimeEntryOperationStatus.Invalid, result.Status);
        Assert.Empty(await db.TimeEntryAdjustments.ToListAsync());
    }
}
