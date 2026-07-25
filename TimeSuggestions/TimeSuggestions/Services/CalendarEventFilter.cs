using TimeSuggestions.Contracts;

namespace TimeSuggestions.Services;

/// <summary>
/// Reguły odsiewania wydarzeń, które nie powinny stać się sugestiami.
/// Czysta funkcja — próg minimalnego czasu przychodzi parametrem (z konfiguracji).
/// </summary>
public static class CalendarEventFilter
{
    // Graph zwraca sensitivity jako: normal / personal / private / confidential.
    private static readonly string[] ExcludedSensitivities = ["private", "confidential"];

    public static List<CalendarEventDto> FilterBillable(
        IEnumerable<CalendarEventDto> events,
        int minimumDurationMinutes)
        => events.Where(calendarEvent => IsBillable(calendarEvent, minimumDurationMinutes)).ToList();

    public static int GetDurationMinutes(CalendarEventDto calendarEvent)
        => (int)Math.Round((calendarEvent.EndDateTime - calendarEvent.StartDateTime).TotalMinutes);

    private static bool IsBillable(CalendarEventDto calendarEvent, int minimumDurationMinutes)
    {
        if (calendarEvent.IsAllDay)
        {
            return false;
        }

        if (IsExcludedSensitivity(calendarEvent.Sensitivity))
        {
            return false;
        }

        // Wydarzenie trwające dokładnie tyle, ile próg, przechodzi (warunek "krótsze niż").
        return GetDurationMinutes(calendarEvent) >= minimumDurationMinutes;
    }

    private static bool IsExcludedSensitivity(string? sensitivity)
        => sensitivity is not null
           && ExcludedSensitivities.Contains(sensitivity, StringComparer.OrdinalIgnoreCase);
}
