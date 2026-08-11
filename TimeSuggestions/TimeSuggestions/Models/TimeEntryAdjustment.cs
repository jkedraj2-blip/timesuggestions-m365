namespace TimeSuggestions.Models;

/// <summary>
/// Jedna korekta czasu wpisu — dziennik audytowy, nie stan: DurationMinutes wpisu
/// jest przeliczany i zapisywany przy każdej zmianie, a korekty zostają jako ślad
/// "skąd wzięła się ta liczba". Wpis z jakąkolwiek korektą jest nadrzędny wobec
/// synchronizacji: sync nigdy nie modyfikuje wpisu z korektami ani zarchiwizowanego
/// (sync wolno dotykać wyłącznie sugestii Pending, jak dotychczas).
/// </summary>
public class TimeEntryAdjustment
{
    public int Id { get; set; }

    public int TimeEntryId { get; set; }

    public TimeEntry? TimeEntry { get; set; }

    /// <summary>Zmiana w minutach: dodatnia (doliczenie) albo ujemna (odjęcie).</summary>
    public int Minutes { get; set; }

    public AdjustmentKind Kind { get; set; }

    /// <summary>
    /// Zakres przerwy, której dotyczy korekta (czas strefy biznesowej — ta sama oś
    /// co TimeEntry.StartedAt); null poza korektami przerw. Dla DetectedGapSubtraction
    /// para (od, do) czyni operację idempotentną: druga próba na tę samą lukę → 409.
    /// </summary>
    public DateTime? GapStartAt { get; set; }

    public DateTime? GapEndAt { get; set; }

    public DateTime CreatedAt { get; set; }
}
