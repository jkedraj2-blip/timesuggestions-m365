using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TimeSuggestions.Contracts;
using TimeSuggestions.Data;
using TimeSuggestions.Services;

namespace TimeSuggestions.Controllers;

[ApiController]
[Route("api/time-entries")]
public class TimeEntriesController(
    AppDbContext db,
    ApprovalService approvalService,
    ArchiveService archiveService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<TimeEntriesResponse>> GetTimeEntries(
        [FromQuery] bool archived = false,
        CancellationToken cancellationToken = default)
    {
        // Domyślnie widok aktywnych — istniejący klient bez parametru dostaje
        // dotychczasowe zachowanie. TotalMinutes dotyczy zwróconego widoku.
        var entries = await db.TimeEntries
            .Where(entry => archived ? entry.ArchivedAt != null : entry.ArchivedAt == null)
            .Include(entry => entry.Case)
            .Include(entry => entry.Suggestions)
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

    [HttpPost("archive")]
    public async Task<ActionResult<ArchiveTimeEntriesResult>> Archive(
        ArchiveTimeEntriesRequest request,
        CancellationToken cancellationToken)
    {
        // [Required] gwarantuje wartości — walidacja modelu odrzuciła braki przed wejściem tutaj.
        var result = await archiveService.ArchiveTimeEntriesAsync(
            request.From!.Value, request.To!.Value, DateTime.UtcNow, cancellationToken);

        return Ok(result);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        var result = await approvalService.DeleteTimeEntryAsync(id, cancellationToken);

        return result.Outcome switch
        {
            ApprovalOutcome.Success => NoContent(),
            ApprovalOutcome.TimeEntryNotFound => NotFound(new { message = "Wpis czasu nie istnieje." }),
            ApprovalOutcome.TimeEntryArchived => Conflict(new { message = "Wpis jest rozliczony — nie można cofnąć zatwierdzenia." }),
            _ => StatusCode(StatusCodes.Status500InternalServerError),
        };
    }
}
