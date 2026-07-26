using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TimeSuggestions.Contracts;
using TimeSuggestions.Data;
using TimeSuggestions.Services;

namespace TimeSuggestions.Controllers;

[ApiController]
[Route("api/time-entries")]
public class TimeEntriesController(AppDbContext db, ApprovalService approvalService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<TimeEntriesResponse>> GetTimeEntries(CancellationToken cancellationToken)
    {
        var entries = await db.TimeEntries
            .Include(entry => entry.Case)
            .Include(entry => entry.Suggestion)
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

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        var result = await approvalService.DeleteTimeEntryAsync(id, cancellationToken);

        return result.Outcome switch
        {
            ApprovalOutcome.Success => NoContent(),
            ApprovalOutcome.TimeEntryNotFound => NotFound(new { message = "Wpis czasu nie istnieje." }),
            _ => StatusCode(StatusCodes.Status500InternalServerError),
        };
    }
}
