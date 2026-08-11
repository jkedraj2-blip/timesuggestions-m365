using System.Text.Json;
using TimeSuggestions.Configuration;
using TimeSuggestions.Models;

namespace TimeSuggestions.Services;

/// <summary>
/// Wykryta przerwa wewnątrz sesji (15–30 min między wersjami) — czasy w strefie
/// biznesowej, na tej samej osi co StartedAt sugestii i wpisu.
/// </summary>
public record DetectedGap(DateTime StartAt, DateTime EndAt)
{
    public int Minutes => (int)Math.Round((EndAt - StartAt).TotalMinutes);
}

/// <summary>
/// Serializacja wykrytych przerw do kolumny JSON. Kolumna zamiast osobnej tabeli —
/// świadomy wybór: przerwy są niemutowalnym atrybutem wyliczonej sesji, czytanym
/// zawsze razem z sugestią/wpisem i nigdy nie filtrowanym relacyjnie; tabela
/// dokładałaby join po listę o typowej długości 0–2 elementów.
/// </summary>
public static class DetectedGaps
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string? Serialize(IReadOnlyList<DetectedGap> gaps)
        => gaps.Count == 0 ? null : JsonSerializer.Serialize(gaps, JsonOptions);

    public static IReadOnlyList<DetectedGap> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<DetectedGap>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            // Uszkodzona kolumna nie może wysypać listy sugestii — brak przerw
            // degraduje tylko przycisk "Odejmij przerwę", nie całą pozycję.
            return [];
        }
    }
}

/// <summary>Jedna sesja pracy nad plikiem wyliczona z historii wersji.</summary>
/// <param name="StartAt">Lokalny początek sesji (pierwsza wersja minus rozbieg, przycięty do północy).</param>
/// <param name="EndAt">Lokalny koniec sesji (ostatnia wersja; wydłużony do minimum przy sesji z jedną wersją).</param>
/// <param name="AnchorUtc">Czas pierwszej wersji sesji (UTC) — kotwica dedupu sugestii.</param>
/// <param name="GrossMinutes">Czas brutto sesji (z wykrytymi przerwami, bez odejmowania).</param>
/// <param name="DetectedGaps">Przerwy 15–30 min wewnątrz sesji (czasy lokalne).</param>
/// <param name="VersionCount">Liczba wersji składających się na sesję.</param>
public record DocumentSession(
    DateTime StartAt,
    DateTime EndAt,
    DateTime AnchorUtc,
    int GrossMinutes,
    IReadOnlyList<DetectedGap> DetectedGaps,
    int VersionCount);

/// <summary>
/// Silnik sesji: tnie historię wersji jednego pliku (DocumentActivity) na sesje pracy.
/// Czysta logika bez I/O — konfiguracja przez konstruktor, wynik deterministyczny.
///
/// Reguły (progi w SuggestionOptions, zero zaszytych liczb):
/// - przerwa ≤ SessionContinuationGapMinutes: jedna ciągła sesja (dokładnie próg przechodzi);
/// - przerwa (SessionContinuationGapMinutes, SessionFlaggedGapMinutes]: jedna sesja,
///   luka zapisana jako wykryta przerwa (dokładnie górny próg to jeszcze przerwa, nie cięcie);
/// - przerwa powyżej SessionFlaggedGapMinutes: nowa sesja;
/// - sesje NIE przechodzą granicy dnia w strefie biznesowej: wersja po północy otwiera
///   nową sesję niezależnie od progu (EntryDate jest osią grupowania i archiwizacji,
///   więc sesja spinająca dwa dni nie miałaby jednej daty);
/// - sesja zaczyna się SessionLeadInMinutes przed pierwszą wersją (wersja to moment
///   zapisu, nie otwarcia), ale rozbieg jest przycinany do północy — patrz wyżej;
/// - sesja krótsza niż MinimumSessionMinutes jest wydłużana do minimum (sesja
///   z jedną wersją nie może wyjść zerowa).
///
/// Czas: przerwy i czas brutto liczone z instantów UTC (różnica lokalna kłamie w noc
/// zmiany czasu — ta sama konwencja co BusinessTime), a granica dnia i wyjściowe
/// StartAt/EndAt w strefie biznesowej. Rozbieg odjęty na osi lokalnej może wskazać
/// czas nieistniejący (wiosenna luka) — to tylko etykieta początku, arytmetyka
/// czasu trwania na tym nie polega.
/// </summary>
public class DocumentSessionEngine(SuggestionOptions options)
{
    private readonly TimeZoneInfo businessTimeZone = TimeZoneInfo.FindSystemTimeZoneById(options.BusinessTimeZoneId);

