using TimeSuggestions.Models;
using TimeSuggestions.Services;

namespace TimeSuggestions.Tests;

public class CaseMatcherTests
{
    [Fact]
    public void Match_ZwracaJednoTrafieniePoNazwieKlienta()
    {
        var result = CaseMatcher.Match(
            "Spotkanie z Kowalski w biurze", TestHelpers.CreateTestCases(), MatchTextSource.MeetingTitle);

        Assert.Equal(MatchKind.Single, result.Kind);
        Assert.Equal(1, result.MatchedCase?.Id);
    }

    [Fact]
    public void Match_ZwracaJednoTrafieniePoNumerzeSprawy()
    {
        var result = CaseMatcher.Match(
            "Analiza dokumentów NT-2026-113", TestHelpers.CreateTestCases(), MatchTextSource.MeetingTitle);

        Assert.Equal(MatchKind.Single, result.Kind);
        Assert.Equal(2, result.MatchedCase?.Id);
    }

    [Fact]
    public void Match_ZwracaBrakTrafieniaDlaTekstuBezSprawy()
    {
        var result = CaseMatcher.Match(
            "Cotygodniowe spotkanie zespołu", TestHelpers.CreateTestCases(), MatchTextSource.MeetingTitle);

        Assert.Equal(MatchKind.None, result.Kind);
        Assert.Null(result.MatchedCase);
    }

    [Fact]
    public void Match_ZwracaWieleTrafienDlaWspolnegoSlowaKluczowego()
    {
        // "Beta" jest słowem kluczowym spraw #4 i #5 — wynik musi być niejednoznaczny.
        var result = CaseMatcher.Match(
            "Analiza Beta — przygotowanie", TestHelpers.CreateTestCases(), MatchTextSource.MeetingTitle);

        Assert.Equal(MatchKind.Multiple, result.Kind);
        Assert.Null(result.MatchedCase);
        Assert.Equal(2, result.Candidates.Count);
    }

    [Fact]
    public void Match_DopasowujeNazwePlikuPoNormalizacji()
    {
        // Separator "_", sufiks wersji i rozszerzenie nie mogą przeszkodzić w dopasowaniu.
        var result = CaseMatcher.Match(
            "Umowa_NovaTech_v2.docx", TestHelpers.CreateTestCases(), MatchTextSource.DocumentName);

        Assert.Equal(MatchKind.Single, result.Kind);
        Assert.Equal(2, result.MatchedCase?.Id);
    }

    [Fact]
    public void Match_DopasowujeMimoRoznicyDiakrytykow()
    {
        // Nazwa pliku bez polskich znaków vs sprawa z diakrytykami.
        var result = CaseMatcher.Match(
            "notatki_grzegrzolka.xlsx", TestHelpers.CreateTestCases(), MatchTextSource.DocumentName);

        Assert.Equal(MatchKind.Single, result.Kind);
        Assert.Equal(3, result.MatchedCase?.Id);
    }

    [Fact]
    public void Match_NieDopasowujePodciaguWewnatrzInnegoSlowa()
    {
        // "Alfa" (słowo kluczowe sprawy #4) nie może pasować do "Alfabet" —
        // dopasowanie działa po pełnych tokenach, nie po podciągach.
        var result = CaseMatcher.Match(
            "Alfabet_cwiczenia.docx", TestHelpers.CreateTestCases(), MatchTextSource.DocumentName);

        Assert.Equal(MatchKind.None, result.Kind);
    }

    [Fact]
    public void Match_DopasowujeTerminWielowyrazowyJakoCiagKolejnychTokenow()
    {
        // "Alfa Holding" (nazwa klienta sprawy #4) pasuje jako ciąg pełnych tokenów.
        var result = CaseMatcher.Match(
            "Prezentacja dla Alfa Holding", TestHelpers.CreateTestCases(), MatchTextSource.MeetingTitle);

        Assert.Equal(MatchKind.Single, result.Kind);
        Assert.Equal(4, result.MatchedCase?.Id);
    }

    [Fact]
    public void Match_NieDopasowujeOdmianyFleksyjnej_SwiadomyKompromis()
    {
        // Kompromis dopasowania po pełnych tokenach: "Kowalskiego" ≠ "Kowalski".
        // Odmiany można dodać jako słowa kluczowe sprawy.
        var result = CaseMatcher.Match(
            "Spotkanie u Kowalskiego", TestHelpers.CreateTestCases(), MatchTextSource.MeetingTitle);

        Assert.Equal(MatchKind.None, result.Kind);
    }

    // --- Rozdzielenie trybów normalizacji: terminy spraw nie tracą słów wersji ---

    private static List<Case> CreateVersionWordCases() =>
    [
        new Case { Id = 10, Name = "Raport kwartalny", CaseNumber = "RK-2026-001", ClientName = "Kwartalnik", Keywords = "raport final" },
        new Case { Id = 11, Name = "Przegląd końcowy", CaseNumber = "PF-2026-002", ClientName = "Finisz", Keywords = "final" },
        new Case { Id = 12, Name = "Obsługa Biuro Kopia", CaseNumber = "BK-2026-003", ClientName = "Biuro Kopia" },
    ];

    [Fact]
    public void Match_KeywordZeSlowemWersji_NieDopasowujeTytuluBezTegoSlowa()
    {
        // Sedno defektu: "raport final" nie może zdegradować się do "raport"
        // i automatycznie przypiąć "Raport roboczy" do złej sprawy.
        var result = CaseMatcher.Match(
            "Raport roboczy", CreateVersionWordCases(), MatchTextSource.MeetingTitle);

        Assert.Equal(MatchKind.None, result.Kind);
    }

