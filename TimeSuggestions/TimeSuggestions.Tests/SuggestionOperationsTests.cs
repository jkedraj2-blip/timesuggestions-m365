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
/// Poprawki czasu na etapie SUGESTII: scalanie sesji tego samego dokumentu i doliczanie
/// wolnych luk. Sedno reguły biznesowej: luka jest do doliczenia tylko wtedy, gdy nikt
/// inny jej nie zajmuje — praca nad innym dokumentem w tym czasie oznacza, że „przerwa"
/// jest już rozliczona i nie ma czego dodawać.
/// </summary>
public sealed class SuggestionOperationsTests : IDisposable
{
    private static readonly DateOnly Day = new(2026, 7, 24);

    private readonly SqliteConnection connection;
    private readonly AppDbContext db;
    private readonly SuggestionOperationsService operations;

    public SuggestionOperationsTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        db = new AppDbContext(options);
        db.Database.EnsureCreated();

        operations = new SuggestionOperationsService(db, Options.Create(new SuggestionOptions()));
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }

    private Suggestion AddSuggestion(
        int startHour,
        int startMinute,
        int durationMinutes,
        string externalId = "file-1",
        string title = "Umowa_NovaTech.docx",
        DateOnly? day = null,
        int startSecond = 0)
    {
        var entryDate = day ?? Day;
        var startedAt = new DateTime(
            entryDate.Year, entryDate.Month, entryDate.Day, startHour, startMinute, startSecond);
        var suggestion = new Suggestion
        {
            Source = SuggestionSource.Document,
            ExternalId = externalId,
            Title = title,
            StartedAt = startedAt,
            SessionAnchor = startedAt,
            // Koniec sesji na osi UTC — tak, jak zapisuje go silnik sesji. StartedAt jest
            // lokalny, więc bez konwersji fixture opisywałby sugestię kończącą się dwie
            // godziny po swoim czasie (letnia Warszawa = UTC+2), a zasięg z SuggestionSpan
            // liczyłby teren, którego w danych nie ma.
            LastActivityAt = BusinessTime.ToUtcInstant(
                startedAt.AddMinutes(durationMinutes), TestHelpers.BusinessTimeZone()),
            EntryDate = entryDate,
            DurationMinutes = durationMinutes,
            ProposedDescription = title,
            Status = SuggestionStatus.Pending,
            CreatedAt = startedAt,
        };
        db.Suggestions.Add(suggestion);
        db.SaveChanges();
        return suggestion;
    }

    /// <summary>
    /// Encja trzyma ostatnią modyfikację w UTC, ale DTO musi podać ją na TEJ SAMEJ osi
    /// co StartedAt. Bez konwersji sesja o jednym zapisie pokazywała „początek 22:58,
    /// ostatnia zmiana 20:58" — ten sam moment na dwóch osiach, wyglądający jak koniec
    /// przed początkiem.
    /// </summary>
    [Fact]
    public void Dto_PodajeOstatniaModyfikacjeNaTejSamejOsiCoPoczatek()
    {
        var startedAtLocal = new DateTime(2026, 8, 11, 22, 58, 36);
        var suggestion = new Suggestion
        {
            Source = SuggestionSource.Document,
            ExternalId = "file-1",
            Title = "Umowa_NovaTech.docx",
            StartedAt = startedAtLocal,
            SessionAnchor = new DateTime(2026, 8, 11, 20, 58, 36, DateTimeKind.Utc),
            // Ten sam moment co StartedAt, tylko w UTC (Europe/Warsaw = UTC+2 w sierpniu).
            LastActivityAt = new DateTime(2026, 8, 11, 20, 58, 36, DateTimeKind.Utc),
            EntryDate = new DateOnly(2026, 8, 11),
            DurationMinutes = 5,
            ProposedDescription = "Praca nad dokumentem",
        };

        var dto = SuggestionDto.FromEntity(
            suggestion, TimeZoneInfo.FindSystemTimeZoneById(new SuggestionOptions().BusinessTimeZoneId));

        Assert.Equal(startedAtLocal, dto.LastActivityAt);
    }

    /// <summary>Kind Unspecified po odczycie z SQLite nadal znaczy UTC — konwersja musi zadziałać.</summary>
    [Fact]
    public async Task Dto_PrzeliczaOstatniaModyfikacjeTakzeGdyKindJestNieokreslony()
    {
        var suggestion = AddSuggestion(9, 0, 30);
        db.ChangeTracker.Clear();
        var reloaded = await db.Suggestions.SingleAsync(candidate => candidate.Id == suggestion.Id);
        Assert.Equal(DateTimeKind.Unspecified, reloaded.LastActivityAt.Kind);

        var dto = SuggestionDto.FromEntity(
            reloaded, TimeZoneInfo.FindSystemTimeZoneById(new SuggestionOptions().BusinessTimeZoneId));

        Assert.Equal(reloaded.LastActivityAt.AddHours(2), dto.LastActivityAt);
    }

    [Fact]
    public async Task Merge_LaczySesjeTegoSamegoDokumentuWJednaSugestie()
    {
        var first = AddSuggestion(9, 0, 30);
        var second = AddSuggestion(10, 0, 20);

        var result = await operations.MergeAsync([first.Id, second.Id], includeGaps: false, CancellationToken.None);

        Assert.Equal(TimeEntryOperationStatus.Success, result.Status);
        var survivor = await db.Suggestions.SingleAsync();
        Assert.Equal(first.Id, survivor.Id);
        Assert.Equal(50, survivor.DurationMinutes);
        Assert.Equal(second.LastActivityAt, survivor.LastActivityAt);
        // Od tej chwili sync nie przelicza sugestii i nie odtwarza sesji składowych.
        Assert.True(survivor.IsUserAdjusted);
    }

    [Fact]
    public async Task Merge_ZLukamiDoliczaPrzerweMiedzySesjami()
    {
        var first = AddSuggestion(9, 0, 30);
        var second = AddSuggestion(10, 0, 20);

        await operations.MergeAsync([first.Id, second.Id], includeGaps: true, CancellationToken.None);

        // 30 + 20 minut sesji + 30 minut wolnej luki między nimi.
        Assert.Equal(80, (await db.Suggestions.SingleAsync()).DurationMinutes);
    }

    [Fact]
    public async Task Merge_OdmawiaScalaniaRoznychDokumentow()
    {
        var first = AddSuggestion(9, 0, 30);
        var other = AddSuggestion(10, 0, 20, externalId: "file-2", title: "Pozew.docx");

        var result = await operations.MergeAsync([first.Id, other.Id], includeGaps: false, CancellationToken.None);

        Assert.Equal(TimeEntryOperationStatus.Invalid, result.Status);
        Assert.Equal(2, await db.Suggestions.CountAsync());
    }

    [Fact]
    public async Task ClaimGap_BezPodanychMinutDoliczaCalaLukeDoWskazanejSugestii()
    {
        var earlier = AddSuggestion(9, 0, 30);
        var later = AddSuggestion(10, 0, 20);

        var result = await operations.ClaimGapAsync(
            later.Id, GapDirection.Before, minutes: null, neighborMinutes: null, CancellationToken.None);

        Assert.Equal(TimeEntryOperationStatus.Success, result.Status);
        var updated = await db.Suggestions.SingleAsync(suggestion => suggestion.Id == later.Id);
        Assert.Equal(50, updated.DurationMinutes);
        Assert.Equal(new DateTime(2026, 7, 24, 9, 30, 0), updated.StartedAt);
        // Sąsiad zostaje nietknięty — luka trafiła w całości do jednej pozycji.
        Assert.Equal(30, (await db.Suggestions.SingleAsync(s => s.Id == earlier.Id)).DurationMinutes);
    }

    /// <summary>Podział podaje użytkownik wprost — nie musi być równy.</summary>
    [Fact]
    public async Task ClaimGap_RozdzielaLukeWedlugMinutPodanychPrzezUzytkownika()
    {
        var earlier = AddSuggestion(9, 0, 30);
        var later = AddSuggestion(10, 0, 20);

        var result = await operations.ClaimGapAsync(
            later.Id, GapDirection.Before, minutes: 20, neighborMinutes: 10, CancellationToken.None);

        Assert.Equal(TimeEntryOperationStatus.Success, result.Status);
        Assert.Equal(40, (await db.Suggestions.SingleAsync(s => s.Id == later.Id)).DurationMinutes);
        Assert.Equal(40, (await db.Suggestions.SingleAsync(s => s.Id == earlier.Id)).DurationMinutes);
    }

    /// <summary>
    /// Suma mniejsza od luki jest w pełni legalna: niedobrane minuty zostają wolne.
    /// Każda pozycja rośnie od swojej strony, więc reszta leży pośrodku i nadal
    /// nikt się z nikim nie nakłada.
    /// </summary>
    [Fact]
    public async Task ClaimGap_NiedobraneMinutyZostajaWolne()
    {
        var earlier = AddSuggestion(9, 0, 30);
        var later = AddSuggestion(10, 0, 20);

        await operations.ClaimGapAsync(
            later.Id, GapDirection.Before, minutes: 5, neighborMinutes: 5, CancellationToken.None);

        var updatedLater = await db.Suggestions.SingleAsync(s => s.Id == later.Id);
        Assert.Equal(25, updatedLater.DurationMinutes);
        Assert.Equal(new DateTime(2026, 7, 24, 9, 55, 0), updatedLater.StartedAt);
        var updatedEarlier = await db.Suggestions.SingleAsync(s => s.Id == earlier.Id);
        Assert.Equal(35, updatedEarlier.DurationMinutes);
        Assert.Equal(new DateTime(2026, 7, 24, 9, 0, 0), updatedEarlier.StartedAt);
    }

    [Fact]
    public async Task ClaimGap_OdmawiaPodzialuWiekszegoNizWolnaPrzerwa()
    {
        AddSuggestion(9, 0, 30);
        var later = AddSuggestion(10, 0, 20);

        var result = await operations.ClaimGapAsync(
            later.Id, GapDirection.Before, minutes: 25, neighborMinutes: 10, CancellationToken.None);

        Assert.Equal(TimeEntryOperationStatus.Conflict, result.Status);
        Assert.Contains("30 min", result.Message);
        Assert.Equal(20, (await db.Suggestions.SingleAsync(s => s.Id == later.Id)).DurationMinutes);
    }

    /// <summary>
    /// Reguła, o którą chodziło najbardziej: praca nad innym dokumentem w „przerwie"
    /// oznacza, że przerwy nie ma — ten czas jest już rozliczony w historii tamtego pliku.
    /// </summary>
    [Fact]
    public async Task ClaimGap_LukaZajetaPrzezPraceNadInnymDokumentemNieJestDoDoliczenia()
    {
        AddSuggestion(9, 0, 30);
        AddSuggestion(9, 30, 30, externalId: "file-2", title: "Pozew.docx");
        var later = AddSuggestion(10, 0, 20);

        var result = await operations.ClaimGapAsync(
            later.Id, GapDirection.Before, minutes: null, neighborMinutes: null, CancellationToken.None);

        Assert.Equal(TimeEntryOperationStatus.Conflict, result.Status);
        Assert.Equal(20, (await db.Suggestions.SingleAsync(s => s.Id == later.Id)).DurationMinutes);
    }

    [Fact]
    public async Task LoadGaps_NieOferujeLukiDluzszejNizLimitZKonfiguracji()
    {
        AddSuggestion(9, 0, 30);
        var later = AddSuggestion(14, 0, 20); // 4,5 godziny dziury — to nie jest przerwa w pracy

        var gaps = await operations.LoadGapsAsync(await db.Suggestions.ToListAsync(), CancellationToken.None);

        Assert.False(gaps.TryGetValue(later.Id, out var forLater) && forLater.Before is not null);
    }

    [Fact]
    public async Task LoadGaps_OpisujeSasiadaITegoSamegoDokumentu()
    {
        var earlier = AddSuggestion(9, 0, 30);
        var later = AddSuggestion(10, 0, 20);

        var gaps = await operations.LoadGapsAsync(await db.Suggestions.ToListAsync(), CancellationToken.None);

        var before = gaps[later.Id].Before;
        Assert.NotNull(before);
        Assert.Equal(earlier.Id, before.SuggestionId);
        Assert.Equal(30, before.GapMinutes);
        Assert.True(before.CanMerge);
        // Symetrycznie z drugiej strony — ta sama luka, ten sam wymiar.
        Assert.Equal(30, gaps[earlier.Id].After!.GapMinutes);
    }

    /// <summary>
    /// Sąsiad zza północy: luka szuka o dobę w obie strony (spotkanie może przechodzić
    /// przez dzień), ale scalenie wymaga jednego dnia rozliczeniowego. UI nie może
    /// dostać zgody na scalenie, które MergeAsync natychmiast odrzuci — przycisk
    /// kończący się błędem „tylko z tego samego dnia" był obietnicą bez pokrycia.
    /// </summary>
    [Fact]
    public async Task LoadGaps_NieObiecujeScaleniaSesjiZDwochDni()
    {
        AddSuggestion(23, 30, 20);
        var nextDay = AddSuggestion(0, 10, 20, day: Day.AddDays(1));

        var gaps = await operations.LoadGapsAsync(await db.Suggestions.ToListAsync(), CancellationToken.None);

        var before = gaps[nextDay.Id].Before;
        Assert.NotNull(before);
        // Sama przerwa nadal jest do rozdzielenia — zabroniona jest tylko fuzja pozycji.
        Assert.Equal(20, before.GapMinutes);
        Assert.False(before.CanMerge);
    }

    /// <summary>
    /// Zgłoszony błąd: „Scal w jedną sesję" pokazywało się, a kliknięcie kończyło się
    /// komunikatem „Scalenie blokuje pozycja: <ten sam plik>". Przyczyna: sprawdzaliśmy
    /// CAŁY przedział scalenia, a w zastanych danych trzecia sesja tego samego pliku
    /// zachodziła na składową o 41 SEKUND. Nakładanie istniało już wcześniej i scalenie
    /// go nie pogarszało — a CanMerge patrzy tylko na przerwę między sąsiadami, więc UI
    /// obiecywało operację, którą ta sama warstwa odrzucała.
    /// </summary>
    [Fact]
    public async Task Merge_NieBlokujeNaNakladaniuKtoreIstnialoJuzPrzedScaleniem()
    {
        // Wcześniejsza sesja kończy się 10:05, a scalana zaczyna 10:00 — sprzeczność
        // odziedziczona po danych, nie skutek scalenia.
        AddSuggestion(9, 0, 65);
        var first = AddSuggestion(10, 0, 10);
        var second = AddSuggestion(10, 10, 20);

        var result = await operations.MergeAsync([first.Id, second.Id], false, CancellationToken.None);

        Assert.Equal(TimeEntryOperationStatus.Success, result.Status);
        var survivor = await db.Suggestions.SingleAsync(candidate => candidate.Id == first.Id);
        Assert.Equal(30, survivor.DurationMinutes);
    }

    /// <summary>Reguła nie została rozluźniona: obca pozycja W PRZERWIE nadal blokuje.</summary>
    [Fact]
    public async Task Merge_OdmawiaGdyObcaPozycjaStoiWPrzerwieMiedzySesjami()
    {
        var first = AddSuggestion(10, 0, 10);
        AddSuggestion(10, 15, 10, externalId: "file-2", title: "Pozew_Kowalski.docx");
        var second = AddSuggestion(10, 30, 10);

        var result = await operations.MergeAsync([first.Id, second.Id], false, CancellationToken.None);

        Assert.Equal(TimeEntryOperationStatus.Conflict, result.Status);
        Assert.Contains("Pozew_Kowalski.docx", result.Message);
    }

    /// <summary>
    /// Przerwa 4 min 40 s to CZTERY minuty do doliczenia, nie pięć. Zaokrąglenie w górę
    /// wydawało minutę, której w przerwie nie ma: doliczona wchodziła w sąsiada i
    /// zostawiała trwałe nakładanie — dokładnie to, przez co scalanie potrafiło potem
    /// odmawiać bez widocznej dla użytkownika przyczyny (różnica gubi się w minutach).
    /// </summary>
    [Fact]
    public async Task LoadGaps_NieWydajeMinutyKtorejWPrzerwieNieMa()
    {
        AddSuggestion(9, 0, 30);
        var later = AddSuggestion(9, 34, 20, startSecond: 40);

        var gaps = await operations.LoadGapsAsync(await db.Suggestions.ToListAsync(), CancellationToken.None);

        Assert.Equal(4, gaps[later.Id].Before!.GapMinutes);
    }

    /// <summary>
    /// Doliczenie całej przerwy nie może wysunąć sesji na sąsiada nawet o sekundę —
    /// inaczej każda kolejna operacja na tej osi jest już odrzucana.
    /// </summary>
    [Fact]
    public async Task ClaimGap_DoliczonaCaloscNieWchodziWSasiada()
    {
        var earlier = AddSuggestion(9, 0, 30);
        var later = AddSuggestion(9, 34, 20, startSecond: 40);

        var result = await operations.ClaimGapAsync(
            earlier.Id, GapDirection.After, null, null, CancellationToken.None);

        Assert.Equal(TimeEntryOperationStatus.Success, result.Status);
        var updated = await db.Suggestions.SingleAsync(candidate => candidate.Id == earlier.Id);
        Assert.True(updated.StartedAt.AddMinutes(updated.DurationMinutes) <= later.StartedAt);
    }

    /// <summary>
    /// Wpis z zatwierdzonej sugestii scalonej obejmuje OBIE sesje. Liczenie końca wpisu
    /// z czasu (start + minuty) kończyło go w środku pracy: druga, krótsza sesja
    /// wypadała poza wpis — w historii wersji nie było jej w podświetleniu, a jej minuty
    /// uchodziły za wolne, mimo że wpis już je rozliczał.
    /// </summary>
    [Fact]
    public async Task Approve_ScalonejSugestiiObejmujeWpisemObieSesje()
    {
        var first = AddSuggestion(9, 0, 30);   // 09:00–09:30
        var second = AddSuggestion(10, 0, 20); // 10:00–10:20
        await operations.MergeAsync([first.Id, second.Id], includeGaps: false, CancellationToken.None);
        var survivor = await db.Suggestions.SingleAsync();

        var approval = await new ApprovalService(db, TestHelpers.DefaultOptions()).ApproveAsync(
            survivor.Id,
            new ApproveSuggestionRequest
            {
                CaseId = 1,
                DurationMinutes = survivor.DurationMinutes,
                Description = "Praca nad dokumentem",
            },
            new DateTime(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc),
            CancellationToken.None);

        Assert.Equal(ApprovalOutcome.Success, approval.Outcome);
        var entry = approval.CreatedEntry!;
        Assert.Equal(new DateTime(2026, 7, 24, 9, 0, 0), entry.StartedAt);
        Assert.Equal(new DateTime(2026, 7, 24, 10, 20, 0), entry.EndedAt);
        // Rozliczana jest nadal SUMA sesji — przerwa między nimi leży w zasięgu wpisu,
        // ale nie w jego czasie (to samo rozróżnienie co przy „Odejmij przerwę").
        Assert.Equal(50, entry.DurationMinutes);
    }

    /// <summary>
    /// Sesja zwykła: koniec wpisu wynika z czasu i nic się nie rozciąga. Skrócenie czasu
    /// przy zatwierdzaniu nie zwalnia minut sesji — dokładnie jak korekta -15 na wpisie,
    /// która zmienia czas, a godzin nie rusza.
    /// </summary>
    [Theory]
    [InlineData(30, 9, 30)]
    [InlineData(20, 9, 30)]
    [InlineData(60, 10, 0)]
    public async Task Approve_KoniecWpisuToPozniejszaZGranic(int durationMinutes, int expectedHour, int expectedMinute)
    {
        var suggestion = AddSuggestion(9, 0, 30);

        var approval = await new ApprovalService(db, TestHelpers.DefaultOptions()).ApproveAsync(
            suggestion.Id,
            new ApproveSuggestionRequest
            {
                CaseId = 1,
                DurationMinutes = durationMinutes,
                Description = "Praca nad dokumentem",
            },
            new DateTime(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc),
            CancellationToken.None);

        Assert.Equal(ApprovalOutcome.Success, approval.Outcome);
        Assert.Equal(
            new DateTime(2026, 7, 24, expectedHour, expectedMinute, 0),
            approval.CreatedEntry!.EndedAt);
    }

    /// <summary>
    /// Kolejne scalenie „z przerwami" liczy lukę od KOŃCA ZASIĘGU wcześniejszego
    /// scalenia. Liczenie od końca jego czasu wciągało w lukę minuty, które ten czas
    /// już zawierał — ten sam kwadrans trafiałby do rozliczenia dwa razy.
    /// </summary>
    [Fact]
    public async Task Merge_ZLukamiNieLiczyDwaRazyMinutWczesniejszegoScalenia()
    {
        var first = AddSuggestion(9, 0, 30);   // 09:00–09:30
        var second = AddSuggestion(10, 0, 20); // 10:00–10:20
        var third = AddSuggestion(11, 0, 10);  // 11:00–11:10
        await operations.MergeAsync([first.Id, second.Id], includeGaps: false, CancellationToken.None);

        await operations.MergeAsync([first.Id, third.Id], includeGaps: true, CancellationToken.None);

        // 50 min scalonych sesji + 10 min trzeciej + 40 min wolnej przerwy 10:20–11:00.
        Assert.Equal(100, (await db.Suggestions.SingleAsync()).DurationMinutes);
    }

    /// <summary>
    /// Scalona sugestia rezerwuje na osi dnia cały swój zasięg: wolna przerwa zaczyna się
    /// po jej ostatniej modyfikacji, a nie po jej czasie. Inaczej sąsiad doliczyłby sobie
    /// minuty leżące w środku cudzej sesji.
    /// </summary>
    [Fact]
    public async Task ClaimGap_NieWchodziWZasiegScalonejSugestii()
    {
        var first = AddSuggestion(9, 0, 30);   // 09:00–09:30
        var second = AddSuggestion(10, 0, 20); // 10:00–10:20
        await operations.MergeAsync([first.Id, second.Id], includeGaps: false, CancellationToken.None);
        var later = AddSuggestion(11, 0, 15, externalId: "file-2", title: "Pozew.docx");

        var result = await operations.ClaimGapAsync(
            later.Id, GapDirection.Before, minutes: null, neighborMinutes: null, CancellationToken.None);

        Assert.Equal(TimeEntryOperationStatus.Success, result.Status);
        var updated = await db.Suggestions.SingleAsync(candidate => candidate.Id == later.Id);
        Assert.Equal(new DateTime(2026, 7, 24, 10, 20, 0), updated.StartedAt);
        Assert.Equal(55, updated.DurationMinutes);
    }

    /// <summary>Zgodność z tym, co obiecuje LoadGaps: scalenie przez północ faktycznie odpada.</summary>
    [Fact]
    public async Task Merge_OdmawiaScalaniaSesjiZDwochDni()
    {
        var first = AddSuggestion(23, 30, 20);
        var nextDay = AddSuggestion(0, 10, 20, day: Day.AddDays(1));

        var result = await operations.MergeAsync([first.Id, nextDay.Id], includeGaps: false, CancellationToken.None);

        Assert.Equal(TimeEntryOperationStatus.Invalid, result.Status);
        Assert.Equal(2, await db.Suggestions.CountAsync());
    }
}
