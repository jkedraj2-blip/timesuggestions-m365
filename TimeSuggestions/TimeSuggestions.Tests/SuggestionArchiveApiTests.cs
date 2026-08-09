using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace TimeSuggestions.Tests;

/// <summary>
/// Pełny cykl archiwum sugestii przez pipeline HTTP: serializacja statusu
/// "archived" (globalny JsonStringEnumConverter z camelCase), filtr
/// ?status=archived przez istniejące bindowanie enuma i terminalność archiwum (409).
/// </summary>
public sealed class SuggestionArchiveApiTests : IDisposable
{
    private readonly string databasePath = Path.Combine(
        Path.GetTempPath(), $"timesuggestions-tests-{Guid.NewGuid():N}.db");

    private readonly WebApplicationFactory<Program> factory;
    private readonly HttpClient client;

    public SuggestionArchiveApiTests()
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
    private async Task<int> CreateSuggestionViaSyncAsync()
    {
        var start = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd'T'10:00:00");
        var end = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd'T'11:00:00");
        var syncJson = $$"""
            {"calendarEvents":[{"id":"event-archive-test","subject":"Spotkanie z Kowalski",
            "startDateTime":"{{start}}","endDateTime":"{{end}}","sensitivity":"normal"}],"driveFiles":[]}
            """;
        var syncResponse = await PostJsonAsync("/api/sync", syncJson);
        Assert.Equal(HttpStatusCode.OK, syncResponse.StatusCode);

        var listJson = await client.GetStringAsync("/api/suggestions");
        using var document = JsonDocument.Parse(listJson);
        return document.RootElement[0].GetProperty("id").GetInt32();
    }

    [Fact]
    public async Task ArchiwumSugestii_PelnyCykl_SerializacjaFiltrITerminalnosc()
    {
        var id = await CreateSuggestionViaSyncAsync();

        Assert.Equal(HttpStatusCode.NoContent,
            (await PostJsonAsync($"/api/suggestions/{id}/reject", "{}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await PostJsonAsync($"/api/suggestions/{id}/archive", "{}")).StatusCode);

        // Status w JSON jako camelCase string, filtr przez istniejące bindowanie enuma.
        var archivedJson = await client.GetStringAsync("/api/suggestions?status=archived");
        using var archived = JsonDocument.Parse(archivedJson);
        var suggestion = Assert.Single(archived.RootElement.EnumerateArray());
        Assert.Equal("archived", suggestion.GetProperty("status").GetString());

        // Zarchiwizowana zniknęła z widoku odrzuconych.
        var rejectedJson = await client.GetStringAsync("/api/suggestions?status=rejected");
        using var rejected = JsonDocument.Parse(rejectedJson);
        Assert.Empty(rejected.RootElement.EnumerateArray());

        // Archiwum jest terminalne — restore odbija się konfliktem.
        var restoreResponse = await PostJsonAsync($"/api/suggestions/{id}/restore", "{}");
        Assert.Equal(HttpStatusCode.Conflict, restoreResponse.StatusCode);
    }

    [Fact]
    public async Task ArchiveRejected_HurtowoArchiwizujeIZwracaLicznik()
    {
        var id = await CreateSuggestionViaSyncAsync();
        Assert.Equal(HttpStatusCode.NoContent,
            (await PostJsonAsync($"/api/suggestions/{id}/reject", "{}")).StatusCode);

        var response = await PostJsonAsync("/api/suggestions/archive-rejected", "{}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"archivedCount\":1", await response.Content.ReadAsStringAsync());

        // Ponowne wywołanie: zero odrzuconych to sukces z zerem.
        var second = await PostJsonAsync("/api/suggestions/archive-rejected", "{}");
        Assert.Contains("\"archivedCount\":0", await second.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ArchiveSuggestion_OczekujacejZwraca409()
    {
        var id = await CreateSuggestionViaSyncAsync();

        var response = await PostJsonAsync($"/api/suggestions/{id}/archive", "{}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("odrzuconą", await response.Content.ReadAsStringAsync());
    }
}
