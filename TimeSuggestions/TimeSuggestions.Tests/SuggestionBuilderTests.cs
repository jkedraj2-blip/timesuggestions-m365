using TimeSuggestions.Configuration;
using TimeSuggestions.Contracts;
using TimeSuggestions.Models;
using TimeSuggestions.Services;

namespace TimeSuggestions.Tests;

public class SuggestionBuilderTests
{
    private static readonly DateTime Now = new(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WindowStart = Now.AddDays(-7);

    private static SuggestionBuilder CreateBuilder(int minimumSessionMinutes = 5)
        => new(new SuggestionOptions { MinimumSessionMinutes = minimumSessionMinutes });

    private static DriveFileDto CreateFile(string id, string name, DateTime modifiedAt, bool byMe = true)
        => new() { Id = id, Name = name, LastModifiedDateTime = modifiedAt, LastModifiedByMe = byMe };

    [Fact]
    public void BuildFromCalendar_WyliczaCzasTrwaniaZroznicyKoncaIPoczatku()
    {
        var calendarEvent = new CalendarEventDto
        {
            Id = "event-1",
            Subject = "Spotkanie z Kowalski",
            StartDateTime = new DateTime(2026, 7, 24, 10, 0, 0),
            EndDateTime = new DateTime(2026, 7, 24, 11, 30, 0),
        };

        var suggestions = CreateBuilder().BuildFromCalendar([calendarEvent], TestHelpers.CreateTestCases(), Now);

        var suggestion = Assert.Single(suggestions);
        Assert.Equal(90, suggestion.DurationMinutes);
        Assert.Equal(SuggestionSource.Calendar, suggestion.Source);
        Assert.Equal("Spotkanie z Kowalski", suggestion.ProposedDescription);
    }

    /// <summary>
    /// Plik bez historii wersji: żadnego wymyślonego czasu. Graph mówi tylko KIEDY
    /// plik zmieniono, więc sugestia dostaje minimum sesji (wartość z konfiguracji,
    /// celowo inna niż domyślne 5 — test wykryje zahardcodowaną liczbę) i prośbę
    /// o uzupełnienie czasu.
    /// </summary>
    [Fact]
    public void BuildFromDocuments_BezHistoriiDajeMinimumSesjiIProsbeOUzupelnienieCzasu()
    {
        var builder = CreateBuilder(minimumSessionMinutes: 7);
        var file = CreateFile("file-1", "Umowa_NovaTech_v2.docx", Now.AddDays(-1));

        var suggestions = builder.BuildFromDocuments([file], TestHelpers.CreateTestCases(), WindowStart, Now, Now).Suggestions;

        var suggestion = Assert.Single(suggestions);
        Assert.Equal(7, suggestion.DurationMinutes);
        Assert.True(suggestion.NeedsTimeReview);
        Assert.Equal(SuggestionSource.Document, suggestion.Source);
    }

    [Fact]
    public void BuildFromDocuments_AgregujeDwieModyfikacjeTegoSamegoDniaDoJednejSugestii()
    {
        var morningEdit = CreateFile("file-1", "Umowa_NovaTech_v2.docx", new DateTime(2026, 7, 24, 9, 0, 0));
        var afternoonEdit = CreateFile("file-1", "Umowa_NovaTech_v2.docx", new DateTime(2026, 7, 24, 16, 0, 0));

        var suggestions = CreateBuilder().BuildFromDocuments(
            [morningEdit, afternoonEdit], TestHelpers.CreateTestCases(), WindowStart, Now, Now).Suggestions;

        var suggestion = Assert.Single(suggestions);
        // Sugestia dostaje godzinę pierwszej modyfikacji danego dnia,
        // przeliczoną z UTC na strefę biznesową (lipiec = CEST, UTC+2).
        Assert.Equal(new DateTime(2026, 7, 24, 11, 0, 0), suggestion.StartedAt);
    }

    [Fact]
    public void BuildFromDocuments_PrzeliczaCzasUtcNaStrefeBiznesowa()
    {
        // 22:30 UTC 6 sierpnia = 00:30 CEST 7 sierpnia — EntryDate i StartedAt
        // muszą być w strefie biznesowej, nie w UTC.
        var nowUtc = new DateTime(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc);
        var file = CreateFile("file-1", "Umowa_NovaTech.docx", new DateTime(2026, 8, 6, 22, 30, 0, DateTimeKind.Utc));

        var suggestions = CreateBuilder().BuildFromDocuments(
            [file], TestHelpers.CreateTestCases(), nowUtc.AddDays(-7), nowUtc, nowUtc).Suggestions;

        var suggestion = Assert.Single(suggestions);
        Assert.Equal(new DateOnly(2026, 8, 7), suggestion.EntryDate);
        Assert.Equal(new DateTime(2026, 8, 7, 0, 30, 0), suggestion.StartedAt);
    }

    [Fact]
    public void BuildFromDocuments_DwieEdycjeWTymSamymDniuUtcAleRoznychDniachLokalnychDajaDwieSugestie()
    {
        var nowUtc = new DateTime(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc);
        // Obie edycje 6 sierpnia UTC, ale druga to już 7 sierpnia czasu lokalnego.
        var dayEdit = CreateFile("file-1", "Umowa_NovaTech.docx", new DateTime(2026, 8, 6, 10, 0, 0, DateTimeKind.Utc));
        var lateEdit = CreateFile("file-1", "Umowa_NovaTech.docx", new DateTime(2026, 8, 6, 22, 30, 0, DateTimeKind.Utc));

        var suggestions = CreateBuilder().BuildFromDocuments(
            [dayEdit, lateEdit], TestHelpers.CreateTestCases(), nowUtc.AddDays(-7), nowUtc, nowUtc).Suggestions;

        Assert.Equal(2, suggestions.Count);
        Assert.Equal([new DateOnly(2026, 8, 6), new DateOnly(2026, 8, 7)],
            suggestions.Select(suggestion => suggestion.EntryDate).Order().ToList());
    }

    [Fact]
    public void BuildFromDocuments_DwieEdycjeWTymSamymDniuLokalnymPoObuStronachPolnocyUtcDajaJednaSugestie()
    {
        var nowUtc = new DateTime(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc);
        // 22:30 UTC 5 sierpnia = 00:30 lokalnie 6 sierpnia; druga edycja 6 sierpnia w południe —
        // ten sam dzień lokalny mimo różnych dni UTC.
        var nightEdit = CreateFile("file-1", "Umowa_NovaTech.docx", new DateTime(2026, 8, 5, 22, 30, 0, DateTimeKind.Utc));
        var dayEdit = CreateFile("file-1", "Umowa_NovaTech.docx", new DateTime(2026, 8, 6, 10, 0, 0, DateTimeKind.Utc));

        var suggestions = CreateBuilder().BuildFromDocuments(
            [nightEdit, dayEdit], TestHelpers.CreateTestCases(), nowUtc.AddDays(-7), nowUtc, nowUtc).Suggestions;

        var suggestion = Assert.Single(suggestions);
        Assert.Equal(new DateOnly(2026, 8, 6), suggestion.EntryDate);
    }

    [Fact]
    public void BuildFromDocuments_TworzyOsobneSugestieDlaModyfikacjiWRozneDni()
    {
        var wednesdayEdit = CreateFile("file-1", "Umowa_NovaTech_v2.docx", new DateTime(2026, 7, 22, 9, 0, 0));
        var thursdayEdit = CreateFile("file-1", "Umowa_NovaTech_v2.docx", new DateTime(2026, 7, 23, 9, 0, 0));

        var suggestions = CreateBuilder().BuildFromDocuments(
            [wednesdayEdit, thursdayEdit], TestHelpers.CreateTestCases(), WindowStart, Now, Now).Suggestions;

        Assert.Equal(2, suggestions.Count);
    }

    [Fact]
    public void BuildFromDocuments_OdrzucaPlikiInneNizWordExcel()
    {
        var image = CreateFile("file-1", "zdjecie.png", Now.AddDays(-1));

        var suggestions = CreateBuilder().BuildFromDocuments([image], TestHelpers.CreateTestCases(), WindowStart, Now, Now).Suggestions;

        Assert.Empty(suggestions);
    }

    [Fact]
    public void BuildFromDocuments_OdrzucaPlikiZmodyfikowanePozaOknemCzasu()
    {
        var oldFile = CreateFile("file-1", "Umowa_NovaTech.docx", Now.AddDays(-30));

        var suggestions = CreateBuilder().BuildFromDocuments([oldFile], TestHelpers.CreateTestCases(), WindowStart, Now, Now).Suggestions;

        Assert.Empty(suggestions);
    }

    [Fact]
    public void BuildFromDocuments_OdrzucaPlikiZmodyfikowanePrzezInnaOsobe()
    {
        var foreignFile = CreateFile("file-1", "Umowa_NovaTech.docx", Now.AddDays(-1), byMe: false);

        var suggestions = CreateBuilder().BuildFromDocuments([foreignFile], TestHelpers.CreateTestCases(), WindowStart, Now, Now).Suggestions;

        Assert.Empty(suggestions);
    }

    [Fact]
    public void BuildFromDocuments_LiczyOdrzuceniaIAgregacje()
    {
        var files = new[]
        {
            CreateFile("file-1", "Umowa_NovaTech.docx", Now.AddDays(-1)),                 // przechodzi
            CreateFile("file-1", "Umowa_NovaTech.docx", Now.AddDays(-1).AddHours(3)),     // zagregowane (ten sam dzień)
            CreateFile("file-2", "zdjecie.png", Now.AddDays(-1)),                         // nie-dokument
            CreateFile("file-3", "Stara_umowa.docx", Now.AddDays(-30)),                   // poza oknem: odpada bez licznika
            CreateFile("file-4", "Cudzy_plik.docx", Now.AddDays(-1), byMe: false),        // cudza modyfikacja
        };

        var result = CreateBuilder().BuildFromDocuments(files, TestHelpers.CreateTestCases(), WindowStart, Now, Now);

        Assert.Equal(1, result.NotOfficeDocumentCount);
        Assert.Equal(1, result.NotModifiedByUserCount);
        Assert.Equal(1, result.AggregatedCount);
        Assert.Single(result.Suggestions);
    }

    [Fact]
    public void BuildFromCalendar_OznaczaNiejednoznaczneDopasowanieBezPrzypisaniaSprawy()
    {
        var calendarEvent = new CalendarEventDto
        {
            Id = "event-1",
            Subject = "Analiza Beta",
            StartDateTime = new DateTime(2026, 7, 24, 10, 0, 0),
            EndDateTime = new DateTime(2026, 7, 24, 11, 0, 0),
        };

        var suggestions = CreateBuilder().BuildFromCalendar([calendarEvent], TestHelpers.CreateTestCases(), Now);

        var suggestion = Assert.Single(suggestions);
        Assert.True(suggestion.IsAmbiguous);
        Assert.Null(suggestion.CaseId);
    }
}
