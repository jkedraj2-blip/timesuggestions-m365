using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TimeSuggestions.Contracts;
using TimeSuggestions.Data;
using TimeSuggestions.Models;

namespace TimeSuggestions.Controllers;

[ApiController]
[Route("api/cases")]
public class CasesController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<CaseDto>>> GetCases(
        [FromQuery] bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        var query = db.Cases.AsQueryable();
        if (!includeInactive)
        {
            query = query.Where(legalCase => legalCase.IsActive);
        }

        var cases = await query
            .OrderBy(legalCase => legalCase.Name)
            .ToListAsync(cancellationToken);

        return Ok(cases.Select(CaseDto.FromEntity).ToList());
    }

    [HttpPost]
    public async Task<ActionResult<CaseDto>> Create(CaseWriteRequest request, CancellationToken cancellationToken)
    {
        // Wstępne sprawdzenie daje przyjazny komunikat; wyścig check-then-insert
        // domyka indeks unikalny na CaseNumber (naruszenie mapowane niżej na 409).
        if (await FindCaseNumberOwnerAsync(request.CaseNumber, excludeCaseId: null, cancellationToken) is Case conflicting)
        {
            return Conflict(new { message = CaseNumberConflictMessage(conflicting) });
        }

        var legalCase = new Case
        {
            Name = request.Name,
            CaseNumber = request.CaseNumber,
            ClientName = request.ClientName,
            Keywords = request.JoinKeywords(),
            IsActive = true,
        };

        db.Cases.Add(legalCase);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqliteErrors.IsUniqueConstraintViolation(exception))
        {
            // Ścieżka wyścigu: numer zajęto między sprawdzeniem a zapisem. Nie mamy tu
            // kolidującej encji, więc komunikat pozostaje ogólny (wariant skrajnie rzadki).
            return Conflict(new { message = "Sprawa o tym numerze już istnieje." });
        }

        return Ok(CaseDto.FromEntity(legalCase));
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<CaseDto>> Update(int id, CaseWriteRequest request, CancellationToken cancellationToken)
    {
        var legalCase = await db.Cases.FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (legalCase is null)
        {
            return NotFound(new { message = "Sprawa nie istnieje." });
        }

        if (await FindCaseNumberOwnerAsync(request.CaseNumber, excludeCaseId: id, cancellationToken) is Case conflicting)
        {
            return Conflict(new { message = CaseNumberConflictMessage(conflicting) });
        }

        legalCase.Name = request.Name;
        legalCase.CaseNumber = request.CaseNumber;
        legalCase.ClientName = request.ClientName;
        legalCase.Keywords = request.JoinKeywords();

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqliteErrors.IsUniqueConstraintViolation(exception))
        {
            // Jak wyżej: wyścig, bez dostępu do kolidującej encji.
            return Conflict(new { message = "Inna sprawa ma już ten numer." });
        }

        return Ok(CaseDto.FromEntity(legalCase));
    }

    // Celowo brak DELETE: wpisy czasu wskazują na sprawę kluczem obcym, a dane
    // rozliczeniowe muszą przetrwać. Dezaktywacja usuwa sprawę z dopasowywania
    // i z list wyboru, nie niszcząc historii.
    [HttpPost("{id:int}/deactivate")]
    public Task<IActionResult> Deactivate(int id, CancellationToken cancellationToken)
        => SetActiveAsync(id, isActive: false, cancellationToken);

    [HttpPost("{id:int}/activate")]
    public Task<IActionResult> Activate(int id, CancellationToken cancellationToken)
        => SetActiveAsync(id, isActive: true, cancellationToken);

    private async Task<IActionResult> SetActiveAsync(int id, bool isActive, CancellationToken cancellationToken)
    {
        var legalCase = await db.Cases.FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (legalCase is null)
        {
            return NotFound(new { message = "Sprawa nie istnieje." });
        }

        legalCase.IsActive = isActive;
        await db.SaveChangesAsync(cancellationToken);

        return NoContent();
    }

    /// <summary>
    /// Sprawa trzymająca dany numer, także nieaktywna: numer identyfikuje sprawę
    /// w historii rozliczeń, więc dezaktywacja go nie zwalnia (recykling numeru
    /// uczyniłby stare wpisy czasu dwuznacznymi).
    /// </summary>
    private Task<Case?> FindCaseNumberOwnerAsync(string caseNumber, int? excludeCaseId, CancellationToken cancellationToken)
        => db.Cases.FirstOrDefaultAsync(
            legalCase => legalCase.CaseNumber == caseNumber && (excludeCaseId == null || legalCase.Id != excludeCaseId),
            cancellationToken);

    /// <summary>
    /// Komunikat konfliktu numeru. Wskazuje NAZWĘ kolidującej sprawy (dane z bazy,
    /// ograniczone długością), zamiast powtarzać wpisany numer — komunikaty błędów
    /// nie odbijają danych wejściowych. Nieaktywność musi paść wprost: domyślna lista
    /// pokazuje wyłącznie sprawy aktywne, więc bez tego użytkownik szuka na ekranie
    /// kolizji, której nie ma jak zobaczyć, i wygląda to jak błąd aplikacji.
    /// </summary>
    private static string CaseNumberConflictMessage(Case conflicting)
        => conflicting.IsActive
            ? $"Ten numer ma już sprawa \"{conflicting.Name}\"."
            : $"Ten numer ma już sprawa \"{conflicting.Name}\", obecnie nieaktywna. "
                + "Aktywuj ją ponownie albo użyj innego numeru.";
}
