using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TimeSuggestions.Contracts;
using TimeSuggestions.Controllers;
using TimeSuggestions.Data;

namespace TimeSuggestions.Tests;

/// <summary>Walidacja zarządzania sprawami — unikalność numeru i dezaktywacja zamiast usuwania.</summary>
public sealed class CasesControllerTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly AppDbContext db;
    private readonly CasesController controller;

    public CasesControllerTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        db = new AppDbContext(options);
        db.Database.EnsureCreated(); // seed 5 spraw z HasData

        controller = new CasesController(db);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }

    private static CaseWriteRequest CreateRequest(string caseNumber = "X-2026-999") => new()
    {
        Name = "Nowa sprawa testowa",
        CaseNumber = caseNumber,
        ClientName = "TestKlient",
        Keywords = ["alias1", "alias2"],
    };

    [Fact]
    public async Task Create_TworzySpraweZeSlowamiKluczowymi()
    {
        var result = await controller.Create(CreateRequest(), CancellationToken.None);

        var created = Assert.IsType<CaseDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(["alias1", "alias2"], created.Keywords);
        Assert.True(created.IsActive);
    }

    [Fact]
    public async Task Create_DuplikatNumeruSprawyZwraca409()
    {
        // "K-2026-001" istnieje w seedzie — duplikat to konflikt stanu (409), nie błąd żądania.
        var result = await controller.Create(CreateRequest("K-2026-001"), CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task Update_NumerZajetyPrzezInnaSpraweZwraca409()
    {
        var result = await controller.Update(2, CreateRequest("K-2026-001"), CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task Deactivate_UsuwaSpraweZListyAktywnych()
    {
        await controller.Deactivate(1, CancellationToken.None);

        var activeCases = await db.Cases.Where(legalCase => legalCase.IsActive).ToListAsync();
        Assert.DoesNotContain(activeCases, legalCase => legalCase.Id == 1);

        // Rekord nie został usunięty — historia wpisów czasu pozostaje spójna.
        Assert.NotNull(await db.Cases.FindAsync(1));
    }
}
