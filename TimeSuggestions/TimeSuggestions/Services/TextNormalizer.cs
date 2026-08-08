using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TimeSuggestions.Services;

/// <summary>
/// Normalizacja tekstu przed dopasowaniem do spraw. Czysta funkcja — bez stanu i zależności.
/// Dzięki normalizacji "Umowa_KlientX_v2.docx" i "umowa klientx" porównują się tak samo.
///
/// Dwa jawne tryby:
/// - <see cref="NormalizeDocumentName"/> — pipeline dla NAZW PLIKÓW: rozszerzenie,
///   diakrytyki, separatory i tokeny vN/(N). Słowa takie jak "final"/"kopia"
///   ZOSTAJĄ — dopasowanie po ciągu pełnych tokenów nie potrzebuje ich usuwać
///   ("umowa klientx" i tak trafia w "Umowa_KlientX_final.docx"), a ich obcinanie
///   po cichu poszerzało terminy spraw i produkowało fałszywe przypisania.
/// - <see cref="NormalizeText"/> — tryb leksykalny dla terminów spraw i tytułów spotkań:
///   tylko wielkość liter, diakrytyki i separatory. Rozszerzenia plików i tokeny
///   wersji to zjawiska nazw plików — w prozie "v2" jest zwykłym słowem ("Sprint v2").
/// </summary>
public static partial class TextNormalizer
{
    private static readonly string[] FileExtensions = [".docx", ".doc", ".xlsx", ".xls"];

    [GeneratedRegex(@"^v\d+$")]
    private static partial Regex VersionTokenPattern();

    [GeneratedRegex(@"^\(\d+\)$")]
    private static partial Regex CopyNumberTokenPattern();

    /// <summary>Normalizacja nazwy pliku — z rozszerzeniem i tokenami wersji vN/(N).</summary>
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

        // Tokeny vN i (N) są w nazwach plików jednoznacznymi oznaczeniami wersji —
        // usuwane wszędzie, żeby "Umowa_v2_KlientX" pasowało do terminu "umowa klientx".
        // Słów "final"/"kopia" NIE ruszamy: to część widocznej nazwy, a ich obcinanie
        // poszerzało terminy spraw (keyword "raport final" degradował się do "raport").
        var meaningfulTokens = tokens
            .Where(token => !IsVersionToken(token))
            .ToList();

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
