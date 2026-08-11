using System.Text.Json;
using TimeSuggestions.Configuration;
using TimeSuggestions.Models;
using TimeSuggestions.Services;

namespace TimeSuggestions.Tests;

/// <summary>
/// Testy tabelaryczne silnika sesji na fixture JSON (TestData/document-session-cases.json):
/// pojedyncza wersja, seria gęsta, luki 20/40 min, domknięcie przedziałów (dokładnie
/// 15/30 min), odwrotna kolejność, duplikaty, przejście przez północ i obie zmiany czasu.
/// Oczekiwane czasy lokalne w strefie Europe/Warsaw (domyślna konfiguracja).
/// </summary>
public class DocumentSessionEngineTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record ExpectedGap(DateTime StartAtLocal, DateTime EndAtLocal, int Minutes);

    private sealed record ExpectedSession(
        DateTime StartAtLocal,
        DateTime EndAtLocal,
        int GrossMinutes,
        int VersionCount,
        List<ExpectedGap> Gaps);

    private sealed record SessionCase(
        string Name,
        List<DateTime> VersionsUtc,
        List<ExpectedSession> ExpectedSessions);

    private static List<SessionCase> LoadCases()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "document-session-cases.json");
        return JsonSerializer.Deserialize<List<SessionCase>>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidOperationException($"Nie udało się wczytać fixture: {path}");
    }

    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var sessionCase in LoadCases())
        {
            data.Add(sessionCase.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void BuildSessions_PrzypadekZFixture(string caseName)
    {
        var sessionCase = LoadCases().Single(candidate => candidate.Name == caseName);
        var engine = new DocumentSessionEngine(new SuggestionOptions());
        var activities = sessionCase.VersionsUtc
            .Select((occurredAt, index) => new DocumentActivity
            {
                ExternalId = "file-1",
                VersionId = $"{index + 1}.0",
                OccurredAt = occurredAt,
                Size = 1000,
                RecordedAt = occurredAt,
            })
            .ToList();

        var sessions = engine.BuildSessions(activities);

        Assert.Equal(sessionCase.ExpectedSessions.Count, sessions.Count);
        foreach (var (expected, actual) in sessionCase.ExpectedSessions.Zip(sessions))
        {
            Assert.Equal(expected.StartAtLocal, actual.StartAt);
            Assert.Equal(expected.EndAtLocal, actual.EndAt);
            Assert.Equal(expected.GrossMinutes, actual.GrossMinutes);
            Assert.Equal(expected.VersionCount, actual.VersionCount);
            Assert.Equal(expected.Gaps.Count, actual.DetectedGaps.Count);
            foreach (var (expectedGap, actualGap) in expected.Gaps.Zip(actual.DetectedGaps))
            {
                Assert.Equal(expectedGap.StartAtLocal, actualGap.StartAt);
                Assert.Equal(expectedGap.EndAtLocal, actualGap.EndAt);
                Assert.Equal(expectedGap.Minutes, actualGap.Minutes);
            }
        }
    }

    [Fact]
    public void BuildSessions_PusteWejscieDajePustaListe()
    {
        var engine = new DocumentSessionEngine(new SuggestionOptions());

        Assert.Empty(engine.BuildSessions([]));
    }

    [Fact]
    public void BuildSessions_KotwicaToCzasPierwszejWersjiUtc()
    {
        var engine = new DocumentSessionEngine(new SuggestionOptions());
        var first = new DateTime(2026, 7, 24, 8, 0, 0, DateTimeKind.Utc);
        var activities = new List<DocumentActivity>
        {
            new() { ExternalId = "f", VersionId = "2.0", OccurredAt = first.AddMinutes(10), Size = 0, RecordedAt = first },
            new() { ExternalId = "f", VersionId = "1.0", OccurredAt = first, Size = 0, RecordedAt = first },
        };

        var session = Assert.Single(engine.BuildSessions(activities));

        // Kotwica jest stabilna, gdy sesja rośnie na końcu — to fundament klucza dedupu.
        Assert.Equal(first, session.AnchorUtc);
    }

    [Fact]
    public void DetectedGaps_SerializacjaOddajeTeSameWartosci()
    {
        var gaps = new List<DetectedGap>
        {
            new(new DateTime(2026, 7, 24, 10, 0, 0), new DateTime(2026, 7, 24, 10, 20, 0)),
        };

        var roundTripped = DetectedGaps.Deserialize(DetectedGaps.Serialize(gaps));

        var gap = Assert.Single(roundTripped);
        Assert.Equal(gaps[0].StartAt, gap.StartAt);
        Assert.Equal(gaps[0].EndAt, gap.EndAt);
        Assert.Equal(20, gap.Minutes);
    }

    [Fact]
    public void DetectedGaps_BrakPrzerwSerializujeSieDoNull()
    {
        // null zamiast "[]" — pusta kolumna nie zajmuje miejsca i czytelnie mówi "brak przerw".
        Assert.Null(DetectedGaps.Serialize([]));
        Assert.Empty(DetectedGaps.Deserialize(null));
        Assert.Empty(DetectedGaps.Deserialize("uszkodzony-json"));
    }
}
