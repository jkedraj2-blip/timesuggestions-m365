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
    public async Task<ActionResult<TimeEntriesResponse>> GetTimeEntries(CancellationToken cancellationToken)
    {
        var entries = await db.TimeEntries
            .Include(entry => entry.Case)
            .OrderByDescending(entry => entry.EntryDate)
            .ThenByDescending(entry => entry.Id)
            .ToListAsync(cancellationToken);

        // Grupowanie po dniach z sumami — UI dostaje gotowe liczby zamiast liczyć je samo.
        var days = entries
            .GroupBy(entry => entry.EntryDate)
            .Select(group => new TimeEntryDayDto(
                group.Key,
                group.Sum(entry => entry.DurationMinutes),
                group.Select(TimeEntryDto.FromEntity).ToList()))
            .ToList();

        return Ok(new TimeEntriesResponse(entries.Sum(entry => entry.DurationMinutes), days));
    }
}
