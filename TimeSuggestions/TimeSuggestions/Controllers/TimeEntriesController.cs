using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TimeSuggestions.Contracts;
using TimeSuggestions.Data;

namespace TimeSuggestions.Controllers;

[ApiController]
[Route("api/time-entries")]
public class TimeEntriesController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<TimeEntryDto>>> GetTimeEntries(CancellationToken cancellationToken)
    {
        var entries = await db.TimeEntries
            .Include(entry => entry.Case)
            .OrderByDescending(entry => entry.EntryDate)
            .ThenByDescending(entry => entry.Id)
            .ToListAsync(cancellationToken);

        return Ok(entries.Select(TimeEntryDto.FromEntity).ToList());
    }
}
