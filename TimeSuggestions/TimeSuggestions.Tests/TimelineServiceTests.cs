using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TimeSuggestions.Configuration;
using TimeSuggestions.Data;
using TimeSuggestions.Models;
using TimeSuggestions.Services;

namespace TimeSuggestions.Tests;

/// <summary>
/// Agregacja osi czasu: liczniki per dzień i lista dnia. Kluczowy przypadek —
/// zatwierdzona sugestia NIE liczy się podwójnie (raz jako sugestia, raz jako wpis).
/// </summary>
public sealed class TimelineServiceTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 8, 6, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Day = new(2026, 8, 6);

    private readonly SqliteConnection connection;
    private readonly AppDbContext db;
    private readonly TimelineService timeline;

    public TimelineServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        db = new AppDbContext(options);
        db.Database.EnsureCreated();

        timeline = TestHelpers.Timeline(db);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }

    private Suggestion SeedSuggestion(
        SuggestionStatus status,
        DateTime startedAt,
        int minutes = 30,
        DateTime? lastActivityAtUtc = null)
    {
        var suggestion = new Suggestion
        {
            Source = SuggestionSource.Calendar,
            ExternalId = $"event-{Guid.NewGuid()}",
            Title = "Spotkanie",
            StartedAt = startedAt,
            SessionAnchor = startedAt,
            LastActivityAt = lastActivityAtUtc ?? default,
            EntryDate = DateOnly.FromDateTime(startedAt),
            DurationMinutes = minutes,
            ProposedDescription = "Spotkanie",
            Status = status,
            CreatedAt = Now,
        };
        db.Suggestions.Add(suggestion);
        db.SaveChanges();
        return suggestion;
    }

    /// <summary>Sesja pracy nad plikiem — źródło pozycji w historii dokumentu.</summary>
    private Suggestion SeedDocumentSuggestion(
        SuggestionStatus status,
        DateTime startedAt,
        string externalId = "file-1")
    {
        var suggestion = new Suggestion
        {
            Source = SuggestionSource.Document,
            ExternalId = externalId,
            Title = "Umowa_NovaTech.docx",
            StartedAt = startedAt,
            SessionAnchor = startedAt,
            LastActivityAt = BusinessTime.ToUtcInstant(startedAt.AddMinutes(30), TestHelpers.BusinessTimeZone()),
            EntryDate = DateOnly.FromDateTime(startedAt),
            DurationMinutes = 30,
            ProposedDescription = "Praca nad dokumentem",
            Status = status,
            CreatedAt = Now,
        };
        db.Suggestions.Add(suggestion);
        db.SaveChanges();
        return suggestion;
    }

    private TimeEntry SeedEntry(
        DateTime startedAt,
        int minutes,
        DateTime? archivedAt = null,
        Suggestion? suggestion = null,
        SuggestionSource source = SuggestionSource.Calendar)
    {
        var entry = new TimeEntry
        {
            CaseId = 1,
            EntryDate = DateOnly.FromDateTime(startedAt),
            StartedAt = startedAt,
            EndedAt = startedAt.AddMinutes(minutes),
            DurationMinutes = minutes,
            Description = "Praca",
            CreatedFromSuggestion = true,
            Source = source,
            Suggestions = suggestion is null ? [] : [suggestion],
            CreatedAt = Now,
            ArchivedAt = archivedAt,
        };
        db.TimeEntries.Add(entry);
        db.SaveChanges();
        return entry;
    }

    private static DateTime At(int day, int hour) => new(2026, 8, day, hour, 0, 0);

    [Fact]
    public async Task GetRangeAsync_ZatwierdzonaSugestiaLiczonaRazJakoWpis()
    {
        // Zatwierdzona sugestia + jej wpis: dzień ma pokazać 1 pozycję aktywną, zero
        // oczekujących — nie 2 pozycje.
        var approved = SeedSuggestion(SuggestionStatus.Approved, At(6, 9));
        SeedEntry(At(6, 9), 60, suggestion: approved);
        SeedSuggestion(SuggestionStatus.Pending, At(6, 11));
        SeedSuggestion(SuggestionStatus.Rejected, At(6, 13)); // odrzucona nie jest pozycją osi

        var days = await timeline.GetRangeAsync(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31), CancellationToken.None);

        var day = Assert.Single(days);
        Assert.Equal(Day, day.Date);
        Assert.Equal(1, day.PendingCount);
        Assert.Equal(1, day.ActiveCount);
        Assert.Equal(0, day.ArchivedCount);
    }

    [Fact]
    public async Task GetRangeAsync_GrupujePoDniachIRozdzielaArchiwum()
    {
        SeedEntry(At(4, 9), 60);
        SeedEntry(At(4, 11), 30, archivedAt: Now);
        SeedEntry(At(5, 9), 45);

        var days = await timeline.GetRangeAsync(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31), CancellationToken.None);

        Assert.Equal(2, days.Count);
        Assert.Equal(new DateOnly(2026, 8, 4), days[0].Date);
        Assert.Equal(1, days[0].ActiveCount);
        Assert.Equal(1, days[0].ArchivedCount);
        Assert.Equal(new DateOnly(2026, 8, 5), days[1].Date);
        Assert.Equal(1, days[1].ActiveCount);
    }

    /// <summary>
    /// Scalona sugestia zajmuje na osi cały swój zasięg, a nie odcinek długości swojego
    /// czasu: po scaleniu czas jest SUMĄ sesji, więc pozycja liczona z minut kończyłaby
    /// się przed ostatnią modyfikacją i wyglądała na krótszą, niż jest.
    /// </summary>
    [Fact]
    public async Task GetDayAsync_ScalonaSugestiaKonczySieNaOstatniejModyfikacji()
    {
        // 09:00 lokalnie, 50 minut czasu, ostatnia modyfikacja 10:20 lokalnie (08:20 UTC).
        SeedSuggestion(
            SuggestionStatus.Pending,
            At(6, 9),
            minutes: 50,
            lastActivityAtUtc: new DateTime(2026, 8, 6, 8, 20, 0, DateTimeKind.Utc));

        var items = await timeline.GetDayAsync(Day, CancellationToken.None);

        var item = Assert.Single(items);
        Assert.Equal(new DateTime(2026, 8, 6, 10, 20, 0), item.EndedAt);
        Assert.Equal(50, item.DurationMinutes);
    }

    /// <summary>
    /// Historia pliku niesie stan KAŻDEJ pozycji, która z niego powstała. Wcześniej stan
    /// podawał ten, kto historię otwierał, więc z karty sugestii nie było widać, że
    /// sąsiedni fragment tej samej historii jest już rozliczony, a z archiwum nie było
    /// widać sugestii czekających na decyzję.
    /// </summary>
    [Fact]
    public async Task GetDocumentHistoryAsync_KazdaPozycjaPlikuMaSwojStan()
    {
        var settled = SeedDocumentSuggestion(SuggestionStatus.Approved, At(6, 9));
        SeedEntry(At(6, 9), 60, archivedAt: Now, suggestion: settled, source: SuggestionSource.Document);
        var unsettled = SeedDocumentSuggestion(SuggestionStatus.Approved, At(6, 12));
        SeedEntry(At(6, 12), 30, suggestion: unsettled, source: SuggestionSource.Document);
        var pending = SeedDocumentSuggestion(SuggestionStatus.Pending, At(6, 15));
        var rejected = SeedDocumentSuggestion(SuggestionStatus.Rejected, At(6, 17));

        var history = await timeline.GetDocumentHistoryAsync("file-1", CancellationToken.None);

        Assert.Equal(
            ["settled", "unsettled", "pending", "rejected"],
            history.Sessions.Select(session => session.Kind));
        // Zatwierdzona sugestia NIE jest osobną pozycją — reprezentuje ją wpis. Inaczej
        // te same zapisy należałyby na ekranie do dwóch pozycji naraz.
        Assert.DoesNotContain(history.Sessions, session => session.SuggestionId == settled.Id);
        Assert.Equal(pending.Id, history.Sessions[2].SuggestionId);
        Assert.Equal(rejected.Id, history.Sessions[3].SuggestionId);
        // Numer sesji ten sam, który pozycja nosi na swojej karcie.
        Assert.Equal("edycja 1", history.Sessions[0].Label);
        Assert.Equal("edycja 3", history.Sessions[2].Label);
    }

    /// <summary>Pozycje innego pliku nie mają czego szukać w tej historii.</summary>
    [Fact]
    public async Task GetDocumentHistoryAsync_NieMieszaPlikow()
    {
        SeedDocumentSuggestion(SuggestionStatus.Pending, At(6, 9));
        SeedDocumentSuggestion(SuggestionStatus.Pending, At(6, 12), externalId: "file-2");

        var history = await timeline.GetDocumentHistoryAsync("file-1", CancellationToken.None);

        var session = Assert.Single(history.Sessions);
        Assert.Equal(At(6, 9), session.StartAt);
    }

    [Fact]
    public async Task GetRangeAsync_NieZwracaDniSpozaZakresu()
    {
        SeedEntry(At(4, 9), 60);

        var days = await timeline.GetRangeAsync(new DateOnly(2026, 8, 5), new DateOnly(2026, 8, 31), CancellationToken.None);

        Assert.Empty(days);
    }

    [Fact]
    public async Task GetDocumentActivityAsync_ChronologiaZPrzerwamiIOznaczeniemWykrytych()
    {
        // Wersje: 10:00, 10:05 (5 min — praca ciągła), 10:25 (20 min — przerwa wewnątrz
        // sesji), 11:25 (60 min — przerwa dzieląca sesje). Czasy UTC → strefa biznesowa.
        var baseUtc = new DateTime(2026, 8, 6, 8, 0, 0, DateTimeKind.Utc);
        db.DocumentActivities.AddRange(
            new DocumentActivity { ExternalId = "file-1", VersionId = "1.0", OccurredAt = baseUtc, Size = 100, RecordedAt = Now },
            new DocumentActivity { ExternalId = "file-1", VersionId = "2.0", OccurredAt = baseUtc.AddMinutes(5), Size = 150, RecordedAt = Now },
            new DocumentActivity { ExternalId = "file-1", VersionId = "3.0", OccurredAt = baseUtc.AddMinutes(25), Size = 200, RecordedAt = Now },
            new DocumentActivity { ExternalId = "file-1", VersionId = "4.0", OccurredAt = baseUtc.AddMinutes(85), Size = 300, RecordedAt = Now },
            new DocumentActivity { ExternalId = "file-2", VersionId = "1.0", OccurredAt = baseUtc, Size = 50, RecordedAt = Now });
        await db.SaveChangesAsync();

        var activity = await timeline.GetDocumentActivityAsync("file-1", CancellationToken.None);

        Assert.Equal(4, activity.Count);
        Assert.Equal(new DateTime(2026, 8, 6, 10, 0, 0), activity[0].OccurredAt);
        Assert.Null(activity[0].GapMinutesSincePrevious);

        // Odstęp poniżej progu ciągłości nie jest przerwą — o tym UI nic nie pisze.
        Assert.Equal(5, activity[1].GapMinutesSincePrevious);
        Assert.False(activity[1].IsSessionBreak);

        Assert.Equal(20, activity[2].GapMinutesSincePrevious);
        Assert.True(activity[2].IsSessionBreak);
        Assert.False(activity[2].SplitsSession);

        // Dłuższa przerwa też jest przerwą — i to ona rozcina pracę na dwie sesje.
        Assert.Equal(60, activity[3].GapMinutesSincePrevious);
        Assert.True(activity[3].IsSessionBreak);
        Assert.True(activity[3].SplitsSession);
    }

    [Fact]
    public async Task GetDayAsync_SortujePoGodzinieStartuIEtykietujeStatusy()
    {
        var approved = SeedSuggestion(SuggestionStatus.Approved, At(6, 14));
        SeedEntry(At(6, 14), 60, suggestion: approved);
        SeedEntry(At(6, 8), 30, archivedAt: Now);
        SeedSuggestion(SuggestionStatus.Pending, At(6, 11));

        var items = await timeline.GetDayAsync(Day, CancellationToken.None);

        Assert.Equal(3, items.Count);
        Assert.Equal(["archived", "pending", "active"], items.Select(item => item.Status));
        Assert.Equal([At(6, 8), At(6, 11), At(6, 14)], items.Select(item => item.StartedAt));
        Assert.Equal("suggestion", items[1].Type);
        Assert.Equal("timeEntry", items[2].Type);
    }
}
