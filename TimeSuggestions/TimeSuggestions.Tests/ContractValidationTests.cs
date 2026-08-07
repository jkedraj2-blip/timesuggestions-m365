using System.ComponentModel.DataAnnotations;
using TimeSuggestions.Contracts;

namespace TimeSuggestions.Tests;

/// <summary>
/// Walidacja DTO na granicy API — te same atrybuty, które wykonuje ASP.NET Core
/// przy bindowaniu, uruchamiane wprost przez Validator.
/// </summary>
public class ContractValidationTests
{
    private static List<ValidationResult> Validate(object request)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);
        return results;
    }

    private static CaseWriteRequest CreateCaseRequest() => new()
    {
        Name = "Sprawa testowa",
        CaseNumber = "X-2026-001",
        ClientName = "TestKlient",
    };

    [Theory]
    [InlineData(0)]
    [InlineData(2000)]
    public void ApproveSuggestionRequest_OdrzucaCzasPozaZakresem(int durationMinutes)
    {
        var request = new ApproveSuggestionRequest { DurationMinutes = durationMinutes, Description = "Praca" };

        Assert.NotEmpty(Validate(request));
    }

    [Fact]
    public void ApproveSuggestionRequest_PrzepuszczaPelnaDobe()
    {
        var request = new ApproveSuggestionRequest { DurationMinutes = 1440, Description = "Praca" };

        Assert.Empty(Validate(request));
    }

    [Fact]
    public void ApproveSuggestionRequest_OdrzucaZbytDlugiOpis()
    {
        var request = new ApproveSuggestionRequest { DurationMinutes = 60, Description = new string('x', 501) };

        Assert.NotEmpty(Validate(request));
    }

    [Fact]
    public void CaseWriteRequest_OdrzucaSlowoKluczoweZeSrednikiem()
    {
        // Średnik to separator formatu bazy — jawny błąd zamiast cichej modyfikacji danych.
        var request = CreateCaseRequest();
        request.Keywords = ["alias;zlosliwy"];

        var results = Validate(request);

        var result = Assert.Single(results);
        Assert.Contains("średnika", result.ErrorMessage);
    }

    [Fact]
    public void CaseWriteRequest_OdrzucaZbytDlugieSlowoKluczowe()
    {
        var request = CreateCaseRequest();
        request.Keywords = [new string('x', 101)];

        Assert.NotEmpty(Validate(request));
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("")]
    public void CaseWriteRequest_OdrzucaPolaZSamychBialychZnakow(string blank)
    {
        var request = new CaseWriteRequest { Name = blank, CaseNumber = "X-1", ClientName = "Klient" };

        Assert.NotEmpty(Validate(request));
    }

    [Fact]
    public void CaseWriteRequest_PrzycinaPolaTekstowe()
    {
        var request = new CaseWriteRequest
        {
            Name = "  Sprawa  ",
            CaseNumber = " X-2026-001 ",
            ClientName = "  Klient ",
        };

        Assert.Equal("Sprawa", request.Name);
        Assert.Equal("X-2026-001", request.CaseNumber);
        Assert.Equal("Klient", request.ClientName);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(100_001)]
    public void ClientFilteredCounts_OdrzucaWartosciPozaZakresem(int count)
    {
        var counts = new ClientFilteredCounts { Private = count };

        Assert.NotEmpty(Validate(counts));
    }

    [Fact]
    public void CalendarEventDto_OdrzucaZbytDlugiTytul()
    {
        var dto = new CalendarEventDto { Id = "event-1", Subject = new string('x', 2001) };

        Assert.NotEmpty(Validate(dto));
    }

    [Fact]
    public void DriveFileDto_OdrzucaZbytDlugaNazwe()
    {
        var dto = new DriveFileDto { Id = "file-1", Name = new string('x', 2001) };

        Assert.NotEmpty(Validate(dto));
    }
}
