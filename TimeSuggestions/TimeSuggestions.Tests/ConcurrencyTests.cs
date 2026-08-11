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
/// Współbieżność na DWÓCH osobnych kontekstach z osobnymi połączeniami do tego samego
/// PLIKU SQLite (baza in-memory bez cache=shared byłaby dla każdego połączenia inna) —
/// konflikty rozstrzyga indeks unikalny, a serwisy mapują je na jawne wyniki.
/// </summary>
public sealed class ConcurrencyTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc);

    private readonly string dbPath = Path.Combine(
        Path.GetTempPath(), $"timesuggestions-tests-{Guid.NewGuid():N}.db");

    public ConcurrencyTests()
    {
        using var db = CreateContext();
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = dbPath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private AppDbContext CreateContext() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={dbPath}").Options);

    private async Task<int> SeedPendingSuggestionAsync()
    {
        using var db = CreateContext();
        var suggestion = new Suggestion
        {
            Source = SuggestionSource.Calendar,
            ExternalId = "event-1",
            Title = "Spotkanie z Kowalski",
            StartedAt = Now.AddDays(-1),
            EntryDate = DateOnly.FromDateTime(Now.AddDays(-1)),
            DurationMinutes = 60,
            ProposedDescription = "Spotkanie z Kowalski",
            Status = SuggestionStatus.Pending,
            CreatedAt = Now,
        };
        db.Suggestions.Add(suggestion);
        await db.SaveChangesAsync();
        return suggestion.Id;
    }

    private static ApproveSuggestionRequest CreateApproveRequest() => new()
    {
        CaseId = 1,
        DurationMinutes = 60,
        Description = "Praca",
    };

    [Fact]
    public async Task ApproveAsync_KonfliktWspolbieznosciZwracaAlreadyApproved()
    {
        var suggestionId = await SeedPendingSuggestionAsync();

        // Symulacja przegranego wyścigu: nasz kontekst przyszpila stan Pending
        // (identity resolution EF odda tę samą, nieodświeżoną instancję), po czym
        // równoległe żądanie zatwierdza sugestię. Nasz UPDATE z tokenem
        // współbieżności na statusie trafia zero wierszy → jawny konflikt,
        // a wycofana transakcja nie zostawia drugiego wpisu.
        using var db = CreateContext();
        await db.Suggestions.SingleAsync(suggestion => suggestion.Id == suggestionId);

        using (var other = CreateContext())
        {
            var otherResult = await new ApprovalService(other)
                .ApproveAsync(suggestionId, CreateApproveRequest(), Now, CancellationToken.None);
            Assert.Equal(ApprovalOutcome.Success, otherResult.Outcome);
        }

        var result = await new ApprovalService(db)
            .ApproveAsync(suggestionId, CreateApproveRequest(), Now, CancellationToken.None);

        Assert.Equal(ApprovalOutcome.AlreadyApproved, result.Outcome);

        using var verification = CreateContext();
        Assert.Equal(1, await verification.TimeEntries.CountAsync());
    }

    [Fact]
    public async Task ApproveAsync_DwaRownolegleZatwierdzeniaTworzaDokladnieJedenWpis()
    {
        var suggestionId = await SeedPendingSuggestionAsync();

        using var db1 = CreateContext();
        using var db2 = CreateContext();

        var results = await Task.WhenAll(
            new ApprovalService(db1).ApproveAsync(suggestionId, CreateApproveRequest(), Now, CancellationToken.None),
            new ApprovalService(db2).ApproveAsync(suggestionId, CreateApproveRequest(), Now, CancellationToken.None));

        Assert.Equal(1, results.Count(result => result.Outcome == ApprovalOutcome.Success));
        // Przegrany dostaje AlreadyApproved (przy przeplocie odczytów) albo
        // SuggestionNotPending (gdy czytał już po cudzym zapisie) — nigdy Success.
        Assert.All(results.Where(result => result.Outcome != ApprovalOutcome.Success),
            result => Assert.Contains(result.Outcome,
                new[] { ApprovalOutcome.AlreadyApproved, ApprovalOutcome.SuggestionNotPending }));

        using var verification = CreateContext();
        Assert.Equal(1, await verification.TimeEntries.CountAsync());
    }

    [Fact]
    public async Task SyncAsync_DwaRownolegleSyncyTegoSamegoPayloaduDajaJednaSugestie()
    {
        var request1 = CreateRequestWithOneEvent();
        var request2 = CreateRequestWithOneEvent();

        using var db1 = CreateContext();
        using var db2 = CreateContext();
        var sync1 = new SyncService(db1, Options.Create(new SuggestionOptions()));
        var sync2 = new SyncService(db2, Options.Create(new SuggestionOptions()));

        // Retry po kodzie 2067 czyści ChangeTracker i wykonuje pełny merge od nowa —
        // oba żądania kończą się bez wyjątku.
        var reports = await Task.WhenAll(
            sync1.SyncAsync(request1, Now, CancellationToken.None),
            sync2.SyncAsync(request2, Now, CancellationToken.None));

        Assert.Equal(2, reports.Length);

        using var verification = CreateContext();
        Assert.Equal(1, await verification.Suggestions.CountAsync());
    }

    private static SyncRequest CreateRequestWithOneEvent() => new()
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
        ],
    };
}

/// <summary>
/// Rozpoznawanie kodu 2067 (SQLITE_CONSTRAINT_UNIQUE) — test negatywny gwarantuje,
/// że naruszenia FK i NOT NULL (ogólny kod 19) nie są mapowane na konflikt duplikatu.
/// </summary>
public class SqliteErrorsTests
{
    private const int SqliteConstraint = 19;
    private const int SqliteConstraintForeignKey = 787;
    private const int SqliteConstraintNotNull = 1299;

    [Fact]
    public void IsUniqueConstraintViolation_RozpoznajeKod2067()
    {
        var exception = new DbUpdateException(
            "unique", new SqliteException("UNIQUE constraint failed", SqliteConstraint, SqliteErrors.ConstraintUnique));

        Assert.True(SqliteErrors.IsUniqueConstraintViolation(exception));
    }

    [Theory]
    [InlineData(SqliteConstraintForeignKey)]
    [InlineData(SqliteConstraintNotNull)]
    [InlineData(SqliteConstraint)]
    public void IsUniqueConstraintViolation_NieMapujeInnychNaruszen(int extendedErrorCode)
    {
        var exception = new DbUpdateException(
            "constraint", new SqliteException("constraint failed", SqliteConstraint, extendedErrorCode));

        Assert.False(SqliteErrors.IsUniqueConstraintViolation(exception));
    }

    [Fact]
    public void IsUniqueConstraintViolation_NieMapujeWyjatkuBezSqliteException()
    {
        Assert.False(SqliteErrors.IsUniqueConstraintViolation(new DbUpdateException("plain")));
    }
}
