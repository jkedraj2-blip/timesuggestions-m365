namespace TimeSuggestions.Models;

/// <summary>Propozycja wpisu czasu pracy przed decyzją użytkownika.</summary>
public class Suggestion
{
    public int Id { get; set; }

    public SuggestionSource Source { get; set; }

    /// <summary>Identyfikator obiektu z Microsoft Graph — podstawa ochrony przed duplikatami przy powtórnej synchronizacji.</summary>
    public required string ExternalId { get; set; }

    /// <summary>Tytuł spotkania albo nazwa pliku.</summary>
    public required string Title { get; set; }

    public DateTime StartedAt { get; set; }

    /// <summary>
    /// Data dzienna wyliczona ze StartedAt, zmapowana jako osobna kolumna,
    /// aby indeks unikalny (Source, ExternalId, EntryDate) mógł deduplikować
    /// dokumenty agregowane per dzień.
    /// </summary>
    public DateOnly EntryDate { get; set; }

    public int DurationMinutes { get; set; }

    /// <summary>Dopasowana sprawa; null przy braku dopasowania lub niejednoznaczności.</summary>
    public int? CaseId { get; set; }

    public Case? Case { get; set; }

    /// <summary>True, gdy tekst pasował do więcej niż jednej sprawy — użytkownik musi wybrać ręcznie.</summary>
    public bool IsAmbiguous { get; set; }

    public string ProposedDescription { get; set; } = string.Empty;

    public SuggestionStatus Status { get; set; } = SuggestionStatus.Pending;

    public DateTime CreatedAt { get; set; }
}
