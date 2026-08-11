namespace TimeSuggestions.Models;

/// <summary>
/// Rodzaj korekty czasu wpisu. Nowe wartości dopisujemy WYŁĄCZNIE na końcu —
/// EF zapisuje enum do bazy jako liczbę, więc wstawienie w środku przenumerowałoby
/// istniejące wiersze (ten sam zapis co przy SuggestionStatus).
/// </summary>
public enum AdjustmentKind
{
    /// <summary>Szybka korekta ±N minut przyciskami w UI.</summary>
    QuickAdjustment,

    /// <summary>Odjęcie wykrytej przerwy (luka 15–30 min wewnątrz sesji) — z zakresem od–do.</summary>
    DetectedGapSubtraction,

    /// <summary>Doliczenie wolnej luki między pozycjami (scalanie „z przerwami" albo claim-gap) — z zakresem od–do.</summary>
    GapAddition,

    /// <summary>Dokładna edycja czasu przez ścieżkę approve/edit.</summary>
    ManualEdit,
}
