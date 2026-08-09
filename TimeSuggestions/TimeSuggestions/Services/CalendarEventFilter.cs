using TimeSuggestions.Contracts;

namespace TimeSuggestions.Services;

/// <summary>
/// Wynik filtrowania wraz z licznikami odrzuceń — UI pokazuje użytkownikowi,
/// dlaczego część spotkań nie stała się sugestiami (inaczej filtr wygląda jak awaria).
/// </summary>
public record CalendarFilterResult(
    List<CalendarEventDto> Accepted,
    int PrivateCount,
    int TooShortCount,
    int AllDayCount,
    int CancelledCount,
    int OutsideWindowCount,
    int InvalidDatesCount);

/// <summary>
/// Reguły odsiewania wydarzeń, które nie powinny stać się sugestiami.
/// Czysta funkcja — próg minimalnego czasu i okno czasu przychodzą parametrami.
/// Okno jest liczone w strefie biznesowej i porównywane bezpośrednio z lokalnymi
/// czasami wydarzeń (Prefer: outlook.timezone) — bez tolerancji, która maskowałaby błędne dane.
/// </summary>
public static class CalendarEventFilter
{
    // Graph zwraca sensitivity jako: normal / personal / private / confidential.
    // "personal" też wykluczamy — to nie jest czas rozliczalny.
    private static readonly string[] ExcludedSensitivities = ["personal", "private", "confidential"];

    public static CalendarFilterResult FilterBillable(
        IEnumerable<CalendarEventDto> events,
        int minimumDurationMinutes,
        DateTime windowStart,
        DateTime windowEnd,
        TimeZoneInfo businessTimeZone)
    {
        var accepted = new List<CalendarEventDto>();
        var privateCount = 0;
        var tooShortCount = 0;
        var allDayCount = 0;
        // Anulowane liczone osobno (nie do prywatnych) — to inna przyczyna odrzucenia
        // i raport ma mówić prawdę o każdej z nich.
        var cancelledCount = 0;
        var outsideWindowCount = 0;
        var invalidDatesCount = 0;

        foreach (var calendarEvent in events)
        {
            // Defensywnie: null w kolekcji odrzuca już walidacja kontraktu (400),
            // ale czysta logika nie może paść z NRE, gdy dostanie go inną drogą.
            if (calendarEvent is null)
            {
                continue;
            }

            if (calendarEvent.IsCancelled)
            {
                cancelledCount++;
                continue;
            }

            if (calendarEvent.IsAllDay)
            {
                allDayCount++;
                continue;
            }

            if (IsExcludedSensitivity(calendarEvent.Sensitivity))
            {
                privateCount++;
                continue;
            }

            if (calendarEvent.EndDateTime < calendarEvent.StartDateTime)
            {
                invalidDatesCount++;
                continue;
            }

            // Porównanie z oknem po sprowadzeniu do strefy biznesowej — JSON z "Z",
            // z offsetem i bez offsetu musi trafiać do tego samego okna.
            var startLocal = BusinessTime.ToBusinessLocal(calendarEvent.StartDateTime, businessTimeZone);
            if (startLocal < windowStart || startLocal > windowEnd)
            {
                outsideWindowCount++;
                continue;
            }

            // Wydarzenie trwające dokładnie tyle, ile próg, przechodzi (warunek "krótsze niż").
            if (GetDurationMinutes(calendarEvent, businessTimeZone) < minimumDurationMinutes)
            {
                tooShortCount++;
                continue;
            }

            accepted.Add(calendarEvent);
        }

        return new CalendarFilterResult(
            accepted, privateCount, tooShortCount, allDayCount,
            cancelledCount, outsideWindowCount, invalidDatesCount);
    }

    /// <summary>
    /// Czas trwania z RÓŻNICY INSTANTÓW UTC, nie lokalnych DateTime — w noc zmiany
    /// czasu różnica lokalna kłamie o godzinę (01:30–03:30 wiosną to 60 minut,
    /// jesienią 180). Konwencje czasów niejednoznacznych/nieistniejących: BusinessTime.
    /// </summary>
    public static int GetDurationMinutes(CalendarEventDto calendarEvent, TimeZoneInfo businessTimeZone)
        => (int)Math.Round(
            (BusinessTime.ToUtcInstant(calendarEvent.EndDateTime, businessTimeZone)
             - BusinessTime.ToUtcInstant(calendarEvent.StartDateTime, businessTimeZone)).TotalMinutes);

    private static bool IsExcludedSensitivity(string? sensitivity)
        => sensitivity is not null
           && ExcludedSensitivities.Contains(sensitivity, StringComparer.OrdinalIgnoreCase);
}
