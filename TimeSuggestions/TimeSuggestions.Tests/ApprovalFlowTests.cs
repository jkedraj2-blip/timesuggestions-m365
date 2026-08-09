using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TimeSuggestions.Contracts;
using TimeSuggestions.Data;
using TimeSuggestions.Models;
using TimeSuggestions.Services;

namespace TimeSuggestions.Tests;

/// <summary>
/// Cykl życia decyzji: odrzucenie i przywrócenie, zatwierdzenie i cofnięcie wpisu.
/// Pomyłka użytkownika nie może być nieodwracalna — te testy to gwarantują.
/// </summary>
public sealed class ApprovalFlowTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection connection;
    private readonly AppDbContext db;
    private readonly ApprovalService approvalService;

    public ApprovalFlowTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        db = new AppDbContext(options);
        db.Database.EnsureCreated();

        approvalService = new ApprovalService(db);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }

    private async Task<Suggestion> SeedPendingSuggestionAsync()
    {
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
        return suggestion;
    }

    [Fact]
    public async Task RestoreAsync_PrzywracaOdrzuconaSugestieDoOczekujacych()
    {
        var suggestion = await SeedPendingSuggestionAsync();
        await approvalService.RejectAsync(suggestion.Id, CancellationToken.None);

        var result = await approvalService.RestoreAsync(suggestion.Id, CancellationToken.None);

        Assert.Equal(ApprovalOutcome.Success, result.Outcome);
        Assert.Equal(SuggestionStatus.Pending, (await db.Suggestions.SingleAsync()).Status);
    }

    [Fact]
    public async Task RestoreAsync_OdmawiaDlaSugestiiKtoraNieJestOdrzucona()
    {
        var suggestion = await SeedPendingSuggestionAsync();

        var result = await approvalService.RestoreAsync(suggestion.Id, CancellationToken.None);

        Assert.Equal(ApprovalOutcome.SuggestionNotRejected, result.Outcome);
    }

    [Fact]
    public async Task DeleteTimeEntryAsync_UsuwaWpisIPrzywracaSugestieDoOczekujacych()
    {
        var suggestion = await SeedPendingSuggestionAsync();
        var request = new ApproveSuggestionRequest { CaseId = 1, DurationMinutes = 60, Description = "Praca" };
        var approval = await approvalService.ApproveAsync(suggestion.Id, request, Now, CancellationToken.None);

        var result = await approvalService.DeleteTimeEntryAsync(approval.CreatedEntry!.Id, CancellationToken.None);

        Assert.Equal(ApprovalOutcome.Success, result.Outcome);
        Assert.Empty(await db.TimeEntries.ToListAsync());
        Assert.Equal(SuggestionStatus.Pending, (await db.Suggestions.SingleAsync()).Status);
    }

    [Fact]
    public async Task DeleteTimeEntryAsync_ZwracaBrakDlaNieistniejacegoWpisu()
    {
        var result = await approvalService.DeleteTimeEntryAsync(999, CancellationToken.None);

        Assert.Equal(ApprovalOutcome.TimeEntryNotFound, result.Outcome);
    }

    [Fact]
    public async Task DeleteTimeEntryAsync_OdmawiaDlaZarchiwizowanegoWpisuINiczegoNieZmienia()
    {
        var suggestion = await SeedPendingSuggestionAsync();
        var request = new ApproveSuggestionRequest { CaseId = 1, DurationMinutes = 60, Description = "Praca" };
        var approval = await approvalService.ApproveAsync(suggestion.Id, request, Now, CancellationToken.None);
        approval.CreatedEntry!.ArchivedAt = Now;
        await db.SaveChangesAsync();

        var result = await approvalService.DeleteTimeEntryAsync(approval.CreatedEntry.Id, CancellationToken.None);

        // Rozliczonego czasu nie cofamy: wpis zostaje, sugestia dalej zatwierdzona.
        Assert.Equal(ApprovalOutcome.TimeEntryArchived, result.Outcome);
        Assert.Equal(1, await db.TimeEntries.CountAsync());
        Assert.Equal(SuggestionStatus.Approved, (await db.Suggestions.SingleAsync()).Status);
    }
}
