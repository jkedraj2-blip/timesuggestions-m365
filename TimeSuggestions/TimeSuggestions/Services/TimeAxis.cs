namespace TimeSuggestions.Services;

/// <summary>
/// Arytmetyka osi dnia w JEDNYM miejscu — wcześniej te same reguły stały w dwóch
/// serwisach i rozjeżdżały się przy każdej poprawce.
///
/// Jednostką rozliczenia jest MINUTA i to ona rządzi obiema regułami:
/// wolny czas obcinamy w dół (minuta zaokrąglona w górę to minuta, której w przerwie
/// nie ma), a nakładanie krótsze niż minuta nie jest nakładaniem. To drugie nie jest
/// pobłażliwością wobec danych, tylko warunek sensownych komunikatów: decyzja podjęta
/// na sekundach jest dla użytkownika niesprawdzalna, bo interfejs operuje minutami,
/// a odmowa, której nie da się zrozumieć, blokuje rozliczenie bez powodu.
/// </summary>
public static class TimeAxis
{
    /// <summary>Minuty wolnego czasu (podłoga) albo null, gdy pozycje na siebie zachodzą.</summary>
    public static int? FreeGapMinutes(DateTime startAt, DateTime endAt)
    {
        var gap = endAt - startAt;
        return gap < TimeSpan.Zero ? null : (int)gap.TotalMinutes;
    }

    /// <summary>Czy przedziały dzielą co najmniej jedną minutę.</summary>
    public static bool Overlaps(DateTime firstStart, DateTime firstEnd, DateTime secondStart, DateTime secondEnd)
    {
        var start = firstStart > secondStart ? firstStart : secondStart;
        var end = firstEnd < secondEnd ? firstEnd : secondEnd;
        return end - start >= TimeSpan.FromMinutes(1);
    }
}
