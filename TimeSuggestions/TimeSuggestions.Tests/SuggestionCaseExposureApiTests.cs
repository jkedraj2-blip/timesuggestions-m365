using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace TimeSuggestions.Tests;

/// <summary>
/// Ekspozycja danych sprawy (numer, klient) w DTO przez pipeline HTTP: pola czytane
/// na żywo z nawigacji Case oraz format kandydatów "Nazwa (Numer)" przy sugestii
/// niejednoznacznej. Sprawy pochodzą z seedu bazy (patrz AppDbContext.HasData).
/// </summary>
public sealed class SuggestionCaseExposureApiTests : IDisposable
{
    private readonly string databasePath = Path.Combine(
        Path.GetTempPath(), $"timesuggestions-tests-{Guid.NewGuid():N}.db");

    private readonly WebApplicationFactory<Program> factory;
    private readonly HttpClient client;

    public SuggestionCaseExposureApiTests()
    {
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Default", $"Data Source={databasePath}"));
        client = factory.CreateClient();
    }

    public void Dispose()
    {
        client.Dispose();
        factory.Dispose();
        // Pula połączeń SQLite trzyma uchwyty do pliku — bez wyczyszczenia
        // usuwanie na Windows potrafi się nie powieść i pliki zostają w temp.
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                File.Delete(databasePath + suffix);
            }
            catch (IOException)
            {
                // Ostatnia linia obrony: plik nadal trzymany — zostanie w temp.
            }
        }
    }

    private Task<HttpResponseMessage> PostJsonAsync(string path, string json)
        => client.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"));

    /// <summary>Tworzy sugestię przez sync (wydarzenie z wczoraj — zawsze w oknie 7 dni).</summary>
    private async Task<JsonDocument> SyncAndGetSuggestionsAsync(string subject)
    {
        var start = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd'T'10:00:00");
        var end = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd'T'11:00:00");
        var syncJson = $$"""
            {"calendarEvents":[{"id":"event-case-exposure","subject":"{{subject}}",
            "startDateTime":"{{start}}","endDateTime":"{{end}}","sensitivity":"normal"}],"driveFiles":[]}
            """;
        var syncResponse = await PostJsonAsync("/api/sync", syncJson);
        Assert.Equal(HttpStatusCode.OK, syncResponse.StatusCode);

        return JsonDocument.Parse(await client.GetStringAsync("/api/suggestions"));
    }

    [Fact]
    public async Task GetSuggestions_DopasowanaSprawaNiesieNumerIKlienta()
    {
        using var document = await SyncAndGetSuggestionsAsync("Spotkanie z Kowalski");

        var suggestion = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal("Kowalski sp. z o.o. — obsługa korporacyjna", suggestion.GetProperty("caseName").GetString());
        Assert.Equal("K-2026-001", suggestion.GetProperty("caseNumber").GetString());
        Assert.Equal("Kowalski", suggestion.GetProperty("clientName").GetString());
    }

    [Fact]
    public async Task GetSuggestions_KandydaciNiejednoznacznejMajaNumeryWNawiasach()
    {
        // "Beta" jest wspólnym słowem kluczowym seedowanych spraw #4 i #5.
        using var document = await SyncAndGetSuggestionsAsync("Analiza Beta");

        var suggestion = Assert.Single(document.RootElement.EnumerateArray());
        Assert.True(suggestion.GetProperty("isAmbiguous").GetBoolean());

        var candidates = suggestion.GetProperty("matchCandidates")
            .EnumerateArray()
            .Select(candidate => candidate.GetString())
            .ToList();
        Assert.Contains("Fuzja Alfa/Beta (AB-2026-021)", candidates);
        Assert.Contains("Beta Logistics — audyt umów (BL-2026-030)", candidates);

        // Bez rozstrzygnięcia nie ma sprawy — pola sprawy pozostają puste, bez wyjątku.
        Assert.Equal(JsonValueKind.Null, suggestion.GetProperty("caseNumber").ValueKind);
        Assert.Equal(JsonValueKind.Null, suggestion.GetProperty("clientName").ValueKind);
    }

    [Fact]
    public async Task Approve_ZwrotIListaWpisowNiosaNumerIKlientaSprawy()
    {
        using var document = await SyncAndGetSuggestionsAsync("Spotkanie z Kowalski");
        var id = document.RootElement[0].GetProperty("id").GetInt32();

        // Zwrot z approve mapuje CreatedEntry — nawigacja Case musi być ustawiona (bez null).
        var approveResponse = await PostJsonAsync(
            $"/api/suggestions/{id}/approve",
            """{"caseId":1,"durationMinutes":60,"description":"Spotkanie z klientem"}""");
        Assert.Equal(HttpStatusCode.OK, approveResponse.StatusCode);

        using var created = JsonDocument.Parse(await approveResponse.Content.ReadAsStringAsync());
        Assert.Equal("K-2026-001", created.RootElement.GetProperty("caseNumber").GetString());
        Assert.Equal("Kowalski", created.RootElement.GetProperty("clientName").GetString());

        using var entries = JsonDocument.Parse(await client.GetStringAsync("/api/time-entries"));
        var day = Assert.Single(entries.RootElement.GetProperty("days").EnumerateArray());
        var entry = Assert.Single(day.GetProperty("entries").EnumerateArray());
        Assert.Equal("K-2026-001", entry.GetProperty("caseNumber").GetString());
        Assert.Equal("Kowalski", entry.GetProperty("clientName").GetString());
    }
}
