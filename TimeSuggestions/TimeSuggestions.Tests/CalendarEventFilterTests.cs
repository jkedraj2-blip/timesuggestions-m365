using TimeSuggestions.Contracts;
using TimeSuggestions.Services;

namespace TimeSuggestions.Tests;

/// <summary>
/// Reguły filtrowania na fixture z przykładowymi odpowiedziami Graph
/// (zapisane dane zamiast sieci — zgodnie z wymaganiami testów).
/// Okno czasu podawane w strefie biznesowej — fixture ma czasy lokalne z lipca 2026.
/// </summary>
public class CalendarEventFilterTests
{
    private const int MinimumDurationMinutes = 5;

    private static readonly DateTime WindowStart = new(2026, 7, 18, 0, 0, 0);
    private static readonly DateTime WindowEnd = new(2026, 7, 25, 12, 0, 0);

    private static CalendarFilterResult Filter(IEnumerable<CalendarEventDto> events)
        => CalendarEventFilter.FilterBillable(events, MinimumDurationMinutes, WindowStart, WindowEnd);

    private static CalendarEventDto CreateEvent(
        string id,
        DateTime start,
        DateTime end,
        string? sensitivity = "normal",
        bool isCancelled = false) => new()
    {
        Id = id,
        Subject = "Spotkanie testowe",
        StartDateTime = start,
        EndDateTime = end,
        Sensitivity = sensitivity,
        IsCancelled = isCancelled,
    };

    [Fact]
    public void FilterBillable_OdrzucaWydarzeniePrywatne()
    {
        var events = TestHelpers.LoadCalendarEventsFixture();

        var billable = Filter(events).Accepted;

        Assert.DoesNotContain(billable, calendarEvent => calendarEvent.Id == "event-private");
    }

    [Fact]
    public void FilterBillable_OdrzucaWydarzeniePoufne()
    {
        var events = TestHelpers.LoadCalendarEventsFixture();

        var billable = Filter(events).Accepted;

        Assert.DoesNotContain(billable, calendarEvent => calendarEvent.Id == "event-confidential");
    }

    [Fact]
    public void FilterBillable_OdrzucaWydarzenieOsobiste()
    {
        // "personal" to również czas nierozliczalny — jak private i confidential.
        var events = TestHelpers.LoadCalendarEventsFixture();

        var billable = Filter(events).Accepted;

        Assert.DoesNotContain(billable, calendarEvent => calendarEvent.Id == "event-personal");
    }

    [Fact]
    public void FilterBillable_OdrzucaWydarzenieAnulowane()
    {
        var events = TestHelpers.LoadCalendarEventsFixture();

        var result = Filter(events);

        Assert.DoesNotContain(result.Accepted, calendarEvent => calendarEvent.Id == "event-cancelled");
        Assert.Equal(1, result.CancelledCount);
    }

    [Fact]
    public void FilterBillable_OdrzucaWydarzenieKrotszeNizProg()
    {
        var events = TestHelpers.LoadCalendarEventsFixture();

        var billable = Filter(events).Accepted;

        Assert.DoesNotContain(billable, calendarEvent => calendarEvent.Id == "event-too-short");
    }

    [Fact]
    public void FilterBillable_PrzepuszczaWydarzenieTrwajaceDokladnieProg()
    {
        // Przypadek graniczny: reguła brzmi "krótsze niż 5 minut", więc równe 5 minut przechodzi.
        var events = TestHelpers.LoadCalendarEventsFixture();

        var billable = Filter(events).Accepted;

        Assert.Contains(billable, calendarEvent => calendarEvent.Id == "event-exactly-5-min");
    }

    [Fact]
    public void FilterBillable_OdrzucaWydarzenieCalodniowe()
    {
        var events = TestHelpers.LoadCalendarEventsFixture();

        var billable = Filter(events).Accepted;

        Assert.DoesNotContain(billable, calendarEvent => calendarEvent.Id == "event-all-day");
    }

    [Fact]
    public void FilterBillable_PrzepuszczaZwykleSpotkanie()
    {
        var events = TestHelpers.LoadCalendarEventsFixture();

        var billable = Filter(events).Accepted;

        Assert.Contains(billable, calendarEvent => calendarEvent.Id == "event-normal");
    }

    [Fact]
    public void FilterBillable_OdrzucaWydarzenieZPrzyszlosci()
    {
        // Wydarzenie po końcu okna (przyszłość) nie jest czasem przepracowanym.
        var futureEvent = CreateEvent(
            "event-future",
            WindowEnd.AddDays(1),
            WindowEnd.AddDays(1).AddHours(1));

        var result = Filter([futureEvent]);

        Assert.Empty(result.Accepted);
        Assert.Equal(1, result.OutsideWindowCount);
    }

    [Fact]
    public void FilterBillable_OdrzucaWydarzeniePrzedOknem()
    {
        var oldEvent = CreateEvent(
            "event-old",
            WindowStart.AddDays(-2),
            WindowStart.AddDays(-2).AddHours(1));

        var result = Filter([oldEvent]);

        Assert.Empty(result.Accepted);
        Assert.Equal(1, result.OutsideWindowCount);
    }

    [Fact]
    public void FilterBillable_OdrzucaWydarzenieZKoncemPrzedPoczatkiem()
    {
        var invalidEvent = CreateEvent(
            "event-invalid",
            new DateTime(2026, 7, 20, 12, 0, 0),
            new DateTime(2026, 7, 20, 11, 0, 0));

        var result = Filter([invalidEvent]);

        Assert.Empty(result.Accepted);
        Assert.Equal(1, result.InvalidDatesCount);
    }

    [Fact]
    public void FilterBillable_LiczyOdrzuceniaPerRegula()
    {
        // Fixture zawiera: normalne, prywatne, poufne, osobiste, 4-minutowe,
        // dokładnie 5-minutowe, całodniowe, anulowane.
        var events = TestHelpers.LoadCalendarEventsFixture();

        var result = Filter(events);

        Assert.Equal(3, result.PrivateCount); // prywatne + poufne + osobiste
        Assert.Equal(1, result.TooShortCount);
        Assert.Equal(1, result.AllDayCount);
        Assert.Equal(1, result.CancelledCount);
        Assert.Equal(0, result.OutsideWindowCount);
        Assert.Equal(0, result.InvalidDatesCount);
        Assert.Equal(2, result.Accepted.Count);
    }

    [Fact]
    public void GetDurationMinutes_WyliczaCzasZPoczatkuIKonca()
    {
        var events = TestHelpers.LoadCalendarEventsFixture();
        var normalEvent = events.Single(calendarEvent => calendarEvent.Id == "event-normal");

        Assert.Equal(90, CalendarEventFilter.GetDurationMinutes(normalEvent));
    }
}
