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
/// <param name="StartAt">Lokalny początek sesji (pierwszy zapis).</param>
/// <param name="EndAt">Lokalny koniec sesji (ostatni zapis; minimum tylko wtedy, gdy oba są w tej samej minucie).</param>
/// <param name="AnchorUtc">Czas pierwszej wersji sesji (UTC) — kotwica dedupu sugestii.</param>
/// <param name="LastActivityUtc">Czas ostatniej wersji sesji (UTC) — koniec zasięgu sesji.</param>
/// <param name="GrossMinutes">Czas brutto sesji (z wykrytymi przerwami, bez odejmowania).</param>
/// <param name="DetectedGaps">Przerwy 15–30 min wewnątrz sesji (czasy lokalne).</param>
/// <param name="VersionCount">Liczba wersji składających się na sesję.</param>
public record DocumentSession(
    DateTime StartAt,
    DateTime EndAt,
    DateTime AnchorUtc,
    DateTime LastActivityUtc,
    int GrossMinutes,
    IReadOnlyList<DetectedGap> DetectedGaps,
    int VersionCount)
{
    /// <summary>
    /// Sesja, z której historia nie wyciąga ŻADNEGO czasu: wszystkie zapisy mieszczą się
    /// w jednej minucie (typowo jeden zapis). Czas jest wtedy nieznany, a nie „minimalny",
    /// więc UI prosi o wpisanie go ręcznie. Warunek liczy się z ZASIĘGU, nie z liczby
    /// wersji: dwa zapisy sekundę po sobie mówią o długości pracy dokładnie tyle samo,
    /// co jeden, a dwa zapisy oddalone o dwie minuty mówią wprost, ile trwała.
    /// </summary>
    public bool NeedsTimeReview => Math.Round((LastActivityUtc - AnchorUtc).TotalMinutes) == 0;
}

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
/// - sesja zaczyna się DOKŁADNIE w momencie pierwszego zapisu i kończy w momencie
///   ostatniego — nic nie jest doliczane „przed" ani „po". Był tu kiedyś rozbieg
///   (10 minut doklejanych z góry, bo „zapis następuje po jakiejś pracy"), ale to było
///   ZAŁOŻENIE udające pomiar i mylące się na niekorzyść KLIENTA. Odpadło razem
///   z domyślnym czasem dokumentu: aplikacja rozlicza to, co widać w historii,
///   a czas sprzed pierwszego zapisu prawnik dopisuje świadomie (Edytuj albo
///   doliczenie wolnej luki);
/// - sesja, której zasięg nie wypełnia pełnej minuty (jeden zapis albo kilka w tej samej
///   minucie), dostaje MinimumSessionMinutes i NeedsTimeReview — historia nie niesie
///   wtedy żadnej informacji o czasie pracy;
/// - sesja ZMIERZONA zostaje przy swojej długości, choćby to były dwie minuty. Minimum
///   zastępuje BRAK pomiaru, nie poprawia pomiaru krótkiego.
///
/// Czas: przerwy i czas brutto liczone z instantów UTC (różnica lokalna kłamie w noc
/// zmiany czasu — ta sama konwencja co BusinessTime), a granica dnia i wyjściowe
/// StartAt/EndAt w strefie biznesowej.
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

        // Sesja to dokładnie odcinek między pierwszym a ostatnim zapisem. Czas brutto
        // liczony z instantów UTC, żeby noc zmiany czasu nie zafałszowała minut.
        var startLocal = firstLocal;
        var measuredMinutes = (int)Math.Round((lastUtc - firstUtc).TotalMinutes);

        // Minimum wchodzi TYLKO tam, gdzie pomiaru nie ma. Wcześniej dostawała je każda
        // sesja krótsza od progu, więc zmierzone dwie minuty pracy szły do rozliczenia
        // jako pięć — trzy minuty dopisane klientowi, w dodatku niewidocznie: karta
        // pokazywała „początek 16:51, ostatnia zmiana 16:53" obok „czas pracy 5 min"
        // i te liczby nie dawały się pogodzić. To ten sam błąd, przez który wyleciał
        // rozbieg i domyślny czas dokumentu: zgadywanie na niekorzyść klienta.
        var hasMeasurement = measuredMinutes > 0;
        var endLocal = hasMeasurement ? lastLocal : startLocal.AddMinutes(options.MinimumSessionMinutes);
        var grossMinutes = hasMeasurement ? measuredMinutes : options.MinimumSessionMinutes;

        return new DocumentSession(
            StartAt: startLocal,
            EndAt: endLocal,
            AnchorUtc: firstUtc,
            LastActivityUtc: lastUtc,
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
