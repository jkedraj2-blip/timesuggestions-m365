using Microsoft.AspNetCore.Mvc;
using TimeSuggestions.Contracts;
using TimeSuggestions.Services;

namespace TimeSuggestions.Controllers;

[ApiController]
[Route("api/summary")]
public class SummaryController(SummaryService summaryService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<SummaryDto>> GetSummary(CancellationToken cancellationToken)
    {
        // Czas lokalny serwera — daty wpisów pochodzą z lokalnych czasów wydarzeń.
        var today = DateOnly.FromDateTime(DateTime.Now);
        return Ok(await summaryService.GetSummaryAsync(today, cancellationToken));
    }
}
