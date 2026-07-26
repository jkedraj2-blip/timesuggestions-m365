using System.ComponentModel.DataAnnotations;

namespace TimeSuggestions.Contracts;

/// <summary>
/// Surowe dane z Microsoft Graph przysyłane przez frontend.
/// Backend celowo nie woła Graph sam — token użytkownika nigdy nie opuszcza przeglądarki.
/// </summary>
public class SyncRequest
{
    public List<CalendarEventDto> CalendarEvents { get; set; } = [];

    public List<DriveFileDto> DriveFiles { get; set; } = [];

    /// <summary>
    /// Opcjonalne nadpisanie domyślnego czasu dokumentu (preferencja użytkownika).
    /// Brak wartości = obowiązuje konfiguracja backendu (appsettings.json).
    /// </summary>
    [Range(1, Configuration.SuggestionOptions.MaxDocumentDurationMinutes,
        ErrorMessage = "Domyślny czas dokumentu musi mieścić się w zakresie 1–480 minut.")]
    public int? DefaultDocumentDurationMinutes { get; set; }
}

/// <summary>Wydarzenie z kalendarza Outlook (podzbiór pól Graph potrzebny logice).</summary>
public class CalendarEventDto
{
    [Required]
    public required string Id { get; set; }

    public string? Subject { get; set; }

    public DateTime StartDateTime { get; set; }

    public DateTime EndDateTime { get; set; }

    public bool IsAllDay { get; set; }

    public string? Sensitivity { get; set; }
}

/// <summary>Plik z OneDrive (podzbiór pól Graph potrzebny logice).</summary>
public class DriveFileDto
{
    [Required]
    public required string Id { get; set; }

    [Required]
    public required string Name { get; set; }

    public DateTime LastModifiedDateTime { get; set; }

    /// <summary>Czy modyfikacji dokonał zalogowany użytkownik — frontend ustala to porównując konto MSAL z lastModifiedBy.</summary>
    public bool LastModifiedByMe { get; set; }
}

public record SyncFetchedCounts(int CalendarEvents, int DriveFiles);

/// <summary>Liczniki odrzuceń per reguła — UI tłumaczy użytkownikowi, czemu pozycje odpadły.</summary>
public record SyncFilteredOutCounts(
    int Private,
    int TooShort,
    int AllDay,
    int NotOfficeDocument,
    int OutsideWindow,
    int NotModifiedByUser)
{
    public int Total => Private + TooShort + AllDay + NotOfficeDocument + OutsideWindow + NotModifiedByUser;
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
    SyncMatchedCounts Matched);
