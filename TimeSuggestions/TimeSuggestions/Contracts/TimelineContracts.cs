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
    string Status,
    // Id pliku z Graph dla pozycji dokumentowych — poziom 3 osi (chronologia
    // modyfikacji i diff wersji) pobiera po nim historię; null dla kalendarza.
    string? ExternalId);

/// <summary>
/// Jedna wersja z chronologii modyfikacji dokumentu (poziom 3 osi czasu).
/// OccurredAt w strefie biznesowej — jak wszystkie czasy prezentacyjne.
/// </summary>
public record DocumentActivityDto(
    string VersionId,
    DateTime OccurredAt,
    long Size,
    // Przerwa od poprzedniej wersji; null dla pierwszej.
    int? GapMinutesSincePrevious,
    // true, gdy przerwa mieści się w przedziale wykrytej przerwy (15–30 min) —
    // UI oznacza ją tak samo jak przerwy przy wpisach.
    bool IsDetectedGapRange);
