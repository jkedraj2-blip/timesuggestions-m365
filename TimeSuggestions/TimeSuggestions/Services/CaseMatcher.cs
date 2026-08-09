using TimeSuggestions.Models;

namespace TimeSuggestions.Services;

public enum MatchKind
{
    None,
    Single,
    Multiple,
}

/// <summary>
/// Skąd pochodzi dopasowywany tekst — wybiera tryb normalizacji (nazwy plików
/// przechodzą pełny pipeline plikowy, tytuły spotkań tylko leksykalny).
/// </summary>
public enum MatchTextSource
{
    MeetingTitle,
    DocumentName,
}

/// <summary>
/// Jawny wynik dopasowania zamiast null/wyjątków — trzy stany muszą być
/// rozróżnialne w interfejsie (przypisana sprawa / do sprawdzenia / niejednoznaczna).
/// </summary>
public record MatchResult(MatchKind Kind, Case? MatchedCase, IReadOnlyList<Case> Candidates)
{
    public static MatchResult None { get; } = new(MatchKind.None, null, []);

    public static MatchResult Single(Case matchedCase) => new(MatchKind.Single, matchedCase, [matchedCase]);

    public static MatchResult Multiple(IReadOnlyList<Case> candidates) => new(MatchKind.Multiple, null, candidates);
}

/// <summary>
/// Dopasowanie tekstu (tytułu spotkania / nazwy pliku) do sprawy — porównanie
/// pełnych tokenów znormalizowanego tekstu, bez uczenia maszynowego (świadoma decyzja zakresu).
/// Termin jednowyrazowy pasuje tylko do identycznego tokenu, wielowyrazowy — do ciągu
/// kolejnych pełnych tokenów; dzięki temu "Alfa" nie pasuje do "Alfabet".
/// Gdy trafienie jednej sprawy zawiera się W CAŁOŚCI w dłuższym trafieniu innej,
/// wygrywa dłuższe: "Audyt Beta Logistics" idzie do klienta "Beta Logistics"
/// (2 tokeny), a nie do sprawy ze słowem kluczowym "Beta" (1 token) — słowo "beta"
/// jest tu częścią dłuższej nazwy. Trafienia ROZŁĄCZNE lub tylko ZAHACZAJĄCE o siebie
/// to osobne dowody i długość niczego nie rozstrzyga: "Omówienie NT-2026-113
/// z Kowalski" wymienia dwie sprawy i pozostaje niejednoznaczne, choć numer sprawy
/// ma więcej tokenów niż nazwisko.
/// Remis długości też pozostaje niejednoznaczny ("Analiza Beta" przy dwóch
/// sprawach z keywordem "Beta").
/// Świadomy kompromis: odmiany fleksyjne ("Kowalskiego") nie są dopasowywane —
/// można je dodać jako słowa kluczowe sprawy.
/// </summary>
public static class CaseMatcher
{
    public static MatchResult Match(string? rawText, IEnumerable<Case> activeCases, MatchTextSource source)
    {
        // Tekst normalizowany trybem właściwym dla ŹRÓDŁA (nazwa pliku traci
        // rozszerzenie i tokeny vN/(N)); terminy sprawy ZAWSZE leksykalnie — termin
        // jest kryterium zapisanym przez użytkownika, nie nazwą pliku, i nie wolno go
        // po cichu skracać. Keyword "raport final" pasuje więc do "raport-final.docx"
        // (obie strony spotykają się jako "raport final"), ale NIE do "Raport
        // roboczy.docx"; jednowyrazowy keyword "final" nie staje się pustym terminem.
        var normalize = source == MatchTextSource.DocumentName
            ? TextNormalizer.NormalizeDocumentName
            : (Func<string?, string>)TextNormalizer.NormalizeText;

        var normalizedText = normalize(rawText);
        if (normalizedText.Length == 0)
        {
            return MatchResult.None;
        }

        var textTokens = normalizedText.Split(' ');
        var matchedCases = activeCases
            .Select(candidate => (Case: candidate, Occurrences: FindTermOccurrences(textTokens, candidate)))
            .Where(matched => matched.Occurrences.Count > 0)
            .ToList();

        if (matchedCases.Count == 0)
        {
            return MatchResult.None;
        }

        // Sprawa odpada tylko wtedy, gdy KAŻDE jej trafienie zawiera się W CAŁOŚCI
        // w ściśle dłuższym trafieniu innej sprawy — wtedy jej słowa są jedynie
        // fragmentem cudzej, pełniejszej nazwy. Samo zahaczenie o wspólne słowo nie
        // wystarcza: trafienie wystające poza cudze choćby jednym tokenem (np.
        // "logistics polska" obok "grupa beta logistics") niesie dowód, którego
        // dłuższe trafienie nie tłumaczy. Trafienie w innym miejscu tekstu to również
        // niezależny dowód i sprawa zostaje kandydatem; przy spotkaniu
        // międzysprawowym decyzję musi podjąć użytkownik.
        var candidates = matchedCases
            .Where(matched => matched.Occurrences.Any(occurrence =>
                !matchedCases.Any(other => other.Case != matched.Case
                    && other.Occurrences.Any(otherOccurrence =>
                        otherOccurrence.Length > occurrence.Length
                        && Contains(otherOccurrence, occurrence)))))
            .Select(matched => matched.Case)
            .ToList();

        return candidates.Count == 1
            ? MatchResult.Single(candidates[0])
            : MatchResult.Multiple(candidates);
    }

    /// <summary>Wystąpienie terminu w tekście: indeks pierwszego tokenu i długość w tokenach.</summary>
    private readonly record struct TermOccurrence(int Start, int Length);

    private static bool Contains(TermOccurrence outer, TermOccurrence inner)
        => outer.Start <= inner.Start && inner.Start + inner.Length <= outer.Start + outer.Length;

    /// <summary>
    /// Wszystkie wystąpienia wszystkich terminów sprawy jako ciągów kolejnych pełnych
    /// tokenów tekstu. Numery spraw działają bez zmian: normalizacja zamienia separatory
    /// na spacje, więc "NT-2026-113" i tekst "Analiza NT-2026-113" dają te same tokeny.
    /// </summary>
    private static List<TermOccurrence> FindTermOccurrences(string[] textTokens, Case candidate)
    {
        var occurrences = new List<TermOccurrence>();
        foreach (var term in GetSearchTerms(candidate))
        {
            var normalizedTerm = TextNormalizer.NormalizeText(term);
            if (normalizedTerm.Length == 0)
            {
                continue;
            }

            var termTokens = normalizedTerm.Split(' ');
            for (var start = 0; start <= textTokens.Length - termTokens.Length; start++)
            {
                if (MatchesAt(textTokens, termTokens, start))
                {
                    occurrences.Add(new TermOccurrence(start, termTokens.Length));
                }
            }
        }

        return occurrences;
    }

    private static bool MatchesAt(string[] textTokens, string[] termTokens, int start)
    {
        for (var offset = 0; offset < termTokens.Length; offset++)
        {
            if (!string.Equals(textTokens[start + offset], termTokens[offset], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<string> GetSearchTerms(Case candidate)
    {
        yield return candidate.ClientName;
        yield return candidate.CaseNumber;

        foreach (var keyword in candidate.Keywords.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            yield return keyword;
        }
    }
}
