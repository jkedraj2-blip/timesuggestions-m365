namespace TimeSuggestions.Configuration;

/// <summary>
/// Parametry reguł biznesowych sugestii. Wartości pochodzą z sekcji "Suggestions"
/// w appsettings.json — progi nie są zaszyte w kodzie, bo mają być łatwe do zmiany
/// bez rekompilacji (wymóg planu: żadnych magicznych liczb).
/// </summary>
public class SuggestionOptions
{
    public const string SectionName = "Suggestions";

    /// <summary>Górna granica sensownego czasu edycji dokumentu (8 godzin) — walidacja wartości od użytkownika.</summary>
    public const int MaxDocumentDurationMinutes = 480;

    /// <summary>Minimalny czas trwania spotkania w minutach; krótsze są odfiltrowywane (dokładnie ta wartość przechodzi).</summary>
    public int MinimumEventDurationMinutes { get; set; } = 5;

    /// <summary>Domyślny czas trwania sugestii z dokumentu — Graph nie mówi, jak długo trwała edycja, tylko kiedy była.</summary>
    public int DefaultDocumentDurationMinutes { get; set; } = 30;

    /// <summary>
    /// Okno synchronizacji wstecz w dniach. Frontend ma własny odpowiednik
    /// (SYNC_DAYS_BACK w graph-config.ts) i deklaruje go w każdym żądaniu
    /// (calendarSnapshotDaysBack) — destrukcyjna rekonsyliacja kalendarza działa
    /// tylko w przecięciu obu okien, więc zmiana tej wartości bez zmiany frontendu
    /// jest bezpieczna (zawęża kasowanie, nie kasuje niepobranych dni).
    /// </summary>
    public int SyncDaysBack { get; set; } = 7;

    /// <summary>
    /// Strefa czasowa biznesowa (ID IANA; .NET konwertuje przez ICU także na Windows).
    /// Kalendarz przychodzi w czasie lokalnym (Prefer: outlook.timezone), dokumenty w UTC —
    /// wspólna strefa sprowadza EntryDate i StartedAt obu źródeł do tej samej podstawy.
    /// </summary>
    public string BusinessTimeZoneId { get; set; } = "Europe/Warsaw";
}
