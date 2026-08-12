using System.ComponentModel.DataAnnotations;

namespace TimeSuggestions.Contracts;

/// <summary>
/// Surowe dane z Microsoft Graph przysyłane przez frontend.
/// Backend celowo nie woła Graph sam — token użytkownika nigdy nie opuszcza przeglądarki.
/// </summary>
public class SyncRequest : IValidatableObject
{
    public List<CalendarEventDto> CalendarEvents { get; set; } = [];

    public List<DriveFileDto> DriveFiles { get; set; } = [];

    /// <summary>
    /// Identyfikatory plików usuniętych z OneDrive (tombstone'y z delta query) —
    /// backend usuwa ich OCZEKUJĄCE sugestie. Pole opcjonalne: starszy frontend
    /// go nie wysyła, a pusta lista nie zmienia zachowania.
    /// </summary>
    public List<string> DeletedDriveFileIds { get; set; } = [];

    /// <summary>
    /// Liczniki pozycji odfiltrowanych po stronie przeglądarki, zanim payload
    /// powstał (prywatność: tytuły prywatnych wydarzeń nie opuszczają przeglądarki;
    /// dokumenty: frontend filtruje delta feed przed wysyłką). Opcjonalne —
    /// starszy frontend go nie wysyła, a zerowe liczniki nic nie zmieniają.
    /// </summary>
    public ClientFilteredCounts ClientFilteredCounts { get; set; } = new();

    /// <summary>
    /// Czy CalendarEvents to KOMPLETNY snapshot okna synchronizacji (wszystkie strony
    /// @odata.nextLink pobrane bez błędu). Tylko wtedy backend wykonuje destrukcyjną
    /// część rekonsyliacji kalendarza (usuwanie oczekujących sugestii spotkań
    /// nieobecnych w payloadzie). Domyślnie false — klient, który pola nie przysłał
    /// (starsza wersja, przerwane stronicowanie), nie może skasować prawidłowych sugestii.
    /// </summary>
    public bool CalendarSnapshotComplete { get; set; }

    /// <summary>
    /// Ile ostatnich pełnych dni lokalnych pokrywa zadeklarowany kompletny snapshot
    /// kalendarza. Backend ogranicza destrukcyjną rekonsyliację do PRZECIĘCIA tego
    /// zakresu ze swoim oknem (Suggestions:SyncDaysBack) — rozjazd konfiguracji
    /// okien frontend/backend zawęża kasowanie, zamiast kasować oczekujące sugestie
    /// spotkań, których klient w ogóle nie pobrał. Brak wartości przy zadeklarowanej
    /// kompletności (starszy klient) = zakres nieznany — część destrukcyjna się
    /// nie uruchamia.
    /// </summary>
    [Range(1, 365, ErrorMessage = "Zakres snapshotu kalendarza musi mieścić się w zakresie 1–365 dni.")]
    public int? CalendarSnapshotDaysBack { get; set; }

    /// <summary>
    /// Opcjonalne nadpisanie okna synchronizacji (preferencja użytkownika z UI).
    /// Brak wartości = obowiązuje konfiguracja backendu (Suggestions:SyncDaysBack).
    /// Górny limit chroni przed żądaniem wieloletniej historii.
    /// </summary>
    [Range(1, Configuration.SuggestionOptions.MaxSyncDaysBack,
        ErrorMessage = "Okno synchronizacji musi mieścić się w zakresie 1–90 dni.")]
    public int? SyncDaysBack { get; set; }

    /// <summary>
    /// Ile pobrań historii wersji nie powiodło się po stronie klienta (per plik).
    /// Backend nie widzi nieudanych żądań Graph — bez tego licznika raport wersji
    /// pokazywałby "plik bez historii" nie odróżniając błędu od braku wersji.
    /// </summary>
    [Range(0, ClientFilteredCounts.MaxCount,
        ErrorMessage = "Licznik błędów pobierania wersji musi być z zakresu 0–100000.")]
    public int DriveFileVersionFetchErrors { get; set; }

    /// <summary>
    /// Elementy listy tombstone'ów walidowane jak pozostałe identyfikatory z Graph.
    /// MVC nie odrzuca null WEWNĄTRZ kolekcji (waliduje tylko niepuste elementy),
    /// więc puste pozycje łapiemy tu jawnie — czytelny 400 zamiast 500 w logice.
    /// </summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (CalendarEvents.Any(calendarEvent => calendarEvent is null))
        {
            yield return new ValidationResult(
                "Lista wydarzeń kalendarza zawiera pusty element.", [nameof(CalendarEvents)]);
        }

