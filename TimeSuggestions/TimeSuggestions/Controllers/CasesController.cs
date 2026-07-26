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
        if (await CaseNumberTakenAsync(request.CaseNumber, excludeCaseId: null, cancellationToken))
        {
            return BadRequest(new { message = "Sprawa o tym numerze już istnieje." });
        }

        var legalCase = new Case
        {
            Name = request.Name,
            CaseNumber = request.CaseNumber,
            ClientName = request.ClientName,
            Keywords = request.JoinedKeywords,
            IsActive = true,
        };

        db.Cases.Add(legalCase);
        await db.SaveChangesAsync(cancellationToken);

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

        if (await CaseNumberTakenAsync(request.CaseNumber, excludeCaseId: id, cancellationToken))
        {
            return BadRequest(new { message = "Inna sprawa ma już ten numer." });
        }

        legalCase.Name = request.Name;
        legalCase.CaseNumber = request.CaseNumber;
        legalCase.ClientName = request.ClientName;
        legalCase.Keywords = request.JoinedKeywords;
        await db.SaveChangesAsync(cancellationToken);

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

    private Task<bool> CaseNumberTakenAsync(string caseNumber, int? excludeCaseId, CancellationToken cancellationToken)
        => db.Cases.AnyAsync(
            legalCase => legalCase.CaseNumber == caseNumber && (excludeCaseId == null || legalCase.Id != excludeCaseId),
            cancellationToken);
}
