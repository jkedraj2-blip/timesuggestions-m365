using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TimeSuggestions.Contracts;
using TimeSuggestions.Data;

namespace TimeSuggestions.Controllers;

[ApiController]
[Route("api/cases")]
public class CasesController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<CaseDto>>> GetActiveCases(CancellationToken cancellationToken)
    {
        var cases = await db.Cases
            .Where(legalCase => legalCase.IsActive)
            .OrderBy(legalCase => legalCase.Name)
            .ToListAsync(cancellationToken);

        return Ok(cases.Select(CaseDto.FromEntity).ToList());
    }
}