        if (DriveFiles.Any(file => file is null))
        {
            yield return new ValidationResult(
                "Lista plików zawiera pusty element.", [nameof(DriveFiles)]);
        }

        if (DriveFiles.Any(file => file?.Versions?.Any(version => version is null) == true))
        {
            yield return new ValidationResult(
                "Lista wersji pliku zawiera pusty element.", [nameof(DriveFiles)]);
        }

        // IsNullOrWhiteSpace: identyfikator z samych spacji jest tak samo bezużyteczny jak pusty.
        if (DeletedDriveFileIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > CalendarEventDto.MaxTextLength))
        {
            yield return new ValidationResult(
                "Identyfikator usuniętego pliku jest pusty albo za długi.", [nameof(DeletedDriveFileIds)]);
        }
    }
}

/// <summary>Wydarzenie z kalendarza Outlook (podzbiór pól Graph potrzebny logice).</summary>
public class CalendarEventDto
{
    /// <summary>Górna granica długości pól tekstowych z Graph — ochrona przed absurdalnie dużym payloadem.</summary>
    public const int MaxTextLength = 2000;

    /// <summary>
    /// Sensitivity to krótka wartość słownikowa Graph (normal/personal/private/
    /// confidential) — limit z dużym zapasem, ale bez przyjmowania kilobajtów.
    /// </summary>
    public const int MaxSensitivityLength = 256;

    [Required]
    [MaxLength(MaxTextLength, ErrorMessage = "Identyfikator wydarzenia jest za długi.")]
    public required string Id { get; set; }

    [MaxLength(MaxTextLength, ErrorMessage = "Tytuł wydarzenia jest za długi.")]
    public string? Subject { get; set; }

    public DateTime StartDateTime { get; set; }

    public DateTime EndDateTime { get; set; }

    public bool IsAllDay { get; set; }

    [MaxLength(MaxSensitivityLength, ErrorMessage = "Pole sensitivity jest za długie.")]
    public string? Sensitivity { get; set; }

    /// <summary>Anulowane spotkania nie są czasem przepracowanym — backend je odrzuca.</summary>
    public bool IsCancelled { get; set; }
}

/// <summary>Plik z OneDrive (podzbiór pól Graph potrzebny logice).</summary>
public class DriveFileDto
{
    [Required]
    [MaxLength(CalendarEventDto.MaxTextLength, ErrorMessage = "Identyfikator pliku jest za długi.")]
    public required string Id { get; set; }

    [Required]
    [MaxLength(CalendarEventDto.MaxTextLength, ErrorMessage = "Nazwa pliku jest za długa.")]
    public required string Name { get; set; }

    public DateTime LastModifiedDateTime { get; set; }

    /// <summary>Czy modyfikacji dokonał zalogowany użytkownik — frontend ustala to porównując konto MSAL z lastModifiedBy.</summary>
    public bool LastModifiedByMe { get; set; }

    /// <summary>
    /// Rozmiar pliku w bajtach — trafia do dziennika razem z próbką z elementu
    /// (DocumentActivity.ItemObservationLabel) jako pomocniczy sygnał skali zmiany.
    /// Starszy klient nie przysyła nic; zero jest wtedy poprawną wartością „nie wiem".
    /// </summary>
    [Range(0, long.MaxValue, ErrorMessage = "Rozmiar pliku nie może być ujemny.")]
    public long Size { get; set; }

    /// <summary>
    /// Historia wersji pliku (GET /me/drive/items/{id}/versions) pobrana przez frontend.
    /// null = wersje niepobrane (starszy klient albo błąd Graph dla tego pliku) —
    /// backend buduje wtedy sugestię fallbackową z minimum czasu i oznaczeniem
    /// „czas do uzupełnienia". Pusta lista to co innego niż null: Graph odpowiedział,
    /// ale bez historii.
    /// </summary>
    public List<DriveFileVersionDto>? Versions { get; set; }
}

/// <summary>Jedna wersja pliku z historii Graph — surowy fakt do dziennika DocumentActivity.</summary>
public class DriveFileVersionDto
{
    [Required]
    [MaxLength(CalendarEventDto.MaxTextLength, ErrorMessage = "Identyfikator wersji jest za długi.")]
    public required string VersionId { get; set; }

    public DateTime LastModifiedDateTime { get; set; }

    [Range(0, long.MaxValue, ErrorMessage = "Rozmiar wersji nie może być ujemny.")]
    public long Size { get; set; }
}

