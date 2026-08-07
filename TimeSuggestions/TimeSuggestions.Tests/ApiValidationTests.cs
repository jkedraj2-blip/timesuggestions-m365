using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;

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
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                File.Delete(databasePath + suffix);
            }
            catch (IOException)
            {
                // Plik bywa jeszcze trzymany przez pulę połączeń SQLite — zostanie w temp.
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
}
