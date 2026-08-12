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
    TimeEntryOperationsService operationsService,
    TimeEntryViewService entryView) : ControllerBase
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

        // Przerwy i etykiety sesji liczone RAZ dla całej listy (dwa zapytania), a nie
        // per wpis — inaczej widok dnia z dwudziestoma wpisami robi czterdzieści rund.
        var views = await entryView.BuildManyAsync(entries, notice: null, cancellationToken);
        var viewById = views.ToDictionary(view => view.Id);

        // Grupowanie po dniach z sumami — UI dostaje gotowe liczby zamiast liczyć je samo.
        var days = entries
            .GroupBy(entry => entry.EntryDate)
            .Select(group => new TimeEntryDayDto(
                group.Key,
                group.Sum(entry => entry.DurationMinutes),
                group.Select(entry => viewById[entry.Id]).ToList()))
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

    /// <summary>
    /// Rozlicza pojedynczy wpis. Osobno od rozliczania zakresu dat, bo gotowość do
    /// faktury jest cechą wpisu, nie dnia: część pracy z danego dnia bywa jeszcze
    /// do sprawdzenia, a rozliczanie hurtem zabierało ją razem z gotową.
    /// </summary>
    [HttpPost("{id:int}/archive")]
    public async Task<ActionResult<TimeEntryDto>> ArchiveEntry(int id, CancellationToken cancellationToken)
        => await ToResponseAsync(
            await archiveService.ArchiveTimeEntryAsync(id, DateTime.UtcNow, cancellationToken),
            cancellationToken);

    /// <summary>Scala wpisy jednej sesji dokumentu; zwraca wynikowy wpis.</summary>
    [HttpPost("merge")]
    public async Task<ActionResult<TimeEntryDto>> Merge(
        MergeTimeEntriesRequest request,
        CancellationToken cancellationToken)
        => await ToResponseAsync(await operationsService.MergeAsync(
            request.TimeEntryIds, request.IncludeGaps, DateTime.UtcNow, cancellationToken), cancellationToken);

    /// <summary>Odwraca scalenie — przywraca wpisy składowe z ich sesji.</summary>
    [HttpPost("{id:int}/unmerge")]
    public async Task<ActionResult<List<TimeEntryDto>>> Unmerge(int id, CancellationToken cancellationToken)
    {
        var result = await operationsService.UnmergeAsync(id, DateTime.UtcNow, cancellationToken);
        return result.Status == TimeEntryOperationStatus.Success
            ? Ok(await entryView.BuildManyAsync(result.Entries!, notice: null, cancellationToken))
            : ToError(result);
    }

    /// <summary>Wyłącza przerwę z rozliczanego czasu wpisu (przerwa zostaje w jego godzinach).</summary>
    [HttpPost("{id:int}/subtract-gap")]
    public async Task<ActionResult<TimeEntryDto>> SubtractGap(
        int id,
        SubtractGapRequest request,
        CancellationToken cancellationToken)
        => await ToResponseAsync(await operationsService.SetGapCountedAsync(
            id, request.GapStartAt!.Value, request.GapEndAt!.Value, counted: false, DateTime.UtcNow, cancellationToken),
            cancellationToken);

    /// <summary>Dolicza z powrotem przerwę leżącą w godzinach wpisu (np. po scaleniu sesji).</summary>
    [HttpPost("{id:int}/add-gap")]
    public async Task<ActionResult<TimeEntryDto>> AddGap(
        int id,
        SubtractGapRequest request,
        CancellationToken cancellationToken)
        => await ToResponseAsync(await operationsService.SetGapCountedAsync(
            id, request.GapStartAt!.Value, request.GapEndAt!.Value, counted: true, DateTime.UtcNow, cancellationToken),
            cancellationToken);

    /// <summary>Zaokrągla czas wpisu do jednostki rozliczeniowej z konfiguracji.</summary>
    [HttpPost("{id:int}/round")]
    public async Task<ActionResult<TimeEntryDto>> Round(int id, CancellationToken cancellationToken)
        => await ToResponseAsync(
            await operationsService.RoundAsync(id, DateTime.UtcNow, cancellationToken), cancellationToken);

    /// <summary>Szybka korekta ±N minut.</summary>
    [HttpPost("{id:int}/adjust")]
    public async Task<ActionResult<TimeEntryDto>> Adjust(
        int id,
        AdjustTimeEntryRequest request,
        CancellationToken cancellationToken)
        => await ToResponseAsync(
            await operationsService.AdjustAsync(id, request.Minutes, DateTime.UtcNow, cancellationToken),
            cancellationToken);

    private async Task<ActionResult<TimeEntryDto>> ToResponseAsync(
        TimeEntryOperationResult result,
        CancellationToken cancellationToken)
        => result.Status == TimeEntryOperationStatus.Success
            ? Ok(await entryView.BuildAsync(result.Entries![0], notice: null, cancellationToken))
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
