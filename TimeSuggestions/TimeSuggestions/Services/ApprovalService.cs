using Microsoft.EntityFrameworkCore;
using TimeSuggestions.Contracts;
using TimeSuggestions.Data;
using TimeSuggestions.Models;

namespace TimeSuggestions.Services;

public enum ApprovalOutcome
{
    Success,
    SuggestionNotFound,
    SuggestionNotPending,
    CaseNotFound,
}

/// <summary>Jawny wynik zamiast wyjątków — kontroler tłumaczy go na kody HTTP.</summary>
public record ApprovalResult(ApprovalOutcome Outcome, TimeEntry? CreatedEntry = null);

/// <summary>
/// Zatwierdzanie i odrzucanie sugestii. Odrzucenie zmienia tylko status (bez usuwania
/// z bazy) — fizyczne usunięcie sprawiłoby, że kolejna synchronizacja przywróci sugestię.
/// </summary>
public class ApprovalService(AppDbContext db)
{
    public async Task<ApprovalResult> ApproveAsync(
        int suggestionId,
        ApproveSuggestionRequest request,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var suggestion = await db.Suggestions
            .FirstOrDefaultAsync(candidate => candidate.Id == suggestionId, cancellationToken);
        if (suggestion is null)
        {
            return new ApprovalResult(ApprovalOutcome.SuggestionNotFound);
        }

        if (suggestion.Status != SuggestionStatus.Pending)
        {
            return new ApprovalResult(ApprovalOutcome.SuggestionNotPending);
        }

        var selectedCase = await db.Cases
            .FirstOrDefaultAsync(legalCase => legalCase.Id == request.CaseId && legalCase.IsActive, cancellationToken);
        if (selectedCase is null)
        {
            return new ApprovalResult(ApprovalOutcome.CaseNotFound);
        }

        var timeEntry = new TimeEntry
        {
            CaseId = selectedCase.Id,
            Case = selectedCase,
            EntryDate = suggestion.EntryDate,
            DurationMinutes = request.DurationMinutes,
            Description = request.Description,
            CreatedFromSuggestion = true,
            Source = suggestion.Source,
            SuggestionId = suggestion.Id,
            CreatedAt = nowUtc,
        };

        suggestion.Status = SuggestionStatus.Approved;
        db.TimeEntries.Add(timeEntry);
        await db.SaveChangesAsync(cancellationToken);

        return new ApprovalResult(ApprovalOutcome.Success, timeEntry);
    }

    public async Task<ApprovalResult> RejectAsync(
        int suggestionId,
        CancellationToken cancellationToken)
    {
        var suggestion = await db.Suggestions
            .FirstOrDefaultAsync(candidate => candidate.Id == suggestionId, cancellationToken);
        if (suggestion is null)
        {
            return new ApprovalResult(ApprovalOutcome.SuggestionNotFound);
        }

        if (suggestion.Status != SuggestionStatus.Pending)
        {
            return new ApprovalResult(ApprovalOutcome.SuggestionNotPending);
        }

        suggestion.Status = SuggestionStatus.Rejected;
        await db.SaveChangesAsync(cancellationToken);

        return new ApprovalResult(ApprovalOutcome.Success);
    }
}