/// <summary>
/// Liczniki filtrów klienckich — backend nie widzi odfiltrowanych pozycji
/// (celowo, prywatność), więc dolicza deklarowane liczby do raportu, aby ten
/// pokazywał prawdę. Wartości walidowane jako nieujemne i rozsądnie ograniczone.
/// </summary>
public class ClientFilteredCounts
{
    public const int MaxCount = 100_000;

    private const string RangeMessage = "Licznik filtrowania klienckiego musi być z zakresu 0–100000.";

    [Range(0, MaxCount, ErrorMessage = RangeMessage)]
    public int Private { get; set; }

    [Range(0, MaxCount, ErrorMessage = RangeMessage)]
    public int Cancelled { get; set; }

    [Range(0, MaxCount, ErrorMessage = RangeMessage)]
    public int DocumentsNotOfficeDocument { get; set; }
}

/// <summary>
/// Ile pozycji weszło do rozliczenia jako kandydaci — pobrane z Graph MINUS te spoza
/// okna rozliczenia. Zapas pobierany ponad okno (patrz SyncFilteredOutCounts) nie jest
/// ani pobrany w tym sensie, ani pominięty; dzięki temu nadal zachodzi niezmiennik
/// raportu: pobrano = utworzone + zaktualizowane + pominięte (już istniały)
/// + odfiltrowane + zagregowane + zdeduplikowane.
/// </summary>
public record SyncFetchedCounts(int CalendarEvents, int DriveFiles);

/// <summary>
/// Liczniki odrzuceń per reguła — UI tłumaczy użytkownikowi, czemu pozycje odpadły.
///
/// Pozycji spoza okna rozliczenia NIE MA tu w ogóle — ani spotkań, ani dokumentów.
/// Okno jest zakresem rozliczenia, a nie regułą odrzucającą pracę: poza nim leży cała
/// reszta kalendarza i cała reszta dysku. Frontend pobiera z Graph z zapasem (doba
/// ponad okno przy dokumentach, a przy kalendarzu — bo okno backendu liczy się od
/// POCZĄTKU doby lokalnej — ponad dwie doby spotkań), więc licznik meldował przy każdej
/// synchronizacji ten sam, stały zapas jako „pominięte pozycje". Zamiast tego pozycje
/// spoza okna nie liczą się także jako pobrane (patrz SyncFetchedCounts) — nigdy nie
/// były kandydatami, więc nie ma czego pomijać.
/// </summary>
public record SyncFilteredOutCounts(
    int Private,
    int TooShort,
    int AllDay,
    int Cancelled,
    int InvalidDates,
    int NotOfficeDocument,
    int NotModifiedByUser)
{
    public int Total => Private + TooShort + AllDay + Cancelled + InvalidDates
        + NotOfficeDocument + NotModifiedByUser;
}

/// <summary>Wyniki dopasowania nowo utworzonych sugestii do spraw.</summary>
public record SyncMatchedCounts(int Single, int None, int Ambiguous);

/// <summary>
/// Liczniki historii wersji z jednego syncu — pomiar gęstości wersji (etap 0 silnika
/// sesji): ile plików przyszło z historią, ile bez, ile pobrań padło po stronie klienta.
/// Plik z nieudanym pobraniem liczy się i w WithoutHistory, i w FetchErrors.
/// </summary>
public record SyncVersionCounts(int FilesWithHistory, int FilesWithoutHistory, int FetchErrors, int NewActivities);

/// <summary>
/// Pełny raport synchronizacji. Aplikacja "pokazuje swoją pracę" — bez tego
/// odfiltrowanie spotkań wygląda dla użytkownika jak zgubione dane.
/// </summary>
public record SyncReport(
    SyncFetchedCounts Fetched,
    SyncFilteredOutCounts FilteredOut,
    int Aggregated,
    // Duplikaty tego samego klucza (źródło, id, dzień) scalone w obrębie jednego
    // żądania — odpowiednik Aggregated dla powtórzeń z Graph (np. wydarzenie
    // zduplikowane między stronami calendarView). Utrzymuje niezmiennik:
    // pobrano = utworzone + zaktualizowane + pominięte + odfiltrowane
    //         + zagregowane + zdeduplikowane.
    int Deduplicated,
    int Created,
    int Updated,
    int SkippedExisting,
    int Removed,
    SyncMatchedCounts Matched,
    // Faktycznie użyte okno w dniach (konfiguracja albo nadpisanie z żądania) —
    // UI pokazuje je w tekstach raportu zamiast zgadywać z własnej stałej.
    int WindowDays,
    // Liczniki historii wersji — zasilają pomiar gęstości wersji z etapu 0.
    SyncVersionCounts Versions);
