using TimeSuggestions.Models;

namespace TimeSuggestions.Contracts;

/// <summary>
/// Liczniki jednego dnia osi czasu. Zatwierdzona sugestia liczy się RAZ — jako wpis
/// (Pending w PendingCount, wpisy w Active/Archived); odrzucone i zarchiwizowane
/// sugestie nie są pozycjami osi.
/// </summary>
public record TimelineDayDto(DateOnly Date, int PendingCount, int ActiveCount, int ArchivedCount);

/// <summary>Pozycja listy dnia: oczekująca sugestia albo wpis czasu.</summary>
public record TimelineItemDto(
    // "suggestion" | "timeEntry" — typ mówi UI, do której zakładki nawigować.
    string Type,
    int Id,
    SuggestionSource Source,
    DateTime StartedAt,
    DateTime EndedAt,
    int DurationMinutes,
    string Title,
    string? CaseName,
    string? CaseNumber,
    string? ClientName,
    // "pending" | "active" | "archived" — status koloruje i etykietuje pozycję
    // (nigdy samym kolorem); "archived" jest nieklikalne.
    string Status);
