using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TimeSuggestions.Services;

/// <summary>
/// Normalizacja tekstu przed dopasowaniem do spraw. Czysta funkcja — bez stanu i zależności.
/// Dzięki normalizacji "Umowa_KlientX_v2.docx" i "umowa klientx" porównują się tak samo.
/// </summary>
public static partial class TextNormalizer
{
    private static readonly string[] FileExtensions = [".docx", ".doc", ".xlsx", ".xls"];

    // Słowa-sufiksy wersji spotykane w nazwach plików; usuwane w całości jako tokeny.
    private static readonly string[] VersionWords = ["final", "kopia"];

    [GeneratedRegex(@"^v\d+$")]
    private static partial Regex VersionTokenPattern();

    [GeneratedRegex(@"^\(\d+\)$")]
    private static partial Regex CopyNumberTokenPattern();

    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var lowered = text.Trim().ToLowerInvariant();
        lowered = RemoveFileExtension(lowered);
        lowered = RemoveDiacritics(lowered);
        lowered = ReplaceSeparatorsWithSpaces(lowered);

        var meaningfulTokens = lowered
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => !IsVersionToken(token));

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
        => VersionWords.Contains(token)
           || VersionTokenPattern().IsMatch(token)
           || CopyNumberTokenPattern().IsMatch(token);
}
