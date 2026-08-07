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

    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");

    private static readonly DateTime WindowStart = new(2026, 7, 18, 0, 0, 0);
    private static readonly DateTime WindowEnd = new(2026, 7, 25, 12, 0, 0);

    private static CalendarFilterResult Filter(IEnumerable<CalendarEventDto> events)
        => CalendarEventFilter.FilterBillable(events, MinimumDurationMinutes, WindowStart, WindowEnd, Warsaw);

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

        Assert.Equal(90, CalendarEventFilter.GetDurationMinutes(normalEvent, Warsaw));
    }

    // --- Zmiana czasu: trwanie liczone z instantów UTC, nie z różnicy lokalnych DateTime ---

    [Fact]
    public void GetDurationMinutes_WiosennaZmianaCzasu_LiczyRealneMinuty()
    {
        // Noc 29.03.2026 w Warszawie: 02:00 → 03:00. Zegar ścienny pokazuje
        // 01:30–03:30 (różnica lokalna 120), ale realnie minęło 60 minut.
        var meeting = CreateEvent(
            "event-spring",
            new DateTime(2026, 3, 29, 1, 30, 0),
            new DateTime(2026, 3, 29, 3, 30, 0));

        Assert.Equal(60, CalendarEventFilter.GetDurationMinutes(meeting, Warsaw));
    }

    [Fact]
    public void GetDurationMinutes_JesiennaZmianaCzasu_LiczyRealneMinuty()
    {
        // Noc 25.10.2026: 03:00 → 02:00. 01:30–03:30 lokalnie to realnie 180 minut.
        var meeting = CreateEvent(
            "event-fall",
            new DateTime(2026, 10, 25, 1, 30, 0),
            new DateTime(2026, 10, 25, 3, 30, 0));

        Assert.Equal(180, CalendarEventFilter.GetDurationMinutes(meeting, Warsaw));
    }

    [Fact]
    public void GetDurationMinutes_CzasNiejednoznaczny_PierwszeWystapienieBezWyjatku()
    {
        // 02:30 w noc jesiennej zmiany występuje dwa razy — konwencja BusinessTime:
        // pierwsze wystąpienie (czas letni, UTC+2), więc 02:30–03:30 to 120 minut.
        var meeting = CreateEvent(
            "event-ambiguous",
            new DateTime(2026, 10, 25, 2, 30, 0),
            new DateTime(2026, 10, 25, 3, 30, 0));

        Assert.Equal(120, CalendarEventFilter.GetDurationMinutes(meeting, Warsaw));
    }

    [Fact]
    public void GetDurationMinutes_CzasNieistniejacy_OffsetPoZmianieBezWyjatku()
    {
        // 02:30 w noc wiosennej zmiany nie istnieje — konwencja BusinessTime:
        // offset po zmianie (jakby zegar już przeskoczył), więc 02:30–03:30 to 60 minut.
        var meeting = CreateEvent(
            "event-invalid-time",
            new DateTime(2026, 3, 29, 2, 30, 0),
            new DateTime(2026, 3, 29, 3, 30, 0));

        Assert.Equal(60, CalendarEventFilter.GetDurationMinutes(meeting, Warsaw));
    }

    [Fact]
    public void GetDurationMinutes_JsonZSufiksemZOffsetemIBezSufiksuLiczySieTakSamo()
    {
        // Ten sam odcinek 10:00–11:30 czasu warszawskiego (lipiec = UTC+2) zapisany
        // na trzy sposoby, jakie może przyjąć deserializacja JSON.
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        string[] payloads =
        [
            """{"id":"event-1","startDateTime":"2026-07-24T10:00:00","endDateTime":"2026-07-24T11:30:00"}""",
            """{"id":"event-1","startDateTime":"2026-07-24T08:00:00Z","endDateTime":"2026-07-24T09:30:00Z"}""",
            """{"id":"event-1","startDateTime":"2026-07-24T11:00:00+03:00","endDateTime":"2026-07-24T12:30:00+03:00"}""",
        ];

        foreach (var payload in payloads)
        {
            var dto = System.Text.Json.JsonSerializer.Deserialize<CalendarEventDto>(payload, options);

            Assert.NotNull(dto);
            Assert.Equal(90, CalendarEventFilter.GetDurationMinutes(dto, Warsaw));
        }
    }

    [Fact]
    public void FilterBillable_GranicaOknaWTygodniuZmianyCzasu()
    {
        // Okno jak w backendzie: początek dnia lokalnego 6 dni przed "teraz"
        // (27.10.2026, już po jesiennej zmianie czasu). Wydarzenie dokładnie na
        // granicy wpada, minutę przed — wypada. Frontend pobiera z dobą zapasu,
        // więc oba wydarzenia w ogóle do tego filtru docierają.
        var windowStart = new DateTime(2026, 10, 21, 0, 0, 0);
        var windowEnd = new DateTime(2026, 10, 27, 14, 0, 0);
        var atBoundary = CreateEvent("event-boundary", windowStart, windowStart.AddHours(1));
        var beforeBoundary = CreateEvent(
            "event-before", windowStart.AddMinutes(-1), windowStart.AddMinutes(59));

        var result = CalendarEventFilter.FilterBillable(
            [atBoundary, beforeBoundary], MinimumDurationMinutes, windowStart, windowEnd, Warsaw);

        var accepted = Assert.Single(result.Accepted);
        Assert.Equal("event-boundary", accepted.Id);
        Assert.Equal(1, result.OutsideWindowCount);
    }
}
