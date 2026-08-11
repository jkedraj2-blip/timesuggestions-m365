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

        operations = new TimeEntryOperationsService(db, Options.Create(new SuggestionOptions()));
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

        var result = await operations.SubtractGapAsync(
            entry.Id, At(9, 30), At(9, 50), Now, CancellationToken.None);

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
        await operations.SubtractGapAsync(entry.Id, At(9, 30), At(9, 50), Now, CancellationToken.None);

        var rerun = await operations.SubtractGapAsync(
            entry.Id, At(9, 30), At(9, 50), Now, CancellationToken.None);

        Assert.Equal(TimeEntryOperationStatus.Conflict, rerun.Status);
        Assert.Equal(40, (await db.TimeEntries.SingleAsync()).DurationMinutes);
    }

    [Fact]
    public async Task SubtractGapAsync_OdmawiaDlaPrzerwySpozaListy()
    {
        var entry = SeedDocumentEntry("file-1", At(9), 60);

        var result = await operations.SubtractGapAsync(
            entry.Id, At(9, 30), At(9, 50), Now, CancellationToken.None);

        Assert.Equal(TimeEntryOperationStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task ClaimGapAsync_DoliczaWolnaLukePrzedWpisem()
    {
        SeedDocumentEntry("file-2", At(8), 30, description: "Poprzednia praca"); // 08:00–08:30
        var entry = SeedDocumentEntry("file-1", At(9), 60);                     // 09:00–10:00

        var result = await operations.ClaimGapAsync(
            entry.Id, GapDirection.Before, Now, CancellationToken.None);

        Assert.Equal(TimeEntryOperationStatus.Success, result.Status);
        var updated = await db.TimeEntries.Include(e => e.Adjustments)
            .SingleAsync(e => e.Id == entry.Id);
        // Luka 30 min doliczona serwerowo, wpis rozszerzony do początku luki.
        Assert.Equal(90, updated.DurationMinutes);
        Assert.Equal(At(8, 30), updated.StartedAt);
        var adjustment = Assert.Single(updated.Adjustments);
        Assert.Equal(AdjustmentKind.GapAddition, adjustment.Kind);
        Assert.Equal(30, adjustment.Minutes);
    }

    [Fact]
    public async Task ClaimGapAsync_LukaZajetaPrzezWpisANieJestDostepnaDlaWpisuB()
    {
        // Niezmiennik wprost: przerwa 08:30–09:00 doliczona do wpisu A wchodzi w jego
        // przedział, więc wpis B (10:00–10:30) nie może jej już doliczyć "przed sobą" —
        // jego sąsiadem staje się rozszerzony wpis A i luka między nimi jest inna.
        SeedDocumentEntry("file-2", At(8), 30);                  // 08:00–08:30
        var entryA = SeedDocumentEntry("file-1", At(9), 60);     // 09:00–10:00
        var entryB = SeedDocumentEntry("file-3", At(10, 30), 30); // 10:30–11:00

        await operations.ClaimGapAsync(entryA.Id, GapDirection.Before, Now, CancellationToken.None);
        var resultB = await operations.ClaimGapAsync(entryB.Id, GapDirection.Before, Now, CancellationToken.None);

        Assert.Equal(TimeEntryOperationStatus.Success, resultB.Status);
        var updatedB = await db.TimeEntries.SingleAsync(e => e.Id == entryB.Id);
        // B dostał wyłącznie lukę 10:00–10:30 — minuty 08:30–09:00 należą już do A.
        Assert.Equal(At(10), updatedB.StartedAt);
        Assert.Equal(60, updatedB.DurationMinutes);
        var updatedA = await db.TimeEntries.SingleAsync(e => e.Id == entryA.Id);
        Assert.Equal(At(8, 30), updatedA.StartedAt);
    }

    [Fact]
    public async Task ClaimGapAsync_409GdyBrakSasiada()
    {
        var entry = SeedDocumentEntry("file-1", At(9), 60);

        var result = await operations.ClaimGapAsync(
            entry.Id, GapDirection.Before, Now, CancellationToken.None);

        Assert.Equal(TimeEntryOperationStatus.Conflict, result.Status);
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

    [Fact]
    public async Task ApproveAsync_409GdyNowyWpisNachodziNaIstniejacy()
    {
        SeedDocumentEntry("file-1", At(9), 60, description: "Zajęte minuty"); // 09:00–10:00
        var suggestion = SeedPendingSuggestion(At(9, 30), 30);

        var result = await new ApprovalService(db).ApproveAsync(
            suggestion.Id,
            new ApproveSuggestionRequest { CaseId = 1, DurationMinutes = 30, Description = "Praca" },
            Now,
            CancellationToken.None);

        Assert.Equal(ApprovalOutcome.OverlapConflict, result.Outcome);
        Assert.Contains("Zajęte minuty", result.Error);
        Assert.Equal(1, await db.TimeEntries.CountAsync());
    }

    [Fact]
    public async Task ApproveAsync_SasiadujaceWpisyNieKoliduja()
    {
        // Przedziały półotwarte: koniec 10:00 i start 10:00 to nie nakład.
        SeedDocumentEntry("file-1", At(9), 60); // 09:00–10:00
        var suggestion = SeedPendingSuggestion(At(10), 30);

        var result = await new ApprovalService(db).ApproveAsync(
            suggestion.Id,
            new ApproveSuggestionRequest { CaseId = 1, DurationMinutes = 30, Description = "Praca" },
            Now,
            CancellationToken.None);

        Assert.Equal(ApprovalOutcome.Success, result.Outcome);
    }
}
