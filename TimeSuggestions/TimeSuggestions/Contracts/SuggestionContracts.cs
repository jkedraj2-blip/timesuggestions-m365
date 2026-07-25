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
    IReadOnlyList<string> MatchCandidates,
    string ProposedDescription,
    SuggestionStatus Status)
{
    /// <summary>
    /// Dla sugestii niejednoznacznych przekazujemy nazwy pasujących spraw —
    /// UI mówi użytkownikowi konkretnie "pasuje do X i Y", a nie tylko "sprawdź to".
    /// </summary>
    public static SuggestionDto FromEntity(Suggestion suggestion, IReadOnlyList<string>? matchCandidates = null) => new(
        suggestion.Id,
        suggestion.Source,
        suggestion.Title,
        suggestion.StartedAt,
        suggestion.DurationMinutes,
        suggestion.CaseId,
        suggestion.Case?.Name,
        suggestion.IsAmbiguous,
        matchCandidates ?? [],
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

public record CaseDto(int Id, string Name, string CaseNumber, string ClientName, IReadOnlyList<string> Keywords)
{
    public static CaseDto FromEntity(Case legalCase) => new(
        legalCase.Id,
        legalCase.Name,
        legalCase.CaseNumber,
        legalCase.ClientName,
        legalCase.Keywords.Split(';', StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>Liczniki dla kafelków podsumowania w nagłówku aplikacji.</summary>
public record SummaryDto(
    int PendingCount,
    int ApprovedCount,
    int RejectedCount,
    int TotalLoggedMinutes,
    int TodayLoggedMinutes,
    DateTime? LastSyncAt);

/// <summary>Wpisy pogrupowane po dniach z sumami — widok "Wpisy czasu" pokazuje je od razu policzone.</summary>
public record TimeEntriesResponse(int TotalMinutes, IReadOnlyList<TimeEntryDayDto> Days);

public record TimeEntryDayDto(DateOnly Date, int TotalMinutes, IReadOnlyList<TimeEntryDto> Entries);

public record TimeEntryDto(
    int Id,
    int CaseId,
    string? CaseName,
    DateOnly EntryDate,
    int DurationMinutes,
    string Description,
    bool CreatedFromSuggestion,
    SuggestionSource Source,
    int SuggestionId,
    string? SourceTitle,
    DateTime? SourceStartedAt)
{
    /// <summary>
    /// SourceTitle/SourceStartedAt to kotwica wpisu w realnym zdarzeniu (tytuł spotkania
    /// lub nazwa pliku) — opis mógł zostać nadpisany przez użytkownika przy zatwierdzaniu.
    /// </summary>
    public static TimeEntryDto FromEntity(TimeEntry entry) => new(
        entry.Id,
        entry.CaseId,
        entry.Case?.Name,
        entry.EntryDate,
        entry.DurationMinutes,
        entry.Description,
        entry.CreatedFromSuggestion,
        entry.Source,
        entry.SuggestionId,
        entry.Suggestion?.Title,
        entry.Suggestion?.StartedAt);
}
