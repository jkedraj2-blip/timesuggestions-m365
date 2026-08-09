using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using TimeSuggestions.Contracts;

namespace TimeSuggestions.Tests;

/// <summary>
/// Testy integracyjne walidacji na granicy API — pełny pipeline HTTP (bindowanie,
/// walidacja modelu, ProblemDetails). Uszkodzony payload ma dawać czytelny 400,
/// nigdy 500. Baza to tymczasowy plik SQLite kasowany po testach.
/// </summary>
public sealed class ApiValidationTests : IDisposable
{
    private readonly string databasePath = Path.Combine(
        Path.GetTempPath(), $"timesuggestions-tests-{Guid.NewGuid():N}.db");

    private readonly WebApplicationFactory<Program> factory;
    private readonly HttpClient client;

    public ApiValidationTests()
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

    private static string CaseJson(string keywordsJson) =>
        $$"""{"name":"Sprawa","caseNumber":"X-2026-001","clientName":"Klient","keywords":{{keywordsJson}}}""";

    [Theory]
    [InlineData("""{"calendarEvents":[null],"driveFiles":[]}""")]
    [InlineData("""{"calendarEvents":[],"driveFiles":[null]}""")]
    [InlineData("""{"calendarEvents":[],"driveFiles":[],"deletedDriveFileIds":[null]}""")]
    public async Task Sync_NullWewnatrzKolekcji_Daje400Zamiast500(string json)
    {
        var response = await PostJsonAsync("/api/sync", json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("pusty", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cases_NullWewnatrzKeywords_Daje400Zamiast500()
    {
        var response = await PostJsonAsync("/api/cases", CaseJson("[null]"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("pusty element", body);
    }

    [Fact]
    public async Task Sync_PoprawnyPustyPayload_Przechodzi()
    {
        // Kontrola: walidacja nie odrzuca prawidłowych żądań.
        var response = await PostJsonAsync("/api/sync", """{"calendarEvents":[],"driveFiles":[]}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // --- Limity długości pól tekstowych i brak odbijania niezaufanych wartości ---

    private static string SyncJsonWithSensitivity(string sensitivity) =>
        $$"""
        {"calendarEvents":[{"id":"event-1","subject":"Spotkanie","startDateTime":"2026-08-06T10:00:00",
        "endDateTime":"2026-08-06T11:00:00","sensitivity":"{{sensitivity}}"}],"driveFiles":[]}
        """;

    [Fact]
    public async Task Sync_Sensitivity10000Znakow_Daje400()
    {
        var response = await PostJsonAsync("/api/sync", SyncJsonWithSensitivity(new string('x', 10_000)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Sync_SensitivityNaGranicyLimitu_Przechodzi()
    {
        var response = await PostJsonAsync(
            "/api/sync", SyncJsonWithSensitivity(new string('x', CalendarEventDto.MaxSensitivityLength)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Cases_Keyword5000Znakow_Daje400BezOdbijaniaWejscia()
    {
        // Średnik w środku trafia w ścieżkę komunikatu z podglądem wartości —
        // odpowiedź nie może odbić pełnego wejścia.
        var keyword = new string('x', 4_999) + ";";
        var response = await PostJsonAsync("/api/cases", CaseJson($"[\"{keyword}\"]"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(body.Length < 1000, $"Odpowiedź walidacji jest za długa ({body.Length} znaków).");
        Assert.DoesNotContain(keyword, body);
    }

    [Fact]
    public async Task Cases_KeywordNaGranicyLimitu_Przechodzi()
    {
        var keyword = new string('k', CaseWriteRequest.MaxKeywordLength);
        var response = await PostJsonAsync("/api/cases", CaseJson($"[\"{keyword}\"]"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Sync_TombstoneIdZSamychSpacji_Daje400()
    {
        var response = await PostJsonAsync(
            "/api/sync", """{"calendarEvents":[],"driveFiles":[],"deletedDriveFileIds":["   "]}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
