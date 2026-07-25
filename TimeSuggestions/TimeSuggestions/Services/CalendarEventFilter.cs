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
    int AllDayCount);

/// <summary>
/// Reguły odsiewania wydarzeń, które nie powinny stać się sugestiami.
/// Czysta funkcja — próg minimalnego czasu przychodzi parametrem (z konfiguracji).
/// </summary>
public static class CalendarEventFilter
{
    // Graph zwraca sensitivity jako: normal / personal / private / confidential.
    private static readonly string[] ExcludedSensitivities = ["private", "confidential"];

    public static CalendarFilterResult FilterBillable(
        IEnumerable<CalendarEventDto> events,
        int minimumDurationMinutes)
    {
        var accepted = new List<CalendarEventDto>();
        var privateCount = 0;
        var tooShortCount = 0;
        var allDayCount = 0;

        foreach (var calendarEvent in events)
        {
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

            // Wydarzenie trwające dokładnie tyle, ile próg, przechodzi (warunek "krótsze niż").
            if (GetDurationMinutes(calendarEvent) < minimumDurationMinutes)
            {
                tooShortCount++;
                continue;
            }

            accepted.Add(calendarEvent);
        }

        return new CalendarFilterResult(accepted, privateCount, tooShortCount, allDayCount);
    }

    public static int GetDurationMinutes(CalendarEventDto calendarEvent)
        => (int)Math.Round((calendarEvent.EndDateTime - calendarEvent.StartDateTime).TotalMinutes);

    private static bool IsExcludedSensitivity(string? sensitivity)
        => sensitivity is not null
           && ExcludedSensitivities.Contains(sensitivity, StringComparer.OrdinalIgnoreCase);
}
