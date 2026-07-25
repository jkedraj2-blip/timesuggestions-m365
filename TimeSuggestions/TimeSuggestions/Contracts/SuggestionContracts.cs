using System.ComponentModel.DataAnnotations;
using TimeSuggestions.Models;

namespace TimeSuggestions.Contracts;

/// <summary>Sugestia w kształcie dla interfejsu — spłaszczona nazwa sprawy zamiast pełnej encji.</summary>
public record SuggestionDto(
    int Id,
    SuggestionSource Source,
    string Title,
    DateTime StartedAt,
    int DurationMinutes,
    int? CaseId,
    string? CaseName,
    bool IsAmbiguous,
    string ProposedDescription,
    SuggestionStatus Status)
{
    public static SuggestionDto FromEntity(Suggestion suggestion) => new(
        suggestion.Id,
        suggestion.Source,
        suggestion.Title,
        suggestion.StartedAt,
        suggestion.DurationMinutes,
        suggestion.CaseId,
        suggestion.Case?.Name,
        suggestion.IsAmbiguous,
        suggestion.ProposedDescription,
        suggestion.Status);
}

/// <summary>Wartości finalne wpisu — edycja to zatwierdzenie z poprawionymi wartościami (jeden endpoint).</summary>
public class ApproveSuggestionRequest
{
    [Required]
    public int CaseId { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "Czas trwania musi być większy od zera.")]
    public int DurationMinutes { get; set; }

    [Required(AllowEmptyStrings = false, ErrorMessage = "Opis czynności jest wymagany.")]
    public string Description { get; set; } = string.Empty;
}

public record CaseDto(int Id, string Name, string CaseNumber, string ClientName)
{
    public static CaseDto FromEntity(Case legalCase)
        => new(legalCase.Id, legalCase.Name, legalCase.CaseNumber, legalCase.ClientName);
}

public record TimeEntryDto(
    int Id,
    int CaseId,
    string? CaseName,
    DateOnly EntryDate,
    int DurationMinutes,
    string Description,
    bool CreatedFromSuggestion,
    SuggestionSource Source,
    int SuggestionId)
{
    public static TimeEntryDto FromEntity(TimeEntry entry) => new(
        entry.Id,
        entry.CaseId,
        entry.Case?.Name,
        entry.EntryDate,
        entry.DurationMinutes,
        entry.Description,
        entry.CreatedFromSuggestion,
        entry.Source,
        entry.SuggestionId);
}
