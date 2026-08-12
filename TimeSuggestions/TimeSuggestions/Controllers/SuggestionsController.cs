using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TimeSuggestions.Configuration;
using TimeSuggestions.Contracts;
using TimeSuggestions.Data;
using TimeSuggestions.Models;
using TimeSuggestions.Services;

namespace TimeSuggestions.Controllers;

[ApiController]
[Route("api/suggestions")]
public class SuggestionsController(
    AppDbContext db,
    ApprovalService approvalService,
    ArchiveService archiveService,
    SuggestionOperationsService operationsService,
    SessionLabelService sessionLabels,
    TimeEntryViewService entryView,
    IOptions<SuggestionOptions> optionsAccessor) : ControllerBase
{
    /// <summary>
    /// Strefa biznesowa: DTO podaje godziny na jednej osi (StartedAt i ostatnia
    /// modyfikacja), choć encja trzyma ostatnią modyfikację w UTC.
    /// </summary>
    private readonly TimeZoneInfo businessTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById(optionsAccessor.Value.BusinessTimeZoneId);

    [HttpGet]
    public async Task<ActionResult<List<SuggestionDto>>> GetSuggestions(
        [FromQuery] SuggestionStatus status = SuggestionStatus.Pending,
        [FromQuery] SuggestionSource? source = null,
        CancellationToken cancellationToken = default)
    {
        var query = db.Suggestions
            .Include(suggestion => suggestion.Case)
            .Where(suggestion => suggestion.Status == status);

        if (source is not null)
        {
            query = query.Where(suggestion => suggestion.Source == source);
        }

        // Kolejność po OSTATNIEJ MODYFIKACJI, malejąco. Sortowanie po StartedAt stawiało
        // dokument tam, gdzie prawnik zaczął nad nim pracować rano — więc plik zmieniony
        // przed chwilą tkwił w środku listy i wyglądał na nieaktualny. Początek sesji
        // pozostaje na karcie, ale o miejscu w kolejce decyduje ostatni zapis.
        var suggestions = await query
            .OrderByDescending(suggestion => suggestion.LastActivityAt)
            .ThenByDescending(suggestion => suggestion.StartedAt)
            .ToListAsync(cancellationToken);

        // Kandydaci dla niejednoznacznych liczeni w locie z logiki czystej —
        // encja celowo nie przechowuje listy trafień (bez dodatkowej tabeli).
        var activeCases = suggestions.Any(suggestion => suggestion.IsAmbiguous)
            ? await db.Cases.Where(legalCase => legalCase.IsActive).ToListAsync(cancellationToken)
            : [];

        var gaps = await operationsService.LoadGapsAsync(suggestions, cancellationToken);
        var labels = await sessionLabels.LoadAsync(suggestions, cancellationToken);

        return Ok(suggestions
            .Select(suggestion => SuggestionDto.FromEntity(
                suggestion,
                businessTimeZone,
                GetMatchCandidates(suggestion, activeCases),
                gaps.GetValueOrDefault(suggestion.Id),
                labels.GetValueOrDefault(suggestion.Id)))
            .ToList());
    }

    /// <summary>
    /// Scala sesje tego samego dokumentu w jedną sugestię — prawnik nie musi zatwierdzać
    /// osobno i scalać dopiero wpisów.
    /// </summary>
    [HttpPost("merge")]
    public async Task<ActionResult<List<SuggestionDto>>> Merge(
        MergeSuggestionsRequest request,
        CancellationToken cancellationToken)
        => ToResponse(await operationsService.MergeAsync(
            request.SuggestionIds, request.IncludeGaps, cancellationToken));

    /// <summary>Rozdziela wolną lukę między tę sugestię a sąsiednią — podział podaje użytkownik.</summary>
    [HttpPost("{id:int}/claim-gap")]
    public async Task<ActionResult<List<SuggestionDto>>> ClaimGap(
        int id,
        ClaimSuggestionGapRequest request,
        CancellationToken cancellationToken)
        => ToResponse(await operationsService.ClaimGapAsync(
            id, request.Direction!.Value, request.Minutes, request.NeighborMinutes, cancellationToken));

    /// <summary>
    /// Wynik operacji na kod HTTP. Zwracamy zmienione sugestie bez luk i kandydatów —
    /// UI i tak przeładowuje listę, żeby zobaczyć skutki na całej osi dnia.
    /// </summary>
    private ActionResult<List<SuggestionDto>> ToResponse(SuggestionOperationResult result) => result.Status switch
    {
        TimeEntryOperationStatus.Success =>
            Ok(result.Suggestions!
                .Select(suggestion => SuggestionDto.FromEntity(suggestion, businessTimeZone))
                .ToList()),
        TimeEntryOperationStatus.NotFound => NotFound(new { message = result.Message }),
        TimeEntryOperationStatus.Conflict => Conflict(new { message = result.Message }),
        TimeEntryOperationStatus.Invalid => BadRequest(new { message = result.Message }),
        _ => StatusCode(StatusCodes.Status500InternalServerError),
    };

    private static IReadOnlyList<string>? GetMatchCandidates(Suggestion suggestion, List<Case> activeCases)
    {
        if (!suggestion.IsAmbiguous)
        {
            return null;
        }

        // Tryb normalizacji zgodny ze źródłem sugestii — tytuł sugestii dokumentowej
        // to nazwa pliku, kalendarzowej — tytuł spotkania.
        var textSource = suggestion.Source == SuggestionSource.Document
            ? MatchTextSource.DocumentName
            : MatchTextSource.MeetingTitle;

        // Numer sprawy w nawiasie: to moment, w którym użytkownik rozstrzyga między
        // podobnymi sprawami, a numer jest jednoznaczny tam, gdzie nazwy bywają mylące.
        return CaseMatcher.Match(suggestion.Title, activeCases, textSource)
            .Candidates
            .Select(candidate => $"{candidate.Name} ({candidate.CaseNumber})")
            .ToList();
    }

    [HttpPost("{id:int}/approve")]
    public async Task<ActionResult<TimeEntryDto>> Approve(
        int id,
        ApproveSuggestionRequest request,
        CancellationToken cancellationToken)
    {
        var result = await approvalService.ApproveAsync(id, request, DateTime.UtcNow, cancellationToken);

        if (result.Outcome == ApprovalOutcome.Success)
        {
            // Notice nie jest błędem: wpis powstał, a prawnik dostaje zdanie o tym, co
            // stało się z jego godzinami (przycięcie do sąsiada, pozostałe pokrycie).
            return Ok(await entryView.BuildAsync(result.CreatedEntry!, result.Notice, cancellationToken));
        }

        return result.Outcome switch
        {
            ApprovalOutcome.SuggestionNotFound => NotFound(new { message = "Sugestia nie istnieje." }),
            ApprovalOutcome.SuggestionNotPending => Conflict(new { message = "Sugestia została już rozstrzygnięta." }),
            ApprovalOutcome.AlreadyApproved => Conflict(new { message = "Sugestia została już zatwierdzona w innym żądaniu." }),
            ApprovalOutcome.CaseNotFound => BadRequest(new { message = "Wskazana sprawa nie istnieje lub jest nieaktywna." }),
            _ => StatusCode(StatusCodes.Status500InternalServerError),
        };
    }

    [HttpPost("{id:int}/reject")]
    public async Task<IActionResult> Reject(int id, CancellationToken cancellationToken)
    {
        var result = await approvalService.RejectAsync(id, cancellationToken);

        return result.Outcome switch
        {
            ApprovalOutcome.Success => NoContent(),
            ApprovalOutcome.SuggestionNotFound => NotFound(new { message = "Sugestia nie istnieje." }),
            ApprovalOutcome.SuggestionNotPending => Conflict(new { message = "Sugestia została już rozstrzygnięta." }),
            _ => StatusCode(StatusCodes.Status500InternalServerError),
        };
    }

    [HttpPost("{id:int}/restore")]
    public async Task<IActionResult> Restore(int id, CancellationToken cancellationToken)
    {
        var result = await approvalService.RestoreAsync(id, cancellationToken);

        return result.Outcome switch
        {
            ApprovalOutcome.Success => NoContent(),
            ApprovalOutcome.SuggestionNotFound => NotFound(new { message = "Sugestia nie istnieje." }),
            ApprovalOutcome.SuggestionNotRejected => Conflict(new { message = "Przywrócić można tylko odrzuconą sugestię." }),
            _ => StatusCode(StatusCodes.Status500InternalServerError),
        };
    }

    [HttpPost("archive-rejected")]
    public async Task<ActionResult<ArchiveSuggestionsResult>> ArchiveRejected(CancellationToken cancellationToken)
    {
        return Ok(await archiveService.ArchiveRejectedSuggestionsAsync(cancellationToken));
    }

    [HttpPost("{id:int}/archive")]
    public async Task<IActionResult> Archive(int id, CancellationToken cancellationToken)
    {
        var result = await archiveService.ArchiveSuggestionAsync(id, cancellationToken);

        return result.Outcome switch
        {
            ApprovalOutcome.Success => NoContent(),
            ApprovalOutcome.SuggestionNotFound => NotFound(new { message = "Sugestia nie istnieje." }),
            ApprovalOutcome.SuggestionNotRejected => Conflict(new { message = "Zarchiwizować można tylko odrzuconą sugestię." }),
            _ => StatusCode(StatusCodes.Status500InternalServerError),
        };
    }
}
