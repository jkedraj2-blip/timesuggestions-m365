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
    SuggestionNotRejected,
    CaseNotFound,
    TimeEntryNotFound,

    /// <summary>Równoległe żądanie zdążyło utworzyć wpis dla tej samej sugestii — konflikt, nie duplikat.</summary>
    AlreadyApproved,
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
            Suggestion = suggestion,
            CreatedAt = nowUtc,
        };

        suggestion.Status = SuggestionStatus.Approved;
        db.TimeEntries.Add(timeEntry);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqliteErrors.IsUniqueConstraintViolation(exception))
        {
            // Indeks unikalny TimeEntries.SuggestionId: równoległe zatwierdzenie zdążyło
            // wstawić wpis między naszym odczytem a zapisem. Mapujemy wyłącznie
            // SQLITE_CONSTRAINT_UNIQUE (2067) — inne błędy zapisu propagują bez maskowania.
            return new ApprovalResult(ApprovalOutcome.AlreadyApproved);
        }

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

    /// <summary>Przywraca odrzuconą sugestię na listę oczekujących — pomyłka nie może być nieodwracalna.</summary>
    public async Task<ApprovalResult> RestoreAsync(int suggestionId, CancellationToken cancellationToken)
    {
        var suggestion = await db.Suggestions
            .FirstOrDefaultAsync(candidate => candidate.Id == suggestionId, cancellationToken);
        if (suggestion is null)
        {
            return new ApprovalResult(ApprovalOutcome.SuggestionNotFound);
        }

        if (suggestion.Status != SuggestionStatus.Rejected)
        {
            return new ApprovalResult(ApprovalOutcome.SuggestionNotRejected);
        }

        suggestion.Status = SuggestionStatus.Pending;
        await db.SaveChangesAsync(cancellationToken);

        return new ApprovalResult(ApprovalOutcome.Success);
    }

    /// <summary>
    /// Usuwa wpis czasu i przywraca powiązaną sugestię do oczekujących.
    /// Jedno SaveChanges = jedna transakcja: albo oba kroki, albo żaden.
    /// </summary>
    public async Task<ApprovalResult> DeleteTimeEntryAsync(int timeEntryId, CancellationToken cancellationToken)
    {
        var timeEntry = await db.TimeEntries
            .Include(entry => entry.Suggestion)
            .FirstOrDefaultAsync(entry => entry.Id == timeEntryId, cancellationToken);
        if (timeEntry is null)
        {
            return new ApprovalResult(ApprovalOutcome.TimeEntryNotFound);
        }

        if (timeEntry.Suggestion is not null)
        {
            timeEntry.Suggestion.Status = SuggestionStatus.Pending;
        }

        db.TimeEntries.Remove(timeEntry);
        await db.SaveChangesAsync(cancellationToken);

        return new ApprovalResult(ApprovalOutcome.Success);
    }
}
