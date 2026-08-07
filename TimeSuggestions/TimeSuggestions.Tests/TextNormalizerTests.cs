using TimeSuggestions.Services;

namespace TimeSuggestions.Tests;

public class TextNormalizerTests
{
    [Fact]
    public void NormalizeDocumentName_UjednolicaWielkoscLiter()
    {
        Assert.Equal("umowa klientx", TextNormalizer.NormalizeDocumentName("UMOWA KlientX"));
    }

    [Fact]
    public void NormalizeDocumentName_UsuwaPolskieDiakrytyki()
    {
        Assert.Equal("grzegrzolka zolc", TextNormalizer.NormalizeDocumentName("Grzegrzółka Żółć"));
    }

    [Fact]
    public void NormalizeDocumentName_UsuwaLZKreska_KtorejNieLapieDekompozycjaUnicode()
    {
        Assert.Equal("lodz", TextNormalizer.NormalizeDocumentName("Łódź"));
    }

    [Theory]
    [InlineData("umowa_klientx", "umowa klientx")]
    [InlineData("umowa.klientx", "umowa klientx")]
    [InlineData("umowa-klientx", "umowa klientx")]
    [InlineData("umowa/klientx", "umowa klientx")]
    public void NormalizeDocumentName_ZamieniaSeparatoryNaSpacje(string input, string expected)
    {
        Assert.Equal(expected, TextNormalizer.NormalizeDocumentName(input));
    }

    [Theory]
    [InlineData("Umowa_KlientX_v2", "umowa klientx")]
    [InlineData("Umowa KlientX (1)", "umowa klientx")]
    [InlineData("Umowa KlientX final", "umowa klientx")]
    [InlineData("Umowa KlientX kopia", "umowa klientx")]
    [InlineData("Umowa KlientX final kopia", "umowa klientx")]
    [InlineData("analiza-kopia.docx", "analiza")]
    public void NormalizeDocumentName_PomijaSufiksyWersji(string input, string expected)
    {
        Assert.Equal(expected, TextNormalizer.NormalizeDocumentName(input));
    }

    [Theory]
    [InlineData("Analiza final klienta", "analiza final klienta")]
    [InlineData("Kopia umowy KlientX", "kopia umowy klientx")]
    public void NormalizeDocumentName_ZachowujeSlowaWersjiWSrodkuTekstu(string input, string expected)
    {
        // "final"/"kopia" to oznaczenia wersji tylko na końcu nazwy pliku —
        // w środku tekstu są zwykłymi słowami i zostają.
        Assert.Equal(expected, TextNormalizer.NormalizeDocumentName(input));
    }

    [Theory]
    [InlineData("Umowa_KlientX_v2.docx", "umowa klientx")]
    [InlineData("Raport.xlsx", "raport")]
    public void NormalizeDocumentName_UsuwaRozszerzeniePliku(string input, string expected)
    {
        Assert.Equal(expected, TextNormalizer.NormalizeDocumentName(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeDocumentName_ZwracaPustyTekstDlaPustegoWejscia(string? input)
    {
        Assert.Equal(string.Empty, TextNormalizer.NormalizeDocumentName(input));
    }

    [Theory]
    [InlineData("raport final", "raport final")]
    [InlineData("Biuro Kopia", "biuro kopia")]
    [InlineData("final", "final")]
    [InlineData("kopia", "kopia")]
    public void NormalizeText_ZachowujeKoncoweSlowaWersji(string input, string expected)
    {
        // Tryb leksykalny: "final"/"kopia" w terminach spraw i tytułach spotkań
        // to zwykłe słowa — jednowyrazowy keyword "final" nie może stać się pusty,
        // a klient "Biuro Kopia" nie może zostać ucięty.
        Assert.Equal(expected, TextNormalizer.NormalizeText(input));
    }

    [Theory]
    [InlineData("Sprint v2", "sprint v2")]
    [InlineData("Przegląd (2)", "przeglad (2)")]
    public void NormalizeText_ZachowujeTokenyWersji(string input, string expected)
    {
        // vN i (N) są jednoznacznymi oznaczeniami wersji tylko w nazwach plików —
        // w tytule spotkania "Sprint v2" to pełnoprawne słowo.
        Assert.Equal(expected, TextNormalizer.NormalizeText(input));
    }

    [Fact]
    public void NormalizeText_NieUsuwaRozszerzeniaPliku()
    {
        // Terminy spraw i tytuły spotkań nie są nazwami plików — ".docx" na końcu
        // tekstu leksykalnego to zwykłe tokeny po zamianie separatorów.
        Assert.Equal("omowienie raport docx", TextNormalizer.NormalizeText("Omówienie raport.docx"));
    }

    [Fact]
    public void NormalizeText_UjednolicaWielkoscLiterDiakrytykiISeparatory()
    {
        Assert.Equal("umowa klientx zolc", TextNormalizer.NormalizeText("UMOWA_KlientX-Żółć"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeText_ZwracaPustyTekstDlaPustegoWejscia(string? input)
    {
        Assert.Equal(string.Empty, TextNormalizer.NormalizeText(input));
    }
}
