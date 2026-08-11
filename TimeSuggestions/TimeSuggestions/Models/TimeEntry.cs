namespace TimeSuggestions.Models;

/// <summary>Docelowy, rozliczalny wpis czasu pracy — powstaje z zatwierdzonej sugestii.</summary>
public class TimeEntry
{
    public int Id { get; set; }

    public int CaseId { get; set; }

    public Case? Case { get; set; }

    /// <summary>Data dzienna wyliczona ze StartedAt w strefie biznesowej — oś grupowania i archiwizacji.</summary>
    public DateOnly EntryDate { get; set; }

    /// <summary>
    /// Początek i koniec wpisu w czasie strefy biznesowej — ta sama oś co
    /// Suggestion.StartedAt, żeby niezmiennik nakładania ("każda minuta doby należy
    /// do najwyżej jednego wpisu") liczył się na jednej osi bez konwersji.
    /// Świadome odstępstwo od trzymania UTC: doba, o której mówi niezmiennik,
    /// to doba lokalna prawnika, a EntryDate to wprost data ze StartedAt.
    /// </summary>
    public DateTime StartedAt { get; set; }

    public DateTime EndedAt { get; set; }

    /// <summary>
    /// Czas wpisu = suma czasu sesji (EndedAt-StartedAt przy pojedynczej sesji)
    /// + suma korekt (TimeEntryAdjustment). Wartość przeliczana i zapisywana przy
    /// każdej zmianie — korekty są dziennikiem audytowym, nie stanem liczonym w locie.
    /// </summary>
    public int DurationMinutes { get; set; }

    public string Description { get; set; } = string.Empty;

    /// <summary>Informacja audytowa: wpis powstał z automatycznej sugestii, a nie ręcznie.</summary>
    public bool CreatedFromSuggestion { get; set; }

    /// <summary>
    /// Źródło pierwszej sugestii wpisu — przy wpisie scalonym z wielu sugestii
    /// wszystkie składowe i tak są dokumentowe (scalanie międzyźródłowe jest zabronione).
    /// </summary>
    public SuggestionSource Source { get; set; }

    /// <summary>
    /// Sugestie, z których powstał wpis. Zwykle jedna; scalanie sesji dokumentowych
    /// podpina ich więcej (klucz obcy jest po stronie sugestii — Suggestion.TimeEntryId).
    /// </summary>
    public List<Suggestion> Suggestions { get; set; } = [];

    /// <summary>Dziennik korekt prawnika — audyt zmian czasu, nigdy nie kasowany przez sync.</summary>
    public List<TimeEntryAdjustment> Adjustments { get; set; } = [];

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Moment rozliczenia (archiwizacji) w UTC; null = wpis aktywny. Wpis zarchiwizowany
    /// jest tylko do odczytu: nie można go usunąć ani cofnąć zatwierdzenia — rozliczonego
    /// czasu nie wolno po cichu zmieniać (korekta to świadomie osobna, przyszła funkcja).
    /// </summary>
    public DateTime? ArchivedAt { get; set; }
}
