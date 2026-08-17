using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TimeSuggestions.Data;
using TimeSuggestions.Models;
using TimeSuggestions.Services;

namespace TimeSuggestions.Tests;

/// <summary>
/// Numeracja sesji pracy nad jednym plikiem. Bez niej lista pokazywała kilka pozycji
/// o identycznej nazwie i nie dało się poznać, o którą pracę chodzi. Numer biegnie przez
/// całą historię pliku, bo praca nad dokumentem rozkłada się na dowolnie wiele dni,
/// a sesja zaczęta przed północą kończy się już w następnym.
/// </summary>
public sealed class SessionLabelTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection connection;
    private readonly AppDbContext db;
    private readonly SessionLabelService labels;
    private readonly SuggestionOperationsService operations;

    public SessionLabelTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        db = new AppDbContext(options);
        db.Database.EnsureCreated();

        labels = new SessionLabelService(db);
        operations = new SuggestionOperationsService(db, TestHelpers.DefaultOptions());
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }

    private Suggestion AddSuggestion(
        int startHour,
        SuggestionStatus status = SuggestionStatus.Pending,
        string externalId = "file-1",
        int day = 24)
    {
        var startedAt = new DateTime(2026, 7, day, startHour, 0, 0);
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

    /// <summary>
    /// Numer należy się już pierwszej sesji. Pomijanie plików z jedną sesją sprawiało, że
    /// brak plakietki znaczył raz „to jedyna edycja", raz „ta pozycja wypadła z numeracji".
    /// </summary>
    [Fact]
    public async Task Pierwsza_SesjaDostajeNumerJeden()
    {
        var only = AddSuggestion(9);

        var result = await labels.LoadAsync([only], CancellationToken.None);

        Assert.Equal("edycja 1", result[only.Id]);
    }

    [Fact]
    public async Task Kilka_SesjiDostajeNumeryWKolejnosciKotwic()
    {
        var first = AddSuggestion(9);
        var second = AddSuggestion(12);
        var third = AddSuggestion(16);

        var result = await labels.LoadAsync([third, first, second], CancellationToken.None);

        Assert.Equal("edycja 1", result[first.Id]);
        Assert.Equal("edycja 2", result[second.Id]);
        Assert.Equal("edycja 3", result[third.Id]);
    }

    /// <summary>
    /// Sedno poprawki: numeracja biegnie przez całą historię pliku. Sesja zaczęta przed
    /// północą trafia datą do następnego dnia, więc numerowanie w obrębie doby dawało
    /// dwóm sąsiednim wieczornym sesjom numery z dwóch różnych pul — jedna wychodziła
    /// „pierwszą z dwóch", druga zostawała bez numeru.
    /// </summary>
    [Fact]
    public async Task Sesje_ZRoznychDniSaWJednejNumeracji()
    {
        var evening = AddSuggestion(23, day: 24);
        var afterMidnight = AddSuggestion(1, day: 25);

        var result = await labels.LoadAsync([evening, afterMidnight], CancellationToken.None);

        Assert.Equal("edycja 1", result[evening.Id]);
        Assert.Equal("edycja 2", result[afterMidnight.Id]);
    }

    /// <summary>
    /// Zatwierdzenie ani odrzucenie nie przenumerowuje reszty: numer ma wskazywać tę samą
    /// pracę niezależnie od tego, co prawnik zdążył już rozstrzygnąć.
    /// </summary>
    [Fact]
    public async Task Rozstrzygniete_SesjeNadalZajmujaSwojNumer()
    {
        AddSuggestion(9, SuggestionStatus.Approved);
        AddSuggestion(12, SuggestionStatus.Rejected);
        var pending = AddSuggestion(16);

        var result = await labels.LoadAsync([pending], CancellationToken.None);

        Assert.Equal("edycja 3", result[pending.Id]);
    }

    /// <summary>
    /// Po scaleniu wynik zachowuje numer WCZEŚNIEJSZEJ ze scalanych sesji (zostaje jej
    /// kotwica), a sesje po niej przesuwają się o jeden — bo realnie jest ich o jedną mniej.
    /// </summary>
    [Fact]
    public async Task Po_ScaleniuNumeryPrzesuwajaSieOJeden()
    {
        var first = AddSuggestion(9);
        var second = AddSuggestion(12);
        var third = AddSuggestion(16);

        await operations.MergeAsync([second.Id, third.Id], includeGaps: false, CancellationToken.None);

        var remaining = await db.Suggestions.ToListAsync();
        var result = await labels.LoadAsync(remaining, CancellationToken.None);
        Assert.Equal("edycja 1", result[first.Id]);
        Assert.Equal("edycja 2", result[second.Id]);
        Assert.DoesNotContain(third.Id, result.Keys);
    }

    [Fact]
    public async Task Inny_PlikMaWlasnaNumeracje()
    {
        var first = AddSuggestion(9);
        AddSuggestion(12);
        var other = AddSuggestion(10, externalId: "file-2");

        var result = await labels.LoadAsync([first, other], CancellationToken.None);

        Assert.Equal("edycja 1", result[first.Id]);
        Assert.Equal("edycja 1", result[other.Id]);
    }
}
