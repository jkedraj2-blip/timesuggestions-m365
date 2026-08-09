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
    /// <summary>Górna granica czasu jednego wpisu — pełna doba.</summary>
    public const int MaxEntryDurationMinutes = 1440;

    public const int MaxDescriptionLength = 500;

    public int CaseId { get; set; }

    [Range(1, MaxEntryDurationMinutes, ErrorMessage = "Czas trwania musi mieścić się w zakresie 1–1440 minut.")]
    public int DurationMinutes { get; set; }

    [Required(AllowEmptyStrings = false, ErrorMessage = "Opis czynności jest wymagany.")]
    [MaxLength(MaxDescriptionLength, ErrorMessage = "Opis czynności może mieć najwyżej 500 znaków.")]
    public string Description { get; set; } = string.Empty;
}

public record CaseDto(int Id, string Name, string CaseNumber, string ClientName, IReadOnlyList<string> Keywords, bool IsActive)
{
    public static CaseDto FromEntity(Case legalCase) => new(
        legalCase.Id,
        legalCase.Name,
        legalCase.CaseNumber,
        legalCase.ClientName,
        legalCase.Keywords.Split(';', StringSplitOptions.RemoveEmptyEntries),
        legalCase.IsActive);
}

/// <summary>
/// Dane sprawy przy tworzeniu i edycji — słowa kluczowe jako lista, sklejane do formatu bazy.
/// Pola tekstowe są przycinane po stronie backendu (settery); wartości z samych białych
/// znaków stają się puste i odpadają na [Required]. Walidacja [Required]/[MaxLength]
/// działa już na wartościach przyciętych.
/// </summary>
public class CaseWriteRequest : IValidatableObject
{
    public const int MaxNameLength = 200;

    public const int MaxCaseNumberLength = 100;

    public const int MaxKeywordLength = 100;

    private string name = string.Empty;
    private string caseNumber = string.Empty;
    private string clientName = string.Empty;

    [Required(AllowEmptyStrings = false, ErrorMessage = "Nazwa sprawy jest wymagana.")]
    [MaxLength(MaxNameLength, ErrorMessage = "Nazwa sprawy może mieć najwyżej 200 znaków.")]
    public string Name { get => name; set => name = value?.Trim() ?? string.Empty; }

    [Required(AllowEmptyStrings = false, ErrorMessage = "Numer sprawy jest wymagany.")]
    [MaxLength(MaxCaseNumberLength, ErrorMessage = "Numer sprawy może mieć najwyżej 100 znaków.")]
    public string CaseNumber { get => caseNumber; set => caseNumber = value?.Trim() ?? string.Empty; }

    [Required(AllowEmptyStrings = false, ErrorMessage = "Nazwa klienta jest wymagana.")]
    [MaxLength(MaxNameLength, ErrorMessage = "Nazwa klienta może mieć najwyżej 200 znaków.")]
    public string ClientName { get => clientName; set => clientName = value?.Trim() ?? string.Empty; }

    public List<string> Keywords { get; set; } = [];

    /// <summary>
    /// Średnik to separator formatu bazy — słowo kluczowe z ';' odrzucamy jawnym błędem
    /// zamiast po cichu modyfikować dane użytkownika. MVC nie odrzuca null wewnątrz
    /// kolekcji, więc puste elementy łapiemy tu jawnie (400 zamiast 500).
    /// </summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var keyword in Keywords)
        {
            if (keyword is null)
            {
                yield return new ValidationResult(
                    "Lista słów kluczowych zawiera pusty element.", [nameof(Keywords)]);
                continue;
            }

            var trimmed = keyword.Trim();
            if (trimmed.Contains(';'))
            {
                // Komunikat nie odbija pełnej wartości wejściowej (niezaufane dane
                // dowolnej długości) — tylko krótki ucięty podgląd.
                yield return new ValidationResult(
                    $"Słowo kluczowe nie może zawierać średnika: \"{TruncateForMessage(trimmed)}\".",
                    [nameof(Keywords)]);
            }

            if (trimmed.Length > MaxKeywordLength)
            {
                yield return new ValidationResult(
                    "Pojedyncze słowo kluczowe może mieć najwyżej 100 znaków.", [nameof(Keywords)]);
            }
        }
    }

    private static string TruncateForMessage(string value)
    {
        if (value.Length <= 32)
        {
            return value;
        }

        // Cięcie nie może rozdzielić pary zastępczej UTF-16 — samotny surogat
        // zostałby zserializowany jako U+FFFD i zniekształcił podgląd.
        var length = char.IsHighSurrogate(value[31]) ? 31 : 32;
        return $"{value[..length]}…";
    }

    /// <summary>
    /// Format przechowywania: pojedyncza kolumna rozdzielana średnikiem (bez osobnej
    /// tabeli — YAGNI). Metoda, nie właściwość: walidacja modelu MVC czyta wszystkie
    /// publiczne właściwości, a getter rzucał NRE dla null w Keywords zanim
    /// Validate() zdążył zwrócić czytelny błąd 400.
    /// </summary>
    public string JoinKeywords() => string.Join(';', Keywords
        .Where(keyword => keyword is not null)
        .Select(keyword => keyword.Trim())
        .Where(keyword => keyword.Length > 0));
}

/// <summary>Liczniki dla kafelków podsumowania w nagłówku aplikacji.</summary>
public record SummaryDto(
    int PendingCount,
    int ApprovedCount,
    int RejectedCount,
    int UnsettledMinutes,
    int TodayLoggedMinutes,
    DateTime? LastSyncAt);

/// <summary>
/// Zakres rozliczenia hurtowego. Przedział domknięty dat DZIENNYCH (EntryDate jest
/// datą w strefie biznesowej, więc porównanie DateOnly nie ma problemów strefowych).
/// </summary>
public class ArchiveTimeEntriesRequest : IValidatableObject
{
    public const int MaxRangeDays = 366;

    [Required(ErrorMessage = "Data początkowa zakresu jest wymagana.")]
    public DateOnly? From { get; set; }

    [Required(ErrorMessage = "Data końcowa zakresu jest wymagana.")]
    public DateOnly? To { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (From is DateOnly from && To is DateOnly to)
        {
            if (to < from)
            {
                yield return new ValidationResult(
                    "Data końcowa nie może być wcześniejsza niż początkowa.", [nameof(To)]);
            }
            else if (to.DayNumber - from.DayNumber + 1 > MaxRangeDays)
            {
                yield return new ValidationResult(
                    "Zakres rozliczenia może obejmować najwyżej 366 dni.", [nameof(To)]);
            }
        }
    }
}

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
    DateTime? SourceStartedAt,
    DateTime? ArchivedAt)
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
        entry.Suggestion?.StartedAt,
        entry.ArchivedAt);
}
