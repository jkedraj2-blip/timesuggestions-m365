using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using TimeSuggestions.Configuration;
using TimeSuggestions.Contracts;
using TimeSuggestions.Services;

namespace TimeSuggestions.Controllers;

[ApiController]
[Route("api/summary")]
public class SummaryController(SummaryService summaryService, IOptions<SuggestionOptions> options) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<SummaryDto>> GetSummary(CancellationToken cancellationToken)
    {
        // "Dzisiaj" w strefie biznesowej — daty wpisów pochodzą z lokalnych czasów wydarzeń.
        var today = SummaryService.GetBusinessToday(DateTime.UtcNow, options.Value.BusinessTimeZoneId);
        return Ok(await summaryService.GetSummaryAsync(today, cancellationToken));
    }
}
