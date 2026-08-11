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
/// Model sesji dokumentowych (etap 1): klucz dedupu z kotwicą sesji, relacja
/// wiele-sugestii-do-jednego-wpisu i godziny na wpisie. Baza SQLite in-memory,
/// bo klucze i relacje domykają indeksy oraz FK.
/// </summary>
public sealed class SessionModelTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection connection;
    private readonly AppDbContext db;

    public SessionModelTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        db = new AppDbContext(options);
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }

    private static Suggestion CreateDocumentSuggestion(string externalId, DateTime sessionAnchor) => new()
    {
        Source = SuggestionSource.Document,
        ExternalId = externalId,
        Title = "Umowa_NovaTech.docx",
        StartedAt = sessionAnchor,
        SessionAnchor = sessionAnchor,
        EntryDate = DateOnly.FromDateTime(sessionAnchor),
        DurationMinutes = 30,
        ProposedDescription = "Praca nad dokumentem",
        Status = SuggestionStatus.Pending,
        CreatedAt = Now,
    };

    [Fact]
    public async Task KluczDedupu_DopuszczaWieleSesjiJednegoPlikaTegoSamegoDnia()
    {
        // Dawny klucz (Source, ExternalId, EntryDate) blokował drugą sesję na plik
        // dziennie — kotwica sesji zdejmuje to ograniczenie.
        db.Suggestions.Add(CreateDocumentSuggestion("file-1", new DateTime(2026, 7, 24, 9, 0, 0)));
        db.Suggestions.Add(CreateDocumentSuggestion("file-1", new DateTime(2026, 7, 24, 14, 0, 0)));

        await db.SaveChangesAsync();

        Assert.Equal(2, await db.Suggestions.CountAsync());
    }

    [Fact]
    public async Task KluczDedupu_TaSamaKotwicaTegoSamegoPlikuNaruszaUnikalnosc()
    {
        var anchor = new DateTime(2026, 7, 24, 9, 0, 0);
        db.Suggestions.Add(CreateDocumentSuggestion("file-1", anchor));
        await db.SaveChangesAsync();

        db.Suggestions.Add(CreateDocumentSuggestion("file-1", anchor));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.True(SqliteErrors.IsUniqueConstraintViolation(exception));
    }

    [Fact]
    public async Task ApproveAsync_PrzenosiGodzinyNaWpisIWiazeSugestiePrzezTimeEntryId()
    {
        var suggestion = CreateDocumentSuggestion("file-1", new DateTime(2026, 7, 24, 9, 0, 0));
        db.Suggestions.Add(suggestion);
        await db.SaveChangesAsync();

        var result = await new ApprovalService(db).ApproveAsync(
            suggestion.Id,
            new ApproveSuggestionRequest { CaseId = 1, DurationMinutes = 45, Description = "Praca" },
            Now,
            CancellationToken.None);

        Assert.Equal(ApprovalOutcome.Success, result.Outcome);
        var entry = await db.TimeEntries.SingleAsync();
        Assert.Equal(new DateTime(2026, 7, 24, 9, 0, 0), entry.StartedAt);
        Assert.Equal(new DateTime(2026, 7, 24, 9, 45, 0), entry.EndedAt);
        Assert.Equal(entry.Id, (await db.Suggestions.SingleAsync()).TimeEntryId);
    }

    [Fact]
    public async Task DeleteTimeEntryAsync_PrzywracaWszystkieSugestieScalonegoWpisu()
    {
        // Wpis scalony z dwóch sesji: cofnięcie zatwierdzenia przywraca OBIE sugestie
        // do oczekujących i zeruje ich powiązanie.
        var first = CreateDocumentSuggestion("file-1", new DateTime(2026, 7, 24, 9, 0, 0));
        var second = CreateDocumentSuggestion("file-1", new DateTime(2026, 7, 24, 14, 0, 0));
        first.Status = SuggestionStatus.Approved;
        second.Status = SuggestionStatus.Approved;
        var entry = new TimeEntry
        {
            CaseId = 1,
            EntryDate = new DateOnly(2026, 7, 24),
            StartedAt = new DateTime(2026, 7, 24, 9, 0, 0),
            EndedAt = new DateTime(2026, 7, 24, 15, 0, 0),
            DurationMinutes = 90,
            Description = "Praca",
            CreatedFromSuggestion = true,
            Source = SuggestionSource.Document,
            Suggestions = [first, second],
            CreatedAt = Now,
        };
        db.TimeEntries.Add(entry);
        await db.SaveChangesAsync();

        var result = await new ApprovalService(db).DeleteTimeEntryAsync(entry.Id, CancellationToken.None);

        Assert.Equal(ApprovalOutcome.Success, result.Outcome);
        Assert.Equal(0, await db.TimeEntries.CountAsync());
        var suggestions = await db.Suggestions.ToListAsync();
        Assert.Equal(2, suggestions.Count);
        Assert.All(suggestions, restored =>
        {
            Assert.Equal(SuggestionStatus.Pending, restored.Status);
            Assert.Null(restored.TimeEntryId);
        });
    }

    [Fact]
    public async Task Sync_DrugaSesjaTegoSamegoDniaNieNadpisujePierwszej()
    {
        // Symulacja przyszłego zachowania silnika sesji na poziomie merge:
        // kandydat z inną kotwicą tego samego pliku i dnia tworzy NOWĄ sugestię.
        var syncService = new SyncService(db, Options.Create(new SuggestionOptions()));
        var request = new SyncRequest
        {
            DriveFiles =
            [
                new DriveFileDto
                {
                    Id = "file-1",
                    Name = "Umowa_NovaTech.docx",
                    LastModifiedDateTime = Now.AddHours(-2),
                    LastModifiedByMe = true,
                },
            ],
        };

        await syncService.SyncAsync(request, Now, CancellationToken.None);
        var rerun = await syncService.SyncAsync(request, Now, CancellationToken.None);

        // Fallback bez wersji: kotwica dzienna → powtórny sync nie duplikuje.
        Assert.Equal(0, rerun.Created);
        Assert.Equal(1, await db.Suggestions.CountAsync());
    }
}