    [Fact]
    public void Match_KeywordZeSlowemWersji_DopasowujeTytulZawierajacyCalyTermin()
    {
        // Bez sprawy #11 ("final") — do "Raport final Q3" pasowałaby też ona,
        // a ten test sprawdza dopasowanie pełnego terminu wielowyrazowego.
        var result = CaseMatcher.Match(
            "Raport final Q3",
            CreateVersionWordCases().Where(candidate => candidate.Id != 11).ToList(),
            MatchTextSource.MeetingTitle);

        Assert.Equal(MatchKind.Single, result.Kind);
        Assert.Equal(10, result.MatchedCase?.Id);
    }

    [Fact]
    public void Match_KeywordZeSlowemWersji_DopasowujeNazwePliku()
    {
        // Wymaganie: keyword "raport final" trafia w "raport-final.docx" — obie strony
        // spotykają się jako "raport final" (nic nie jest obcinane). Bez sprawy #11
        // ("final"), która do tego pliku pasuje również.
        var result = CaseMatcher.Match(
            "raport-final.docx",
            CreateVersionWordCases().Where(candidate => candidate.Id != 11).ToList(),
            MatchTextSource.DocumentName);

        Assert.Equal(MatchKind.Single, result.Kind);
        Assert.Equal(10, result.MatchedCase?.Id);
    }

    [Fact]
    public void Match_JednowyrazowyKeywordFinal_DopasowujeNazwePliku()
    {
        // Wymaganie: keyword "final" NIE degraduje się do pustego terminu — pasuje
        // do pliku, którego nazwa zawiera token "final".
        var result = CaseMatcher.Match(
            "raport-final.docx", [CreateVersionWordCases()[1]], MatchTextSource.DocumentName);

        Assert.Equal(MatchKind.Single, result.Kind);
        Assert.Equal(11, result.MatchedCase?.Id);
    }

    [Fact]
    public void Match_KeywordZeSlowemWersji_NieDopasowujeNazwyPlikuBezTegoSlowa()
    {
        // Sedno defektu (wariant plikowy): "raport final" nie może zdegradować się
        // do "raport" i automatycznie przypiąć "Raport roboczy.docx" do złej sprawy.
        var result = CaseMatcher.Match(
            "Raport roboczy.docx", CreateVersionWordCases(), MatchTextSource.DocumentName);

        Assert.Equal(MatchKind.None, result.Kind);
    }

    [Fact]
    public void Match_NazwaKlientaZeSlowemKopia_NieLapiePlikuZSamymPierwszymSlowem()
    {
        // Klient "Biuro Kopia" nie może zostać ucięty do "biuro" i łapać
        // dowolnego pliku ze słowem "biuro".
        var result = CaseMatcher.Match(
            "Biuro-podrozne-x.docx", CreateVersionWordCases(), MatchTextSource.DocumentName);

        Assert.Equal(MatchKind.None, result.Kind);
    }

    [Fact]
    public void Match_TerminBezSlowaWersji_DopasowujeMimoKoncowegoFinalWNazwiePliku()
    {
        // Końcowe "final" w nazwie pliku nie przeszkadza dopasowaniu ciągu tokenów —
        // usuwanie słów wersji nie jest do tego potrzebne.
        var cases = new List<Case>
        {
            new() { Id = 13, Name = "Umowa KlientX", CaseNumber = "UK-2026-004", ClientName = "KlientX", Keywords = "umowa klientx" },
        };

        var result = CaseMatcher.Match("Umowa_KlientX_final.docx", cases, MatchTextSource.DocumentName);

        Assert.Equal(MatchKind.Single, result.Kind);
        Assert.Equal(13, result.MatchedCase?.Id);
    }

    [Fact]
    public void Match_JednowyrazowyKeywordFinal_DopasowujeTytulSpotkania()
    {
        // Przed poprawką "final" normalizował się do pustego stringa i nigdy nie pasował.
        var result = CaseMatcher.Match(
            "Final review", [CreateVersionWordCases()[1]], MatchTextSource.MeetingTitle);

        Assert.Equal(MatchKind.Single, result.Kind);
        Assert.Equal(11, result.MatchedCase?.Id);
    }

    [Fact]
    public void Match_NazwaKlientaKonczacaSieNaKopia_NieJestUcinana()
    {
        var result = CaseMatcher.Match(
            "Spotkanie: Biuro Kopia — audyt", CreateVersionWordCases(), MatchTextSource.MeetingTitle);

        Assert.Equal(MatchKind.Single, result.Kind);
        Assert.Equal(12, result.MatchedCase?.Id);
    }

    // --- Regresja: dopasowanie numerów spraw i keywordów liczbowych bez zmian ---

    [Fact]
    public void Match_NumerSprawyNieDopasowujeSieDoDluzszegoNumeru()
    {
        // NT-2026-11 nie może pasować do NT-2026-113 — token "11" ≠ "113".
        var cases = new List<Case>
        {
            new() { Id = 20, Name = "Nowa sprawa", CaseNumber = "NT-2026-11", ClientName = "NowaTech" },
        };

        var result = CaseMatcher.Match("Analiza dokumentów NT-2026-113", cases, MatchTextSource.MeetingTitle);

        Assert.Equal(MatchKind.None, result.Kind);
    }

    [Fact]
    public void Match_KeywordLiczbowyPasujePoPelnymTokenie()
    {
        var cases = new List<Case>
        {
            new() { Id = 21, Name = "Projekt 2026", CaseNumber = "P-2026-005", ClientName = "Projektowo", Keywords = "113" },
        };

        var result = CaseMatcher.Match("Analiza NT-2026-113", cases, MatchTextSource.MeetingTitle);

        Assert.Equal(MatchKind.Single, result.Kind);
        Assert.Equal(21, result.MatchedCase?.Id);
    }
}
