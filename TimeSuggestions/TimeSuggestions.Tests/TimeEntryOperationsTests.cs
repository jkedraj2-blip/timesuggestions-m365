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
/// Operacje prawnika i niezmiennik nakładania: każda minuta doby należy do najwyżej
/// jednego wpisu. Testy pokrywają ścieżki szczęśliwe i konflikty 409 (blokująca
/// pozycja, powtórne odjęcie przerwy, archiwum).
/// </summary>
public sealed class TimeEntryOperationsTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Day = new(2026, 7, 24);

    private readonly SqliteConnection connection;
    private readonly AppDbContext db;
    private readonly TimeEntryOperationsService operations;

    public TimeEntryOperationsTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        db = new AppDbContext(options);
        db.Database.EnsureCreated();

        operations = new TimeEntryOperationsService(
            db, TestHelpers.DefaultOptions(), new EntryGapService(db, TestHelpers.DefaultOptions()));
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }

    /// <summary>Wpis dokumentowy z sugestią sesyjną — para jak po zatwierdzeniu sugestii.</summary>
    private TimeEntry SeedDocumentEntry(
        string externalId,
        DateTime startedAt,
        int minutes,
        string description = "Praca nad dokumentem",
        string? detectedGapsJson = null,
        DateTime? archivedAt = null)
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
            DetectedGapsJson = detectedGapsJson,
            ProposedDescription = description,
            Status = SuggestionStatus.Approved,
            CreatedAt = Now,
        };
        var entry = new TimeEntry
        {
            CaseId = 2,
            EntryDate = DateOnly.FromDateTime(startedAt),
            StartedAt = startedAt,
            EndedAt = startedAt.AddMinutes(minutes),
            DurationMinutes = minutes,
            Description = description,
            CreatedFromSuggestion = true,
            Source = SuggestionSource.Document,
            DetectedGapsJson = detectedGapsJson,
            Suggestions = [suggestion],
            CreatedAt = Now,
            ArchivedAt = archivedAt,
        };
        db.TimeEntries.Add(entry);
        db.SaveChanges();
        return entry;
    }

    private Suggestion SeedPendingSuggestion(DateTime startedAt, int minutes, string title = "Spotkanie robocze")
    {
        var suggestion = new Suggestion
        {
            Source = SuggestionSource.Calendar,
            ExternalId = $"event-{Guid.NewGuid()}",
            Title = title,
            StartedAt = startedAt,
            SessionAnchor = startedAt,
            EntryDate = DateOnly.FromDateTime(startedAt),
            DurationMinutes = minutes,
            ProposedDescription = title,
            Status = SuggestionStatus.Pending,
            CreatedAt = Now,
        };
        db.Suggestions.Add(suggestion);
        db.SaveChanges();
        return suggestion;
    }

    private static DateTime At(int hour, int minute = 0) => new(2026, 7, 24, hour, minute, 0);

    /// <summary>Oczekująca sugestia dokumentowa — stan sprzed zatwierdzenia, jak po syncu.</summary>
    private Suggestion SeedPendingDocumentSuggestion(string externalId, DateTime startedAt, int minutes)
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
            Status = SuggestionStatus.Pending,
            CreatedAt = Now,
        };
        db.Suggestions.Add(suggestion);
        db.SaveChanges();
        return suggestion;
    }

    [Fact]
    public async Task MergeAsync_LaczyDwaWpisyTegoSamegoDokumentuBezPrzerw()
    {
        var first = SeedDocumentEntry("file-1", At(9), 30);
        var second = SeedDocumentEntry("file-1", At(11), 45);

        var result = await operations.MergeAsync(
            [first.Id, second.Id], includeGaps: false, Now, CancellationToken.None);

        Assert.Equal(TimeEntryOperationStatus.Success, result.Status);
        var merged = await db.TimeEntries.Include(e => e.Suggestions).SingleAsync();
        Assert.Equal(75, merged.DurationMinutes); // suma sesji bez luki
        Assert.Equal(At(9), merged.StartedAt);
        Assert.Equal(At(11, 45), merged.EndedAt);
        Assert.Equal(2, merged.Suggestions.Count);
    }

    [Fact]
    public async Task MergeAsync_ZPrzerwamiDoliczaLukeIZapisujeGapAddition()
    {
        var first = SeedDocumentEntry("file-1", At(9), 30);   // 09:00–09:30
        var second = SeedDocumentEntry("file-1", At(10), 30); // 10:00–10:30, luka 30 min

        var result = await operations.MergeAsync(
            [first.Id, second.Id], includeGaps: true, Now, CancellationToken.None);

        Assert.Equal(TimeEntryOperationStatus.Success, result.Status);
        var merged = await db.TimeEntries.Include(e => e.Adjustments).SingleAsync();
        Assert.Equal(90, merged.DurationMinutes); // 30 + 30 + 30 luki
        var adjustment = Assert.Single(merged.Adjustments);
        Assert.Equal(AdjustmentKind.GapAddition, adjustment.Kind);
        Assert.Equal(30, adjustment.Minutes);
        Assert.Equal(At(9, 30), adjustment.GapStartAt);
        Assert.Equal(At(10), adjustment.GapEndAt);
    }

    /// <summary>
    /// A1: wpis zawarty w drugim nie może skracać wyniku scalenia. Układ jest osiągalny
    /// przez UI: pole „Czas" przy zatwierdzaniu nie ma górnego ograniczenia, więc
    /// podniesienie czasu pierwszej sesji tworzy wpis obejmujący sesję drugą (której
    /// zatwierdzenie i tak przechodzi — pokrycie „od tyłu" daje Notice, nie odmowę).
    /// Koniec wyniku to NAJPÓŹNIEJSZY koniec składowych, nie koniec ostatniej po starcie.
    /// </summary>
    [Fact]
    public async Task MergeAsync_WpisZawartyWDrugimNieSkracaWyniku()
    {
        var approvals = new ApprovalService(db, TestHelpers.DefaultOptions());

        // Pierwsza sesja zatwierdzona z czasem podniesionym do 600 min → wpis 10:00–20:00.
        var first = SeedPendingDocumentSuggestion("file-1", At(10), 30);
        var firstApproved = await approvals.ApproveAsync(
            first.Id,
            new ApproveSuggestionRequest { CaseId = 1, DurationMinutes = 600, Description = "Praca" },
            Now,
            CancellationToken.None);
        Assert.Equal(ApprovalOutcome.Success, firstApproved.Outcome);
        Assert.Equal(At(20), firstApproved.CreatedEntry!.EndedAt);

        // Druga sesja tego samego pliku pojawia się później (praca 11:00–11:30 nie leży
        // w rozliczonym zakresie [kotwica, ostatnia zmiana] pierwszej sugestii).
        var second = SeedPendingDocumentSuggestion("file-1", At(11), 30);
        var secondApproved = await approvals.ApproveAsync(
            second.Id,
            new ApproveSuggestionRequest { CaseId = 1, DurationMinutes = 30, Description = "Praca" },
            Now,
            CancellationToken.None);
        Assert.Equal(ApprovalOutcome.Success, secondApproved.Outcome);

        var result = await operations.MergeAsync(
            [firstApproved.CreatedEntry!.Id, secondApproved.CreatedEntry!.Id],
            includeGaps: false, Now, CancellationToken.None);

        Assert.Equal(TimeEntryOperationStatus.Success, result.Status);
        var merged = await db.TimeEntries.SingleAsync();
        Assert.Equal(At(10), merged.StartedAt);
        // Bez tej reguły wynik kończył się o 11:30 (koniec ostatniego PO STARCIE wpisu)
        // i odcinek 11:30–20:00 wypadał z zasięgu — jego minuty stawały się „wolne".
        Assert.Equal(At(20), merged.EndedAt);
        Assert.Equal(630, merged.DurationMinutes);
    }

    /// <summary>
    /// B2: składowe, które już się nakładały (zastane dane, np. po zatwierdzeniu
    /// z podniesionym czasem), wolno scalić — a rozdzielenie przywraca je w pierwotnej
    /// postaci, łącznie z tym nakładaniem. To świadome zachowanie: unmerge odtwarza stan
    /// sprzed scalenia z sesji, nie naprawia zastanych danych. Operacje na osi dnia i tak
    /// bronią się przed zachodzeniem (FreeGapMinutes zwraca null, FindBlocker odmawia).
    /// </summary>
    [Fact]
    public async Task UnmergeAsync_PrzywracaSkladoweTakzeGdyJuzSieNakladaly()
    {
        var first = SeedDocumentEntry("file-1", At(9), 60);       // 09:00–10:00
        var second = SeedDocumentEntry("file-1", At(9, 30), 60);  // 09:30–10:30 — zachodzi na pierwszy

        var merge = await operations.MergeAsync(
            [first.Id, second.Id], includeGaps: false, Now, CancellationToken.None);
        Assert.Equal(TimeEntryOperationStatus.Success, merge.Status);

        var unmerge = await operations.UnmergeAsync(merge.Entries![0].Id, Now, CancellationToken.None);

        Assert.Equal(TimeEntryOperationStatus.Success, unmerge.Status);
        var restored = await db.TimeEntries.OrderBy(e => e.StartedAt).ToListAsync();
        Assert.Equal(2, restored.Count);
        Assert.Equal(At(9), restored[0].StartedAt);
        Assert.Equal(At(10), restored[0].EndedAt);
        Assert.Equal(At(9, 30), restored[1].StartedAt);
        Assert.Equal(At(10, 30), restored[1].EndedAt);
    }

    [Fact]
    public async Task MergeAsync_409GdyMiedzyWpisamiStoiInnaPozycja()
    {
        var first = SeedDocumentEntry("file-1", At(9), 30);
        var second = SeedDocumentEntry("file-1", At(11), 30);
        // Wpis INNEGO dokumentu w środku przedziału — sąsiedztwo globalne złamane.
        SeedDocumentEntry("file-2", At(10), 30, description: "Inny dokument");

        var result = await operations.MergeAsync(
            [first.Id, second.Id], includeGaps: false, Now, CancellationToken.None);

        Assert.Equal(TimeEntryOperationStatus.Conflict, result.Status);
        Assert.Contains("Inny dokument", result.Message);
    }

    [Fact]
    public async Task MergeAsync_409GdyMiedzyWpisamiStoiOczekujacaSugestia()
    {
        var first = SeedDocumentEntry("file-1", At(9), 30);
        var second = SeedDocumentEntry("file-1", At(11), 30);
        SeedPendingSuggestion(At(10), 20, title: "Telefon do klienta");

        var result = await operations.MergeAsync(
            [first.Id, second.Id], includeGaps: false, Now, CancellationToken.None);

        Assert.Equal(TimeEntryOperationStatus.Conflict, result.Status);
        Assert.Contains("Telefon do klienta", result.Message);
    }

    [Fact]
    public async Task MergeAsync_OdmawiaDlaRoznychDokumentowIDni()
    {
        var first = SeedDocumentEntry("file-1", At(9), 30);
        var otherFile = SeedDocumentEntry("file-2", At(11), 30);

        var differentFiles = await operations.MergeAsync(
            [first.Id, otherFile.Id], includeGaps: false, Now, CancellationToken.None);
        Assert.Equal(TimeEntryOperationStatus.Invalid, differentFiles.Status);

        var nextDay = SeedDocumentEntry("file-1", At(9).AddDays(1), 30);
        var differentDays = await operations.MergeAsync(
            [first.Id, nextDay.Id], includeGaps: false, Now, CancellationToken.None);
        Assert.Equal(TimeEntryOperationStatus.Invalid, differentDays.Status);
    }

    [Fact]
    public async Task MergeAsync_OdmawiaDlaWpisuZarchiwizowanego()
    {
        var first = SeedDocumentEntry("file-1", At(9), 30);
        var second = SeedDocumentEntry("file-1", At(11), 30, archivedAt: Now);

        var result = await operations.MergeAsync(
            [first.Id, second.Id], includeGaps: false, Now, CancellationToken.None);

        Assert.Equal(TimeEntryOperationStatus.Archived, result.Status);
    }

    [Fact]
    public async Task UnmergeAsync_PrzywracaWpisySkladoweZSesji()
    {
        var first = SeedDocumentEntry("file-1", At(9), 30);
        var second = SeedDocumentEntry("file-1", At(10), 30);
        await operations.MergeAsync([first.Id, second.Id], includeGaps: true, Now, CancellationToken.None);
        var merged = await db.TimeEntries.SingleAsync();

        var result = await operations.UnmergeAsync(merged.Id, Now, CancellationToken.None);

        Assert.Equal(TimeEntryOperationStatus.Success, result.Status);
        var restored = await db.TimeEntries.OrderBy(e => e.StartedAt).ToListAsync();
        Assert.Equal(2, restored.Count);
        Assert.Equal([30, 30], restored.Select(e => e.DurationMinutes));
        Assert.Equal(At(9), restored[0].StartedAt);
        Assert.Equal(At(10), restored[1].StartedAt);
        // Korekty scalenia (GapAddition) zniknęły razem ze scalonym wpisem.
        Assert.Equal(0, await db.TimeEntryAdjustments.CountAsync());
    }

    [Fact]
    public async Task UnmergeAsync_OdmawiaDlaWpisuBezScalenia()
    {
        var entry = SeedDocumentEntry("file-1", At(9), 30);

        var result = await operations.UnmergeAsync(entry.Id, Now, CancellationToken.None);

        Assert.Equal(TimeEntryOperationStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task SubtractGapAsync_OdejmujePrzerweZListyWykrytych()
    {
        var gaps = DetectedGaps.Serialize([new DetectedGap(At(9, 30), At(9, 50))]);
        var entry = SeedDocumentEntry("file-1", At(9), 60, detectedGapsJson: gaps);

        var result = await operations.SetGapCountedAsync(
            entry.Id, At(9, 30), At(9, 50), counted: false, Now, CancellationToken.None);

        Assert.Equal(TimeEntryOperationStatus.Success, result.Status);
        var updated = await db.TimeEntries.Include(e => e.Adjustments).SingleAsync();
        Assert.Equal(40, updated.DurationMinutes);
        var adjustment = Assert.Single(updated.Adjustments);
        Assert.Equal(AdjustmentKind.DetectedGapSubtraction, adjustment.Kind);
        Assert.Equal(-20, adjustment.Minutes);
    }

    [Fact]
    public async Task SubtractGapAsync_DrugaProbaNaTeSamaLuke409()
    {
        var gaps = DetectedGaps.Serialize([new DetectedGap(At(9, 30), At(9, 50))]);
        var entry = SeedDocumentEntry("file-1", At(9), 60, detectedGapsJson: gaps);
        await operations.SetGapCountedAsync(entry.Id, At(9, 30), At(9, 50), counted: false, Now, CancellationToken.None);

        var rerun = await operations.SetGapCountedAsync(
            entry.Id, At(9, 30), At(9, 50), counted: false, Now, CancellationToken.None);

        Assert.Equal(TimeEntryOperationStatus.Conflict, rerun.Status);
        Assert.Equal(40, (await db.TimeEntries.SingleAsync()).DurationMinutes);
    }

    [Fact]
    public async Task SubtractGapAsync_OdmawiaDlaPrzerwySpozaListy()
    {
        var entry = SeedDocumentEntry("file-1", At(9), 60);

        var result = await operations.SetGapCountedAsync(
            entry.Id, At(9, 30), At(9, 50), counted: false, Now, CancellationToken.None);

        Assert.Equal(TimeEntryOperationStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task AdjustAsync_KorektaZmieniaCzasIZapisujeDziennik()
    {
        var entry = SeedDocumentEntry("file-1", At(9), 60);

        await operations.AdjustAsync(entry.Id, -15, Now, CancellationToken.None);
        await operations.AdjustAsync(entry.Id, 30, Now, CancellationToken.None);

        var updated = await db.TimeEntries.Include(e => e.Adjustments).SingleAsync();
        Assert.Equal(75, updated.DurationMinutes);
        Assert.Equal(2, updated.Adjustments.Count);
        Assert.All(updated.Adjustments, a => Assert.Equal(AdjustmentKind.QuickAdjustment, a.Kind));
    }

    [Fact]
    public async Task AdjustAsync_409GdyWynikPozaGranicami()
    {
        var entry = SeedDocumentEntry("file-1", At(9), 30);

        var belowZero = await operations.AdjustAsync(entry.Id, -30, Now, CancellationToken.None);
        Assert.Equal(TimeEntryOperationStatus.Conflict, belowZero.Status);

        var aboveMax = await operations.AdjustAsync(
            entry.Id, SuggestionOptions.MaxDocumentDurationMinutes, Now, CancellationToken.None);
        Assert.Equal(TimeEntryOperationStatus.Conflict, aboveMax.Status);
    }

    /// <summary>
    /// A3: limit 480 min blokuje wyłącznie WZROST czasu. Zatwierdzenie dopuszcza
    /// 1–1440 min (pole „Czas" nie ma górnego ograniczenia w UI), więc wpis powyżej
    /// limitu istnieje legalnie — a operacja, która czas ZMNIEJSZA, nie może być
    /// odrzucana komunikatem „przekroczyłby limit".
    /// </summary>
    [Fact]
    public async Task AdjustAsync_ZmniejszenieWpisuPowyzejLimituPrzechodzi()
    {
        var entry = SeedDocumentEntry("file-1", At(9), 600); // 600 min — legalne po zatwierdzeniu

        var result = await operations.AdjustAsync(entry.Id, -15, Now, CancellationToken.None);

        Assert.Equal(TimeEntryOperationStatus.Success, result.Status);
        Assert.Equal(585, (await db.TimeEntries.SingleAsync()).DurationMinutes);
    }

    [Fact]
    public async Task AdjustAsync_ZwiekszenieWpisuPowyzejLimituNadalBlokowane()
    {
        var entry = SeedDocumentEntry("file-1", At(9), 600);

        var result = await operations.AdjustAsync(entry.Id, 15, Now, CancellationToken.None);

        Assert.Equal(TimeEntryOperationStatus.Conflict, result.Status);
        Assert.Equal(600, (await db.TimeEntries.SingleAsync()).DurationMinutes);
    }

    /// <summary>A3: zaokrąglenie W DÓŁ wpisu powyżej limitu też jest zmniejszeniem.</summary>
    [Fact]
    public async Task RoundAsync_ZaokraglenieWDolPowyzejLimituPrzechodzi()
    {
        var entry = SeedDocumentEntry("file-1", At(9), 605); // najbliższa wielokrotność 30 → 600

        var result = await operations.RoundAsync(entry.Id, Now, CancellationToken.None);

        Assert.Equal(TimeEntryOperationStatus.Success, result.Status);
        Assert.Equal(600, (await db.TimeEntries.SingleAsync()).DurationMinutes);
    }

    /// <summary>A3: odjęcie przerwy zawsze zmniejsza czas — limit nie ma tu nic do rzeczy.</summary>
    [Fact]
    public async Task SubtractGapAsync_OdejmowaniePowyzejLimituPrzechodzi()
    {
        var gaps = DetectedGaps.Serialize([new DetectedGap(At(9, 30), At(9, 50))]);
        var entry = SeedDocumentEntry("file-1", At(9), 600, detectedGapsJson: gaps);

        var result = await operations.SetGapCountedAsync(
            entry.Id, At(9, 30), At(9, 50), counted: false, Now, CancellationToken.None);

        Assert.Equal(TimeEntryOperationStatus.Success, result.Status);
        Assert.Equal(580, (await db.TimeEntries.SingleAsync()).DurationMinutes);
    }

    /// <summary>
    /// Nakładanie NIE odrzuca zatwierdzenia. Sugestia zaczynająca się w środku cudzego
    /// wpisu to realny przypadek (dokument zapisany w trakcie rozliczonego spotkania),
    /// a nie błąd danych — odmowa zostawiałaby prawnika z pracą, której nie da się
    /// rozliczyć. Wpis powstaje, a komunikat nazywa pokrycie po imieniu.
    /// </summary>
    [Fact]
    public async Task ApproveAsync_PokrycieZInnymWpisemNieBlokuje()
    {
        SeedDocumentEntry("file-1", At(9), 60, description: "Zajęte minuty"); // 09:00–10:00
        var suggestion = SeedPendingSuggestion(At(9, 30), 30);

        var result = await new ApprovalService(db, TestHelpers.DefaultOptions()).ApproveAsync(
            suggestion.Id,
            new ApproveSuggestionRequest { CaseId = 1, DurationMinutes = 30, Description = "Praca" },
            Now,
            CancellationToken.None);

        Assert.Equal(ApprovalOutcome.Success, result.Outcome);
        Assert.Contains("Zajęte minuty", result.Notice);
        Assert.Equal(30, result.CreatedEntry!.DurationMinutes);
        Assert.Equal(2, await db.TimeEntries.CountAsync());
    }

    /// <summary>
    /// Sedno zgłoszonej nielogicznej odmowy: koniec liczony z minut wychodził SEKUNDY
    /// za początek sąsiada, a komunikat pokazywał obie godziny jako równe (00:07).
    /// Zasięg przycinamy do sąsiada, rozliczanego czasu nie ruszamy.
    /// </summary>
    [Fact]
    public async Task ApproveAsync_PrzycinaZasiegDoSasiadaZamiastOdmawiac()
    {
        // Sąsiad zaczyna 10:00:10; sugestia 09:00 + 60 min sięgałaby 10:00:00 tylko
        // pozornie — sekundy startu przesuwają koniec za jego początek.
        SeedDocumentEntry("file-2", new DateTime(2026, 7, 24, 10, 0, 10), 30, description: "Kolejna praca");
        var suggestion = SeedPendingSuggestion(new DateTime(2026, 7, 24, 9, 0, 20), 60);

        var result = await new ApprovalService(db, TestHelpers.DefaultOptions()).ApproveAsync(
            suggestion.Id,
            new ApproveSuggestionRequest { CaseId = 1, DurationMinutes = 60, Description = "Praca" },
            Now,
            CancellationToken.None);

        Assert.Equal(ApprovalOutcome.Success, result.Outcome);
        Assert.Equal(new DateTime(2026, 7, 24, 10, 0, 10), result.CreatedEntry!.EndedAt);
        Assert.Equal(60, result.CreatedEntry.DurationMinutes);
        Assert.Contains("10:00:10", result.Notice);
    }

    /// <summary>
    /// A5: w niezmienniku nakładania uczestniczą też OCZEKUJĄCE sugestie (rezerwacje
    /// minut) — tak mówi komentarz klasowy operacji i README, i tak liczą sąsiadów oba
    /// serwisy operacji. Zatwierdzenie musi je widzieć tak samo: zasięg wpisu przycina
    /// się do początku oczekującej sugestii, zamiast po cichu ją nakryć i zostawić jej
    /// późniejszą odmowę „Scalenie blokuje pozycja: wpis …" bez ostrzeżenia.
    /// </summary>
    [Fact]
    public async Task ApproveAsync_PrzycinaZasiegDoOczekujacejSugestii()
    {
        SeedPendingSuggestion(At(10), 30, title: "Telefon do klienta"); // 10:00–10:30
        var suggestion = SeedPendingSuggestion(At(9), 60);

        var result = await new ApprovalService(db, TestHelpers.DefaultOptions()).ApproveAsync(
            suggestion.Id,
            new ApproveSuggestionRequest { CaseId = 1, DurationMinutes = 90, Description = "Praca" },
            Now,
            CancellationToken.None);

        Assert.Equal(ApprovalOutcome.Success, result.Outcome);
        // Zasięg przycięty do początku oczekującej sugestii; rozliczany czas bez zmian.
        Assert.Equal(At(10), result.CreatedEntry!.EndedAt);
        Assert.Equal(90, result.CreatedEntry.DurationMinutes);
        Assert.Contains("10:00", result.Notice);
    }

    /// <summary>
    /// Sąsiad zaczynający się DOKŁADNIE w momencie startu sugestii nie daje się przyciąć:
    /// koniec zjeżdżał wtedy do początku i powstawał wpis o zerowym zasięgu, z zachowanym
    /// czasem do rozliczenia. Taki wpis nie zajmuje na osi dnia ani minuty, więc jego czas
    /// wyglądał na wolny i mógł zostać doliczony drugi raz gdzie indziej. Układ jest realny:
    /// spotkanie 18:30–19:00 i praca nad dokumentem zaczęta o 18:30. Przycięcie ma sens
    /// wyłącznie wobec sąsiada zaczynającego się PÓŹNIEJ; równy start to nakładanie,
    /// o którym się uprzedza, a nie usterka do naprawienia zasięgiem.
    /// </summary>
    [Fact]
    public async Task ApproveAsync_SasiadOTejSamejGodzinieStartuNieZerujeZasiegu()
    {
        SeedPendingSuggestion(At(18, 30), 30, title: "Spotkanie"); // 18:30–19:00
        var suggestion = SeedPendingDocumentSuggestion("file-1", At(18, 30), 5);

        var result = await new ApprovalService(db, TestHelpers.DefaultOptions()).ApproveAsync(
            suggestion.Id,
            new ApproveSuggestionRequest { CaseId = 1, DurationMinutes = 5, Description = "Praca" },
            Now,
            CancellationToken.None);

        Assert.Equal(ApprovalOutcome.Success, result.Outcome);
        Assert.Equal(At(18, 30), result.CreatedEntry!.StartedAt);
        Assert.Equal(At(18, 35), result.CreatedEntry.EndedAt);
        Assert.Contains("pokrywa się", result.Notice);
    }

    /// <summary>A5: pokrycie z oczekującą sugestią „od tyłu" daje Notice z jej nazwą, jak przy wpisie.</summary>
    [Fact]
    public async Task ApproveAsync_PokrycieZOczekujacaSugestiaDajeNotice()
    {
        SeedPendingSuggestion(At(8, 30), 60, title: "Poranne spotkanie"); // 08:30–09:30
        var suggestion = SeedPendingSuggestion(At(9), 30);

        var result = await new ApprovalService(db, TestHelpers.DefaultOptions()).ApproveAsync(
            suggestion.Id,
            new ApproveSuggestionRequest { CaseId = 1, DurationMinutes = 30, Description = "Praca" },
            Now,
            CancellationToken.None);

        Assert.Equal(ApprovalOutcome.Success, result.Outcome);
        Assert.Contains("Poranne spotkanie", result.Notice);
    }

    [Fact]
    public async Task ApproveAsync_SasiadujaceWpisyNieKoliduja()
    {
        // Przedziały półotwarte: koniec 10:00 i start 10:00 to nie nakład.
        SeedDocumentEntry("file-1", At(9), 60); // 09:00–10:00
        var suggestion = SeedPendingSuggestion(At(10), 30);

        var result = await new ApprovalService(db, TestHelpers.DefaultOptions()).ApproveAsync(
            suggestion.Id,
            new ApproveSuggestionRequest { CaseId = 1, DurationMinutes = 30, Description = "Praca" },
            Now,
            CancellationToken.None);

        Assert.Equal(ApprovalOutcome.Success, result.Outcome);
    }
}
