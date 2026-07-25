using TimeSuggestions.Services;

namespace TimeSuggestions.Tests;

public class TextNormalizerTests
{
    [Fact]
    public void Normalize_UjednolicaWielkoscLiter()
    {
        Assert.Equal("umowa klientx", TextNormalizer.Normalize("UMOWA KlientX"));
    }

    [Fact]
    public void Normalize_UsuwaPolskieDiakrytyki()
    {
        Assert.Equal("grzegrzolka zolc", TextNormalizer.Normalize("Grzegrzółka Żółć"));
    }

    [Fact]
    public void Normalize_UsuwaLZKreska_KtorejNieLapieDekompozycjaUnicode()
    {
        Assert.Equal("lodz", TextNormalizer.Normalize("Łódź"));
    }

    [Theory]
    [InlineData("umowa_klientx", "umowa klientx")]
    [InlineData("umowa.klientx", "umowa klientx")]
    [InlineData("umowa-klientx", "umowa klientx")]
    [InlineData("umowa/klientx", "umowa klientx")]
    public void Normalize_ZamieniaSeparatoryNaSpacje(string input, string expected)
    {
        Assert.Equal(expected, TextNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData("Umowa_KlientX_v2", "umowa klientx")]
    [InlineData("Umowa KlientX (1)", "umowa klientx")]
    [InlineData("Umowa KlientX final", "umowa klientx")]
    [InlineData("Umowa KlientX kopia", "umowa klientx")]
    public void Normalize_PomijaSufiksyWersji(string input, string expected)
    {
        Assert.Equal(expected, TextNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData("Umowa_KlientX_v2.docx", "umowa klientx")]
    [InlineData("Raport.xlsx", "raport")]
    public void Normalize_UsuwaRozszerzeniePliku(string input, string expected)
    {
        Assert.Equal(expected, TextNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_ZwracaPustyTekstDlaPustegoWejscia(string? input)
    {
        Assert.Equal(string.Empty, TextNormalizer.Normalize(input));
    }
}
