using TimeSuggestions.Services;

namespace TimeSuggestions.Tests;

public class CaseMatcherTests
{
    [Fact]
    public void Match_ZwracaJednoTrafieniePoNazwieKlienta()
    {
        var result = CaseMatcher.Match("Spotkanie z Kowalski w biurze", TestHelpers.CreateTestCases());

        Assert.Equal(MatchKind.Single, result.Kind);
        Assert.Equal(1, result.MatchedCase?.Id);
    }

    [Fact]
    public void Match_ZwracaJednoTrafieniePoNumerzeSprawy()
    {
        var result = CaseMatcher.Match("Analiza dokumentów NT-2026-113", TestHelpers.CreateTestCases());

        Assert.Equal(MatchKind.Single, result.Kind);
        Assert.Equal(2, result.MatchedCase?.Id);
    }

    [Fact]
    public void Match_ZwracaBrakTrafieniaDlaTekstuBezSprawy()
    {
        var result = CaseMatcher.Match("Cotygodniowe spotkanie zespołu", TestHelpers.CreateTestCases());

        Assert.Equal(MatchKind.None, result.Kind);
        Assert.Null(result.MatchedCase);
    }

    [Fact]
    public void Match_ZwracaWieleTrafienDlaWspolnegoSlowaKluczowego()
    {
        // "Beta" jest słowem kluczowym spraw #4 i #5 — wynik musi być niejednoznaczny.
        var result = CaseMatcher.Match("Analiza Beta — przygotowanie", TestHelpers.CreateTestCases());

        Assert.Equal(MatchKind.Multiple, result.Kind);
        Assert.Null(result.MatchedCase);
        Assert.Equal(2, result.Candidates.Count);
    }

    [Fact]
    public void Match_DopasowujeNazwePlikuPoNormalizacji()
    {
        // Separator "_", sufiks wersji i rozszerzenie nie mogą przeszkodzić w dopasowaniu.
        var result = CaseMatcher.Match("Umowa_NovaTech_v2.docx", TestHelpers.CreateTestCases());

        Assert.Equal(MatchKind.Single, result.Kind);
        Assert.Equal(2, result.MatchedCase?.Id);
    }

    [Fact]
    public void Match_DopasowujeMimoRoznicyDiakrytykow()
    {
        // Nazwa pliku bez polskich znaków vs sprawa z diakrytykami.
        var result = CaseMatcher.Match("notatki_grzegrzolka.xlsx", TestHelpers.CreateTestCases());

        Assert.Equal(MatchKind.Single, result.Kind);
        Assert.Equal(3, result.MatchedCase?.Id);
    }

    [Fact]
    public void Match_NieDopasowujePodciaguWewnatrzInnegoSlowa()
    {
        // "Alfa" (słowo kluczowe sprawy #4) nie może pasować do "Alfabet" —
        // dopasowanie działa po pełnych tokenach, nie po podciągach.
        var result = CaseMatcher.Match("Alfabet_cwiczenia.docx", TestHelpers.CreateTestCases());

        Assert.Equal(MatchKind.None, result.Kind);
    }

    [Fact]
    public void Match_DopasowujeTerminWielowyrazowyJakoCiagKolejnychTokenow()
    {
        // "Alfa Holding" (nazwa klienta sprawy #4) pasuje jako ciąg pełnych tokenów.
        var result = CaseMatcher.Match("Prezentacja dla Alfa Holding", TestHelpers.CreateTestCases());

        Assert.Equal(MatchKind.Single, result.Kind);
        Assert.Equal(4, result.MatchedCase?.Id);
    }

    [Fact]
    public void Match_NieDopasowujeOdmianyFleksyjnej_SwiadomyKompromis()
    {
        // Kompromis dopasowania po pełnych tokenach: "Kowalskiego" ≠ "Kowalski".
        // Odmiany można dodać jako słowa kluczowe sprawy.
        var result = CaseMatcher.Match("Spotkanie u Kowalskiego", TestHelpers.CreateTestCases());

        Assert.Equal(MatchKind.None, result.Kind);
    }
}
