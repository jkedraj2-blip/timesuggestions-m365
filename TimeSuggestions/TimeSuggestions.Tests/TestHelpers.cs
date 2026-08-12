using System.Text.Json;
using Microsoft.Extensions.Options;
using TimeSuggestions.Configuration;
using TimeSuggestions.Contracts;
using TimeSuggestions.Data;
using TimeSuggestions.Models;
using TimeSuggestions.Services;

namespace TimeSuggestions.Tests;

/// <summary>Wspólne dane i wczytywanie fixtures — testy nie wymagają sieci ani logowania.</summary>
public static class TestHelpers
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Progi i strefa biznesowa w wartościach domyślnych — tych samych co appsettings.</summary>
    public static IOptions<SuggestionOptions> DefaultOptions() => Options.Create(new SuggestionOptions());

    /// <summary>Strefa biznesowa do porównań czasu w asercjach.</summary>
    public static TimeZoneInfo BusinessTimeZone()
        => TimeZoneInfo.FindSystemTimeZoneById(new SuggestionOptions().BusinessTimeZoneId);

    /// <summary>
    /// Oś czasu z zależnościami złożonymi tak jak w Program.cs — historia dokumentu
    /// podaje dziś także stan pozycji, więc potrzebuje przerw i numeracji sesji.
    /// </summary>
    public static TimelineService Timeline(AppDbContext db)
        => new(db, DefaultOptions(), new EntryGapService(db, DefaultOptions()), new SessionLabelService(db));

    public static List<CalendarEventDto> LoadCalendarEventsFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "calendar-events.json");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<List<CalendarEventDto>>(json, JsonOptions)
               ?? throw new InvalidOperationException($"Nie udało się wczytać fixture: {path}");
    }

    /// <summary>Sprawy testowe niezależne od seedu bazy — testy logiki nie zależą od migracji.</summary>
    public static List<Case> CreateTestCases() =>
    [
        new Case { Id = 1, Name = "Kowalski sp. z o.o.", CaseNumber = "K-2026-001", ClientName = "Kowalski" },
        new Case { Id = 2, Name = "NovaTech S.A.", CaseNumber = "NT-2026-113", ClientName = "NovaTech" },
        new Case { Id = 3, Name = "Spór Grzegrzółka vs Żółć", CaseNumber = "GZ-2026-007", ClientName = "Grzegrzółka" },
        new Case { Id = 4, Name = "Fuzja Alfa/Beta", CaseNumber = "AB-2026-021", ClientName = "Alfa Holding", Keywords = "Alfa;Beta" },
        new Case { Id = 5, Name = "Beta Logistics — audyt", CaseNumber = "BL-2026-030", ClientName = "Beta Logistics", Keywords = "Beta" },
    ];
}
