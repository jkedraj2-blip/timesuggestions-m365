namespace TimeSuggestions.Models;

/// <summary>
/// Pojedynczy fakt z historii wersji pliku OneDrive (GET /me/drive/items/{id}/versions) —
/// append-only dziennik: rekordy nigdy nie są modyfikowane ani kasowane przez operacje
/// użytkownika (scalanie wpisów, korekty). To źródło prawdy o historii modyfikacji,
/// na którym silnik sesji liczy realny czas pracy zamiast sztywnych 30 minut.
/// </summary>
public class DocumentActivity
{
    public int Id { get; set; }

    /// <summary>Identyfikator pliku z Microsoft Graph (driveItem.id).</summary>
    public required string ExternalId { get; set; }

    /// <summary>Identyfikator wersji z Graph — razem z ExternalId tworzy klucz naturalny faktu.</summary>
    public required string VersionId { get; set; }

    /// <summary>Moment zapisu wersji (UTC) — na tej osi silnik sesji tnie pracę na sesje.</summary>
    public DateTime OccurredAt { get; set; }

    /// <summary>Rozmiar pliku w tej wersji (bajty) — pomocniczy sygnał skali zmiany.</summary>
    public long Size { get; set; }

    /// <summary>Kiedy fakt trafił do dziennika (UTC) — odróżnia moment zdarzenia od momentu rejestracji.</summary>
    public DateTime RecordedAt { get; set; }
}
