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

    /// <summary>Okno synchronizacji wstecz w dniach.</summary>
    public int SyncDaysBack { get; set; } = 7;
}
