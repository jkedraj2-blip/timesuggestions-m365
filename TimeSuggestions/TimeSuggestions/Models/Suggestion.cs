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
    /// Kotwica sesji — stabilny między synchronizacjami składnik klucza dedupu
    /// (Source, ExternalId, SessionAnchor), dzięki któremu jeden plik może mieć
    /// wiele sesji (sugestii) tego samego dnia. Wartości:
    /// dokument z historią wersji — czas pierwszej wersji sesji (UTC; sesja rosnąca
    /// "od tyłu" nie zmienia kotwicy, kolejne wersje przesuwają tylko koniec);
    /// dokument bez historii (fallback) — początek dnia biznesowego (odpowiednik
    /// dawnego klucza po EntryDate, więc zachowanie bez wersji się nie zmienia);
    /// kalendarz — lokalny początek spotkania (dedup i tak działa per ExternalId,
    /// kotwica domyka wyłącznie wyścig równoległych synchronizacji).
    /// </summary>
    public DateTime SessionAnchor { get; set; }

    /// <summary>
    /// Data dzienna wyliczona ze StartedAt w strefie biznesowej — oś grupowania,
    /// agregacji i archiwizacji (indeks dedupu przeszedł na SessionAnchor).
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

    /// <summary>
    /// Wpis czasu, do którego trafiła zatwierdzona sugestia. Relacja odwrócona
    /// (klucz obcy po stronie sugestii), bo scalanie sesji dokumentowych łączy
    /// WIELE sugestii w JEDEN wpis — dawny unikalny TimeEntries.SuggestionId
    /// wykluczał tę relację. null = sugestia bez wpisu (oczekująca/odrzucona).
    /// </summary>
    public int? TimeEntryId { get; set; }

    public TimeEntry? TimeEntry { get; set; }

    public DateTime CreatedAt { get; set; }
}
