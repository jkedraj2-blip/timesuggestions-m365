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
/// Chronologia pliku RAZEM ze wszystkimi pozycjami, które z niej powstały. Wcześniej
/// odpowiedź niosła same wersje, a stan pozycji podawał ten, kto historię otwierał —
/// więc z karty sugestii nie było widać, że sąsiedni fragment tej samej historii jest
/// już rozliczonym wpisem, a z archiwum nie było widać sugestii oczekujących. Ta sama
/// historia opowiadała dwie różne rzeczy zależnie od miejsca otwarcia.
/// </summary>
public record DocumentHistoryDto(
    IReadOnlyList<DocumentActivityDto> Versions,
    IReadOnlyList<DocumentSessionDto> Sessions);

/// <summary>
/// Jedna pozycja powstała z tego pliku: sugestia albo wpis czasu, razem ze swoim
/// zasięgiem na osi biznesowej i przerwami. Sugestia ZATWIERDZONA nie jest osobną
/// pozycją — reprezentuje ją wpis, który z niej powstał; inaczej te same zapisy
/// należałyby na ekranie do dwóch pozycji naraz.
/// </summary>
public record DocumentSessionDto(
    // "pending" | "rejected" | "archived" | "unsettled" | "settled" — stan decyduje
    // o kolorze obszaru i o treści plakietki; nigdy sam kolor.
    string Kind,
    DateTime StartAt,
    DateTime EndAt,
    int? SuggestionId,
    int? TimeEntryId,
    // „edycja 3" — ten sam numer, który pozycja nosi na swojej karcie.
    string? Label,
    IReadOnlyList<DetectedGapDto> Gaps);

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
    // Przerwa dłuższa niż próg ciągłości pracy: przestój, o którym trzeba coś
    // powiedzieć. Krótsze odstępy to pisanie z pauzą na myślenie — UI zostawia je
    // szare i bez opisu, bo rozliczenia nie zmieniają.
    bool IsSessionBreak,
    // Przerwa dłuższa niż próg dzielenia sesji, czyli taka, która rozcina pracę na dwie
    // osobne. Rozstrzyga stan przerwy tam, gdzie pozycja nie przysłała własnej listy:
    // przerwa dzieląca sesje z definicji nie jest wliczona w czas, krótsza siedzi
    // w czasie brutto i jest.
    bool SplitsSession,
    // true dla KAŻDEGO wiersza należącego do najnowszej wersji pliku — nie tylko dla
    // ostatniego. Wersja jeszcze niezapieczętowana pojawia się w dzienniku wiele razy
    // (ten sam numer, kolejne momenty), a jej treści Graph nie wydaje spod adresu
    // wersji (400 invalidRequest); trzeba ją brać z endpointu elementu.
    bool IsCurrent,
    // Ten sam numer wersji co wiersz wyżej: kolejna próbka wciąż otwartej wersji.
    // Graph przechowuje jeden snapshot na numer, więc stan pośredni nie istnieje
    // i nie ma czego porównywać — UI nie pokazuje wtedy przycisku.
    bool IsSameVersionAsPrevious);
