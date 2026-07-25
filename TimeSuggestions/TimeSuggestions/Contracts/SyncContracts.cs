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

public record SyncResult(int Created, int SkippedExisting);
