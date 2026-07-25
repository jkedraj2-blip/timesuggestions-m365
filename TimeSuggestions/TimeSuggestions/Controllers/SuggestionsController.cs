using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TimeSuggestions.Contracts;
using TimeSuggestions.Data;
using TimeSuggestions.Models;
using TimeSuggestions.Services;

namespace TimeSuggestions.Controllers;

[ApiController]
[Route("api/suggestions")]
public class SuggestionsController(AppDbContext db, ApprovalService approvalService) : ControllerBase
{
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

        var suggestions = await query
            .OrderBy(suggestion => suggestion.StartedAt)
            .ToListAsync(cancellationToken);

        // Kandydaci dla niejednoznacznych liczeni w locie z logiki czystej —
        // encja celowo nie przechowuje listy trafień (bez dodatkowej tabeli).
        var activeCases = suggestions.Any(suggestion => suggestion.IsAmbiguous)
            ? await db.Cases.Where(legalCase => legalCase.IsActive).ToListAsync(cancellationToken)
            : [];

        return Ok(suggestions
            .Select(suggestion => SuggestionDto.FromEntity(suggestion, GetMatchCandidates(suggestion, activeCases)))
            .ToList());
    }

    private static IReadOnlyList<string>? GetMatchCandidates(Suggestion suggestion, List<Case> activeCases)
    {
        if (!suggestion.IsAmbiguous)
        {
            return null;
        }

        return CaseMatcher.Match(suggestion.Title, activeCases)
            .Candidates
            .Select(candidate => candidate.Name)
            .ToList();
    }

    [HttpPost("{id:int}/approve")]
    public async Task<ActionResult<TimeEntryDto>> Approve(
        int id,
        ApproveSuggestionRequest request,
        CancellationToken cancellationToken)
    {
        var result = await approvalService.ApproveAsync(id, request, DateTime.UtcNow, cancellationToken);

        return result.Outcome switch
        {
            ApprovalOutcome.Success => Ok(TimeEntryDto.FromEntity(result.CreatedEntry!)),
            ApprovalOutcome.SuggestionNotFound => NotFound(new { message = "Sugestia nie istnieje." }),
            ApprovalOutcome.SuggestionNotPending => Conflict(new { message = "Sugestia została już rozstrzygnięta." }),
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
}
