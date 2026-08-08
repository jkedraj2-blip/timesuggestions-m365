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
/// Świadomy kompromis: odmiany fleksyjne ("Kowalskiego") nie są już dopasowywane —
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
            .Where(candidate => MatchesAnyTerm(textTokens, candidate))
            .ToList();

        return matchedCases.Count switch
        {
            0 => MatchResult.None,
            1 => MatchResult.Single(matchedCases[0]),
            _ => MatchResult.Multiple(matchedCases),
        };
    }

    private static bool MatchesAnyTerm(string[] textTokens, Case candidate)
        => GetSearchTerms(candidate)
            .Select(TextNormalizer.NormalizeText)
            .Where(term => term.Length > 0)
            .Any(term => ContainsTokenSequence(textTokens, term.Split(' ')));

    /// <summary>
    /// Sprawdza, czy tokeny terminu występują w tekście jako ciąg kolejnych pełnych tokenów.
    /// Numery spraw działają bez zmian: normalizacja zamienia separatory na spacje,
    /// więc "NT-2026-113" i tekst "Analiza NT-2026-113" dają te same tokeny.
    /// </summary>
    private static bool ContainsTokenSequence(string[] textTokens, string[] termTokens)
    {
        for (var start = 0; start <= textTokens.Length - termTokens.Length; start++)
        {
            var allMatch = true;
            for (var offset = 0; offset < termTokens.Length; offset++)
            {
                if (!string.Equals(textTokens[start + offset], termTokens[offset], StringComparison.Ordinal))
                {
                    allMatch = false;
                    break;
                }
            }

            if (allMatch)
            {
                return true;
            }
        }

        return false;
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
