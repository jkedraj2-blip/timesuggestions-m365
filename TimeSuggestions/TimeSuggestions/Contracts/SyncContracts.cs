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
    /// Opcjonalne nadpisanie domyślnego czasu dokumentu (preferencja użytkownika).
    /// Brak wartości = obowiązuje konfiguracja backendu (appsettings.json).
    /// </summary>
    [Range(1, Configuration.SuggestionOptions.MaxDocumentDurationMinutes,
        ErrorMessage = "Domyślny czas dokumentu musi mieścić się w zakresie 1–480 minut.")]
    public int? DefaultDocumentDurationMinutes { get; set; }

    /// <summary>Elementy listy tombstone'ów walidowane jak pozostałe identyfikatory z Graph.</summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (DeletedDriveFileIds.Any(id => string.IsNullOrEmpty(id) || id.Length > CalendarEventDto.MaxTextLength))
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

    [Required]
    [MaxLength(MaxTextLength, ErrorMessage = "Identyfikator wydarzenia jest za długi.")]
    public required string Id { get; set; }

    [MaxLength(MaxTextLength, ErrorMessage = "Tytuł wydarzenia jest za długi.")]
    public string? Subject { get; set; }

    public DateTime StartDateTime { get; set; }

    public DateTime EndDateTime { get; set; }

    public bool IsAllDay { get; set; }

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
    public int DocumentsOutsideWindow { get; set; }

    [Range(0, MaxCount, ErrorMessage = RangeMessage)]
    public int DocumentsNotOfficeDocument { get; set; }
}

public record SyncFetchedCounts(int CalendarEvents, int DriveFiles);

/// <summary>Liczniki odrzuceń per reguła — UI tłumaczy użytkownikowi, czemu pozycje odpadły.</summary>
public record SyncFilteredOutCounts(
    int Private,
    int TooShort,
    int AllDay,
    int Cancelled,
    int InvalidDates,
    int NotOfficeDocument,
    int OutsideWindow,
    int NotModifiedByUser)
{
    public int Total => Private + TooShort + AllDay + Cancelled + InvalidDates
        + NotOfficeDocument + OutsideWindow + NotModifiedByUser;
}

/// <summary>Wyniki dopasowania nowo utworzonych sugestii do spraw.</summary>
public record SyncMatchedCounts(int Single, int None, int Ambiguous);

/// <summary>
/// Pełny raport synchronizacji. Aplikacja "pokazuje swoją pracę" — bez tego
/// odfiltrowanie spotkań wygląda dla użytkownika jak zgubione dane.
/// </summary>
public record SyncReport(
    SyncFetchedCounts Fetched,
    SyncFilteredOutCounts FilteredOut,
    int Aggregated,
    int Created,
    int Updated,
    int SkippedExisting,
    int Removed,
    SyncMatchedCounts Matched);
