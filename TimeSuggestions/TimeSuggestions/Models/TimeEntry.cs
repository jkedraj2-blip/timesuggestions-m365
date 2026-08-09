namespace TimeSuggestions.Models;

/// <summary>Docelowy, rozliczalny wpis czasu pracy — powstaje z zatwierdzonej sugestii.</summary>
public class TimeEntry
{
    public int Id { get; set; }

    public int CaseId { get; set; }

    public Case? Case { get; set; }

    public DateOnly EntryDate { get; set; }

    public int DurationMinutes { get; set; }

    public string Description { get; set; } = string.Empty;

    /// <summary>Informacja audytowa: wpis powstał z automatycznej sugestii, a nie ręcznie.</summary>
    public bool CreatedFromSuggestion { get; set; }

    public SuggestionSource Source { get; set; }

    public int SuggestionId { get; set; }

    public Suggestion? Suggestion { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Moment rozliczenia (archiwizacji) w UTC; null = wpis aktywny. Wpis zarchiwizowany
    /// jest tylko do odczytu: nie można go usunąć ani cofnąć zatwierdzenia — rozliczonego
    /// czasu nie wolno po cichu zmieniać (korekta to świadomie osobna, przyszła funkcja).
    /// </summary>
    public DateTime? ArchivedAt { get; set; }
}
