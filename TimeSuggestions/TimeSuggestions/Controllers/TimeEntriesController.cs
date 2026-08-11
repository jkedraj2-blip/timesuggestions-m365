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
    ArchiveService archiveService,
    TimeEntryOperationsService operationsService) : ControllerBase
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
            .Include(entry => entry.Adjustments)
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

    /// <summary>Scala wpisy jednej sesji dokumentu; zwraca wynikowy wpis.</summary>
    [HttpPost("merge")]
    public async Task<ActionResult<TimeEntryDto>> Merge(
        MergeTimeEntriesRequest request,
        CancellationToken cancellationToken)
        => ToResponse(await operationsService.MergeAsync(
            request.TimeEntryIds, request.IncludeGaps, DateTime.UtcNow, cancellationToken));

    /// <summary>Odwraca scalenie — przywraca wpisy składowe z ich sesji.</summary>
    [HttpPost("{id:int}/unmerge")]
    public async Task<ActionResult<List<TimeEntryDto>>> Unmerge(int id, CancellationToken cancellationToken)
    {
        var result = await operationsService.UnmergeAsync(id, DateTime.UtcNow, cancellationToken);
        return result.Status == TimeEntryOperationStatus.Success
            ? Ok(result.Entries!.Select(TimeEntryDto.FromEntity).ToList())
            : ToError(result);
    }

    /// <summary>Odejmuje wykrytą przerwę (idempotentnie — druga próba na tę samą lukę → 409).</summary>
    [HttpPost("{id:int}/subtract-gap")]
    public async Task<ActionResult<TimeEntryDto>> SubtractGap(
        int id,
        SubtractGapRequest request,
        CancellationToken cancellationToken)
        => ToResponse(await operationsService.SubtractGapAsync(
            id, request.GapStartAt!.Value, request.GapEndAt!.Value, DateTime.UtcNow, cancellationToken));

    /// <summary>Dolicza wolną lukę do sąsiedniej pozycji na osi dnia — minuty liczy serwer.</summary>
    [HttpPost("{id:int}/claim-gap")]
    public async Task<ActionResult<TimeEntryDto>> ClaimGap(
        int id,
        ClaimGapRequest request,
        CancellationToken cancellationToken)
        => ToResponse(await operationsService.ClaimGapAsync(
            id, request.Direction!.Value, DateTime.UtcNow, cancellationToken));

    /// <summary>Szybka korekta ±N minut.</summary>
    [HttpPost("{id:int}/adjust")]
    public async Task<ActionResult<TimeEntryDto>> Adjust(
        int id,
        AdjustTimeEntryRequest request,
        CancellationToken cancellationToken)
        => ToResponse(await operationsService.AdjustAsync(id, request.Minutes, DateTime.UtcNow, cancellationToken));

    private ActionResult<TimeEntryDto> ToResponse(TimeEntryOperationResult result)
        => result.Status == TimeEntryOperationStatus.Success
            ? Ok(TimeEntryDto.FromEntity(result.Entries![0]))
            : ToError(result);

    private ActionResult ToError(TimeEntryOperationResult result) => result.Status switch
    {
        TimeEntryOperationStatus.NotFound => NotFound(new { message = result.Message }),
        // Archiwum blokuje operacje tak samo jak edycję — spójnie 409.
        TimeEntryOperationStatus.Archived => Conflict(new { message = result.Message }),
        TimeEntryOperationStatus.Conflict => Conflict(new { message = result.Message }),
        TimeEntryOperationStatus.Invalid => BadRequest(new { message = result.Message }),
        _ => StatusCode(StatusCodes.Status500InternalServerError),
    };

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
