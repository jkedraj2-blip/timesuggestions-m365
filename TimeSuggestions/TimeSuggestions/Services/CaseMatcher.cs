using TimeSuggestions.Models;

namespace TimeSuggestions.Services;

public enum MatchKind
{
    None,
    Single,
    Multiple,
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
/// Dopasowanie tekstu (tytułu spotkania / nazwy pliku) do sprawy — proste porównanie
/// zawierania na znormalizowanym tekście, bez uczenia maszynowego (świadoma decyzja zakresu).
/// </summary>
public static class CaseMatcher
{
    public static MatchResult Match(string? rawText, IEnumerable<Case> activeCases)
    {
        var normalizedText = TextNormalizer.Normalize(rawText);
        if (normalizedText.Length == 0)
        {
            return MatchResult.None;
        }

        var matchedCases = activeCases
            .Where(candidate => MatchesAnyTerm(normalizedText, candidate))
            .ToList();

        return matchedCases.Count switch
        {
            0 => MatchResult.None,
            1 => MatchResult.Single(matchedCases[0]),
            _ => MatchResult.Multiple(matchedCases),
        };
    }

    private static bool MatchesAnyTerm(string normalizedText, Case candidate)
        => GetSearchTerms(candidate)
            .Select(TextNormalizer.Normalize)
            .Any(term => term.Length > 0 && normalizedText.Contains(term, StringComparison.Ordinal));

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