    public List<DocumentSession> BuildSessions(IEnumerable<DocumentActivity> activities)
    {
        // Porządkowanie wejścia należy do silnika: sync może donieść wersje w dowolnej
        // kolejności (stronicowanie, zaległa wersja), a duplikat czasu (np. ta sama
        // wersja pod dwoma id po przywróceniu pliku) nie może sztucznie wydłużać sesji.
        var ordered = activities
            .Select(activity => AsUtc(activity.OccurredAt))
            .Distinct()
            .OrderBy(occurredAt => occurredAt)
            .ToList();

        var sessions = new List<DocumentSession>();
        if (ordered.Count == 0)
        {
            return sessions;
        }

        var current = new List<(DateTime Utc, DateTime Local)>();
        var gaps = new List<DetectedGap>();

        foreach (var utc in ordered)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(utc, businessTimeZone);

            if (current.Count > 0)
            {
                var previous = current[^1];
                var gapMinutes = (utc - previous.Utc).TotalMinutes;

                if (gapMinutes > options.SessionFlaggedGapMinutes || local.Date != previous.Local.Date)
                {
                    sessions.Add(FinalizeSession(current, gaps));
                    current = [];
                    gaps = [];
                }
                else if (gapMinutes > options.SessionContinuationGapMinutes)
                {
                    gaps.Add(new DetectedGap(previous.Local, local));
                }
            }

            current.Add((utc, local));
        }

        sessions.Add(FinalizeSession(current, gaps));
        return sessions;
    }

    private DocumentSession FinalizeSession(
        IReadOnlyList<(DateTime Utc, DateTime Local)> versions,
        List<DetectedGap> gaps)
    {
        var (firstUtc, firstLocal) = versions[0];
        var (lastUtc, _) = versions[^1];
        var (_, lastLocal) = versions[^1];

        // Rozbieg przycięty do północy: sesja nie może zacząć się poprzedniego dnia,
        // bo EntryDate (z lokalnego startu) jest osią grupowania i archiwizacji.
        var leadInMinutes = Math.Min(options.SessionLeadInMinutes, firstLocal.TimeOfDay.TotalMinutes);
        var startLocal = firstLocal.AddMinutes(-leadInMinutes);

        // Czas brutto z instantów UTC + rozbieg (rozbieg to korekta zegara ściennego,
        // dodawana osobno, żeby noc zmiany czasu nie zafałszowała minut).
        var grossMinutes = (int)Math.Round((lastUtc - firstUtc).TotalMinutes + leadInMinutes);

        var endLocal = lastLocal;
        if (grossMinutes < options.MinimumSessionMinutes)
        {
            endLocal = startLocal.AddMinutes(options.MinimumSessionMinutes);
            grossMinutes = options.MinimumSessionMinutes;
        }

        return new DocumentSession(
            StartAt: startLocal,
            EndAt: endLocal,
            AnchorUtc: firstUtc,
            GrossMinutes: grossMinutes,
            DetectedGaps: gaps,
            VersionCount: versions.Count);
    }

    /// <summary>OccurredAt zapisujemy w UTC, ale po odczycie z SQLite Kind bywa Unspecified.</summary>
    private static DateTime AsUtc(DateTime dateTime) => dateTime.Kind switch
    {
        DateTimeKind.Utc => dateTime,
        DateTimeKind.Local => dateTime.ToUniversalTime(),
        _ => DateTime.SpecifyKind(dateTime, DateTimeKind.Utc),
    };
}
