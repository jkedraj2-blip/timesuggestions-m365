using TimeSuggestions.Services;

namespace TimeSuggestions.Tests;

/// <summary>
/// Reguły filtrowania na fixture z przykładowymi odpowiedziami Graph
/// (zapisane dane zamiast sieci — zgodnie z wymaganiami testów).
/// </summary>
public class CalendarEventFilterTests
{
    private const int MinimumDurationMinutes = 5;

    [Fact]
    public void FilterBillable_OdrzucaWydarzeniePrywatne()
    {
        var events = TestHelpers.LoadCalendarEventsFixture();

        var billable = CalendarEventFilter.FilterBillable(events, MinimumDurationMinutes);

        Assert.DoesNotContain(billable, calendarEvent => calendarEvent.Id == "event-private");
    }

    [Fact]
    public void FilterBillable_OdrzucaWydarzeniePoufne()
    {
        var events = TestHelpers.LoadCalendarEventsFixture();

        var billable = CalendarEventFilter.FilterBillable(events, MinimumDurationMinutes);

        Assert.DoesNotContain(billable, calendarEvent => calendarEvent.Id == "event-confidential");
    }

    [Fact]
    public void FilterBillable_OdrzucaWydarzenieKrotszeNizProg()
    {
        var events = TestHelpers.LoadCalendarEventsFixture();

        var billable = CalendarEventFilter.FilterBillable(events, MinimumDurationMinutes);

        Assert.DoesNotContain(billable, calendarEvent => calendarEvent.Id == "event-too-short");
    }

    [Fact]
    public void FilterBillable_PrzepuszczaWydarzenieTrwajaceDokladnieProg()
    {
        // Przypadek graniczny: reguła brzmi "krótsze niż 5 minut", więc równe 5 minut przechodzi.
        var events = TestHelpers.LoadCalendarEventsFixture();

        var billable = CalendarEventFilter.FilterBillable(events, MinimumDurationMinutes);

        Assert.Contains(billable, calendarEvent => calendarEvent.Id == "event-exactly-5-min");
    }

    [Fact]
    public void FilterBillable_OdrzucaWydarzenieCalodniowe()
    {
        var events = TestHelpers.LoadCalendarEventsFixture();

        var billable = CalendarEventFilter.FilterBillable(events, MinimumDurationMinutes);

        Assert.DoesNotContain(billable, calendarEvent => calendarEvent.Id == "event-all-day");
    }

    [Fact]
    public void FilterBillable_PrzepuszczaZwykleSpotkanie()
    {
        var events = TestHelpers.LoadCalendarEventsFixture();

        var billable = CalendarEventFilter.FilterBillable(events, MinimumDurationMinutes);

        Assert.Contains(billable, calendarEvent => calendarEvent.Id == "event-normal");
    }

    [Fact]
    public void GetDurationMinutes_WyliczaCzasZPoczatkuIKonca()
    {
        var events = TestHelpers.LoadCalendarEventsFixture();
        var normalEvent = events.Single(calendarEvent => calendarEvent.Id == "event-normal");

        Assert.Equal(90, CalendarEventFilter.GetDurationMinutes(normalEvent));
    }
}
