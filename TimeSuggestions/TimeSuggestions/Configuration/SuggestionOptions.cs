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

    /// <summary>
    /// Górna granica okna synchronizacji nadpisywanego z żądania — kalendarz jest
    /// pobierany przy każdym syncu jako pełny snapshot okna, więc bez limitu jedno
    /// żądanie mogłoby wymusić przetwarzanie wieloletniej historii.
    /// </summary>
    public const int MaxSyncDaysBack = 90;

    /// <summary>Minimalny czas trwania spotkania w minutach; krótsze są odfiltrowywane (dokładnie ta wartość przechodzi).</summary>
    public int MinimumEventDurationMinutes { get; set; } = 5;

    /// <summary>
    /// Najdłuższa luka, którą wolno jednym ruchem doliczyć do sąsiedniej pozycji.
    /// Osiem godzin, czyli dzień pracy: to prawnik wie, czy w tej dziurze pracował nad
    /// sprawą poza dokumentem, i on ją rozdziela. Dwie godziny odcinały normalne
    /// przerwy w dniu (obiad, rozprawa, dojazd) razem z całym wierszem sąsiada, więc
    /// czasu nie dało się nawet podzielić. Limit został jako zapora przed doliczeniem
    /// całej doby jednym kliknięciem, a nie jako ocena, co jest przerwą.
    /// </summary>
    public int MaxClaimableGapMinutes { get; set; } = 480;

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

    /// <summary>
    /// Przerwa między wersjami pliku, do której (włącznie) sesja trwa nieprzerwanie —
    /// dokładnie 15 minut nadal jest jedną ciągłą sesją.
    /// </summary>
    public int SessionContinuationGapMinutes { get; set; } = 15;

    /// <summary>
    /// Górna granica przerwy (włącznie), przy której wersje nadal należą do jednej sesji,
    /// ale luka jest zapisywana jako wykryta przerwa (przycisk "Odejmij przerwę").
    /// Powyżej tej wartości zaczyna się nowa sesja → nowa sugestia.
    /// </summary>
    public int SessionFlaggedGapMinutes { get; set; } = 30;

    /// <summary>Minimalny czas sesji — sesja z jedną wersją nie może wyjść zerowa.</summary>
    public int MinimumSessionMinutes { get; set; } = 5;

    /// <summary>
    /// Jednostka rozliczeniowa kancelarii: do jej wielokrotności zaokrągla przycisk
    /// „Zaokrąglij" (pół godziny daje wpisy typu 30 min, 1 godz., 1,5 godz.). Wartość
    /// jest w konfiguracji, bo jednostka to ustalenie z klientem, nie stała programu —
    /// kancelarie rozliczają się w kwadransach, pół- albo dziesiątych godziny.
    /// </summary>
    public int BillingIncrementMinutes { get; set; } = 30;
}
