using Microsoft.AspNetCore.Mvc;
using TimeSuggestions.Contracts;
using TimeSuggestions.Services;

namespace TimeSuggestions.Controllers;

[ApiController]
[Route("api/timeline")]
public class TimelineController(TimelineService timelineService) : ControllerBase
{
    /// <summary>Ten sam limit co przy rozliczaniu zakresu — oś czasu pyta o miesiąc, nie o dekadę.</summary>
    private const int MaxRangeDays = ArchiveTimeEntriesRequest.MaxRangeDays;

    [HttpGet]
    public async Task<ActionResult<List<TimelineDayDto>>> GetRange(
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        CancellationToken cancellationToken)
    {
        if (from is not DateOnly fromDate || to is not DateOnly toDate)
        {
            return BadRequest(new { message = "Parametry from i to (yyyy-MM-dd) są wymagane." });
        }

        if (toDate < fromDate)
        {
            return BadRequest(new { message = "Data końcowa nie może być wcześniejsza niż początkowa." });
        }

        if (toDate.DayNumber - fromDate.DayNumber + 1 > MaxRangeDays)
        {
            return BadRequest(new { message = "Zakres osi czasu może obejmować najwyżej 366 dni." });
        }

        return Ok(await timelineService.GetRangeAsync(fromDate, toDate, cancellationToken));
    }

    [HttpGet("{date}")]
    public async Task<ActionResult<List<TimelineItemDto>>> GetDay(
        DateOnly date,
        CancellationToken cancellationToken)
        => Ok(await timelineService.GetDayAsync(date, cancellationToken));

    /// <summary>
    /// Chronologia modyfikacji dokumentu razem z pozycjami, które z niej powstały
    /// (poziom 3 osi czasu). Id pliku w query, nie w ścieżce — identyfikatory Graph
    /// zawierają znaki wymagające kodowania. Jedno żądanie na obie rzeczy: stan pozycji
    /// jest częścią tej samej odpowiedzi, więc nie może rozjechać się z wersjami.
    /// </summary>
    [HttpGet("document-activity")]
    public async Task<ActionResult<DocumentHistoryDto>> GetDocumentActivity(
        [FromQuery] string? externalId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(externalId))
        {
            return BadRequest(new { message = "Parametr externalId jest wymagany." });
        }

        return Ok(await timelineService.GetDocumentHistoryAsync(externalId, cancellationToken));
    }
}
