using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TimeSuggestions.Services;

/// <summary>
/// Normalizacja tekstu przed dopasowaniem do spraw. Czysta funkcja — bez stanu i zależności.
/// Dzięki normalizacji "Umowa_KlientX_v2.docx" i "umowa klientx" porównują się tak samo.
///
/// Dwa jawne tryby:
/// - <see cref="NormalizeDocumentName"/> — pełny pipeline dla NAZW PLIKÓW: rozszerzenie,
///   diakrytyki, separatory, tokeny vN/(N) i końcowe słowa wersji ("final", "kopia").
/// - <see cref="NormalizeText"/> — tryb leksykalny dla terminów spraw i tytułów spotkań:
///   tylko wielkość liter, diakrytyki i separatory. Rozszerzenia plików i oznaczenia
///   wersji to zjawiska nazw plików — w prozie "final" czy "v2" są zwykłymi słowami
///   ("Final review", "Sprint v2"), a klient może nazywać się "Biuro Kopia".
/// </summary>
public static partial class TextNormalizer
{
    private static readonly string[] FileExtensions = [".docx", ".doc", ".xlsx", ".xls"];

    // Słowa-sufiksy wersji spotykane w nazwach plików; usuwane wyłącznie jako końcowe
    // tokeny (oznaczenie wersji pliku) — w środku tekstu to zwykłe słowa.
    private static readonly string[] VersionWords = ["final", "kopia"];

    [GeneratedRegex(@"^v\d+$")]
    private static partial Regex VersionTokenPattern();

    [GeneratedRegex(@"^\(\d+\)$")]
    private static partial Regex CopyNumberTokenPattern();

    /// <summary>Normalizacja nazwy pliku — pełny pipeline łącznie z oznaczeniami wersji.</summary>
    public static string NormalizeDocumentName(string? text) => NormalizeCore(text, isDocumentName: true);

    /// <summary>Normalizacja leksykalna (terminy spraw, tytuły spotkań) — bez logiki plikowej.</summary>
    public static string NormalizeText(string? text) => NormalizeCore(text, isDocumentName: false);

    private static string NormalizeCore(string? text, bool isDocumentName)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var lowered = text.Trim().ToLowerInvariant();
        if (isDocumentName)
        {
            lowered = RemoveFileExtension(lowered);
        }

        lowered = RemoveDiacritics(lowered);
        lowered = ReplaceSeparatorsWithSpaces(lowered);

        var tokens = lowered.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!isDocumentName)
        {
            return string.Join(' ', tokens);
        }

        // Tokeny vN i (N) są w nazwach plików jednoznacznymi oznaczeniami wersji — usuwane wszędzie.
        var meaningfulTokens = tokens
            .Where(token => !IsVersionToken(token))
            .ToList();

        // Słowa wersji ("final", "kopia") tylko z końca — "Analiza final klienta"
        // zachowuje "final" jako zwykłe słowo w środku tekstu.
        while (meaningfulTokens.Count > 0 && VersionWords.Contains(meaningfulTokens[^1]))
        {
            meaningfulTokens.RemoveAt(meaningfulTokens.Count - 1);
        }

        return string.Join(' ', meaningfulTokens);
    }

    private static string RemoveFileExtension(string text)
    {
        foreach (var extension in FileExtensions)
        {
            if (text.EndsWith(extension, StringComparison.Ordinal))
            {
                return text[..^extension.Length];
            }
        }

        return text;
    }

    private static string RemoveDiacritics(string text)
    {
        // Dekompozycja Unicode nie rozkłada "ł" na "l" + znak diakrytyczny, stąd jawna zamiana.
        var withoutLStroke = text.Replace('ł', 'l');

        var decomposed = withoutLStroke.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string ReplaceSeparatorsWithSpaces(string text)
        => text.Replace('_', ' ').Replace('.', ' ').Replace('-', ' ').Replace('/', ' ');

    private static bool IsVersionToken(string token)
        => VersionTokenPattern().IsMatch(token)
           || CopyNumberTokenPattern().IsMatch(token);
}
