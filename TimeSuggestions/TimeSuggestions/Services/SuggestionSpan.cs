using TimeSuggestions.Models;

namespace TimeSuggestions.Services;

/// <summary>
/// Zasięg sugestii na osi dnia, czyli teren, który zajmuje ona w niezmienniku
/// "każda minuta doby należy do najwyżej jednego wpisu".
///
/// Koniec zasięgu to PÓŹNIEJSZA z dwóch granic: koniec liczonego czasu
/// (StartedAt + DurationMinutes) i ostatnia znana modyfikacja. Zwykle jest to ten sam
/// moment, bo sesja trwa dokładnie od pierwszego do ostatniego zapisu. Granice
/// rozjeżdżają się po SCALENIU: wynik ma czas będący SUMĄ sesji, ale rozciąga się od
/// pierwszej kotwicy do ostatniej modyfikacji, więc przerwa między sesjami leży w jego
/// zasięgu, choć nie jest rozliczana.
///
/// Liczenie końca z samego czasu gubiło wtedy drugą sesję: wpis powstały ze scalonej
/// sugestii kończył się w godzinie, w której praca jeszcze trwała, w historii wersji
/// podświetlał wyłącznie pierwszą sesję (krótszej po prostu nie było widać), a jej
/// minuty zostawały "wolne" dla innej pozycji, mimo że wpis już je rozliczał.
///
/// Zasięg ≠ czas rozliczany — to samo rozróżnienie, które wpis czasu ma od zawsze:
/// odjęcie przerwy i korekta ±15 min zmieniają DurationMinutes, a godzin wpisu nie
/// ruszają.
/// </summary>
public static class SuggestionSpan
{
    /// <param name="lastActivityAt">
    /// Wartość z encji, zawsze w UTC — po odczycie z SQLite Kind bywa Unspecified,
    /// więc oznaczamy go jawnie przed konwersją (tak samo jak w SuggestionDto).
    /// </param>
    public static DateTime EndOf(
        DateTime startedAt,
        int durationMinutes,
        DateTime lastActivityAt,
        TimeZoneInfo businessTimeZone)
    {
        var durationEnd = startedAt.AddMinutes(durationMinutes);
        var lastActivityLocal = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(lastActivityAt, DateTimeKind.Utc), businessTimeZone);
        return lastActivityLocal > durationEnd ? lastActivityLocal : durationEnd;
    }

    public static DateTime EndOf(Suggestion suggestion, TimeZoneInfo businessTimeZone)
        => EndOf(suggestion.StartedAt, suggestion.DurationMinutes, suggestion.LastActivityAt, businessTimeZone);
}
