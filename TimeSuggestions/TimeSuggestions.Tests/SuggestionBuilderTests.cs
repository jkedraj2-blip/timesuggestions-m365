using TimeSuggestions.Configuration;
using TimeSuggestions.Contracts;
using TimeSuggestions.Models;
using TimeSuggestions.Services;

namespace TimeSuggestions.Tests;

public class SuggestionBuilderTests
{
    private static readonly DateTime Now = new(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WindowStart = Now.AddDays(-7);

    private static SuggestionBuilder CreateBuilder(int defaultDocumentMinutes = 30)
        => new(new SuggestionOptions { DefaultDocumentDurationMinutes = defaultDocumentMinutes });

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

    [Fact]
    public void BuildFromDocuments_UzywaDomyslnegoCzasuZKonfiguracji()
    {
        // Wartość celowo inna niż domyślne 30 — test wykryje zahardcodowaną liczbę.
        var builder = CreateBuilder(defaultDocumentMinutes: 45);
        var file = CreateFile("file-1", "Umowa_NovaTech_v2.docx", Now.AddDays(-1));

        var suggestions = builder.BuildFromDocuments([file], TestHelpers.CreateTestCases(), WindowStart, Now, Now);

        var suggestion = Assert.Single(suggestions);
        Assert.Equal(45, suggestion.DurationMinutes);
        Assert.Equal(SuggestionSource.Document, suggestion.Source);
    }

    [Fact]
    public void BuildFromDocuments_AgregujeDwieModyfikacjeTegoSamegoDniaDoJednejSugestii()
    {
        var morningEdit = CreateFile("file-1", "Umowa_NovaTech_v2.docx", new DateTime(2026, 7, 24, 9, 0, 0));
        var afternoonEdit = CreateFile("file-1", "Umowa_NovaTech_v2.docx", new DateTime(2026, 7, 24, 16, 0, 0));

        var suggestions = CreateBuilder().BuildFromDocuments(
            [morningEdit, afternoonEdit], TestHelpers.CreateTestCases(), WindowStart, Now, Now);

        var suggestion = Assert.Single(suggestions);
        // Sugestia dostaje godzinę pierwszej modyfikacji danego dnia.
        Assert.Equal(new DateTime(2026, 7, 24, 9, 0, 0), suggestion.StartedAt);
    }

    [Fact]
    public void BuildFromDocuments_TworzyOsobneSugestieDlaModyfikacjiWRozneDni()
    {
        var wednesdayEdit = CreateFile("file-1", "Umowa_NovaTech_v2.docx", new DateTime(2026, 7, 22, 9, 0, 0));
        var thursdayEdit = CreateFile("file-1", "Umowa_NovaTech_v2.docx", new DateTime(2026, 7, 23, 9, 0, 0));

        var suggestions = CreateBuilder().BuildFromDocuments(
            [wednesdayEdit, thursdayEdit], TestHelpers.CreateTestCases(), WindowStart, Now, Now);

        Assert.Equal(2, suggestions.Count);
    }

    [Fact]
    public void BuildFromDocuments_OdrzucaPlikiInneNizWordExcel()
    {
        var image = CreateFile("file-1", "zdjecie.png", Now.AddDays(-1));

        var suggestions = CreateBuilder().BuildFromDocuments([image], TestHelpers.CreateTestCases(), WindowStart, Now, Now);

        Assert.Empty(suggestions);
    }

    [Fact]
    public void BuildFromDocuments_OdrzucaPlikiZmodyfikowanePozaOknemCzasu()
    {
        var oldFile = CreateFile("file-1", "Umowa_NovaTech.docx", Now.AddDays(-30));

        var suggestions = CreateBuilder().BuildFromDocuments([oldFile], TestHelpers.CreateTestCases(), WindowStart, Now, Now);

        Assert.Empty(suggestions);
    }

    [Fact]
    public void BuildFromDocuments_OdrzucaPlikiZmodyfikowanePrzezInnaOsobe()
    {
        var foreignFile = CreateFile("file-1", "Umowa_NovaTech.docx", Now.AddDays(-1), byMe: false);

        var suggestions = CreateBuilder().BuildFromDocuments([foreignFile], TestHelpers.CreateTestCases(), WindowStart, Now, Now);

        Assert.Empty(suggestions);
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
