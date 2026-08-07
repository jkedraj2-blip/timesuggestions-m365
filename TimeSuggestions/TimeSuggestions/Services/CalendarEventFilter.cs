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
        DateTime windowEnd)
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

            if (calendarEvent.StartDateTime < windowStart || calendarEvent.StartDateTime > windowEnd)
            {
                outsideWindowCount++;
                continue;
            }

            // Wydarzenie trwające dokładnie tyle, ile próg, przechodzi (warunek "krótsze niż").
            if (GetDurationMinutes(calendarEvent) < minimumDurationMinutes)
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

    public static int GetDurationMinutes(CalendarEventDto calendarEvent)
        => (int)Math.Round((calendarEvent.EndDateTime - calendarEvent.StartDateTime).TotalMinutes);

    private static bool IsExcludedSensitivity(string? sensitivity)
        => sensitivity is not null
           && ExcludedSensitivities.Contains(sensitivity, StringComparer.OrdinalIgnoreCase);
}
