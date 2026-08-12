namespace TimeSuggestions.Models;

/// <summary>
/// Pojedynczy fakt „plik był w tym momencie zapisywany" — append-only dziennik:
/// rekordy nigdy nie są modyfikowane ani kasowane przez operacje użytkownika
/// (scalanie wpisów, korekty). To źródło prawdy o historii modyfikacji, na którym
/// silnik sesji liczy realny czas pracy.
///
/// Fakt jest identyfikowany przez (plik, wersja, MOMENT), nie przez samą wersję.
/// Powód jest sednem działania Worda online: nowa wersja powstaje dopiero przy
/// zamknięciu karty albo po kilkunastu minutach bezczynności, a przez cały czas
/// ciągłej pracy autozapis dopisuje do TEJ SAMEJ wersji, przesuwając wyłącznie jej
/// lastModifiedDateTime. Przy kluczu (plik, wersja) każdy kolejny odczyt tej samej
/// wersji był odrzucany jako duplikat i godziny nieprzerwanej pracy zostawiały po
/// sobie jeden punkt. Z momentem w kluczu każdy odczyt jest osobną próbką i sesja
/// rośnie razem z pracą.
/// </summary>
public class DocumentActivity
{
    /// <summary>
    /// Etykieta, którą część dysków OneDrive/SharePoint opisuje NAJNOWSZĄ wersję
    /// zamiast numerem. To oznaczenie POZYCJI, nie tożsamość wersji — przy kolejnym
    /// zapisie przeskakuje dalej, więc jako fakt ma sens wyłącznie razem z momentem.
    /// </summary>
    public const string CurrentVersionLabel = "current";

    /// <summary>
    /// Pseudo-wersja dla próbki wziętej z samego pliku (driveItem.lastModifiedDateTime),
    /// a nie z listy wersji. To jedyny sygnał, jaki Graph daje o pracy trwającej
    /// WEWNĄTRZ niezapieczętowanej jeszcze wersji — bez niego ciągła edycja jest
    /// dla nas niewidoczna aż do zamknięcia dokumentu.
    /// </summary>
    public const string ItemObservationLabel = "item";

    public int Id { get; set; }

    /// <summary>Identyfikator pliku z Microsoft Graph (driveItem.id).</summary>
    public required string ExternalId { get; set; }

    /// <summary>
    /// Identyfikator wersji z Graph albo <see cref="ItemObservationLabel"/> dla próbki
    /// z samego pliku — razem z ExternalId i OccurredAt tworzy klucz naturalny faktu.
    /// </summary>
    public required string VersionId { get; set; }

    /// <summary>Moment zapisu wersji (UTC) — na tej osi silnik sesji tnie pracę na sesje.</summary>
    public DateTime OccurredAt { get; set; }

    /// <summary>Rozmiar pliku w tej wersji (bajty) — pomocniczy sygnał skali zmiany.</summary>
    public long Size { get; set; }

    /// <summary>Kiedy fakt trafił do dziennika (UTC) — odróżnia moment zdarzenia od momentu rejestracji.</summary>
    public DateTime RecordedAt { get; set; }
}
