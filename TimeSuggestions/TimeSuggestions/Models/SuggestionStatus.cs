namespace TimeSuggestions.Models;

public enum SuggestionStatus
{
    Pending,
    Approved,
    Rejected,

    // Nowe wartości dopisujemy NA KOŃCU: EF zapisuje enum jako int, więc wstawienie
    // w środku przenumerowałoby istniejące wiersze w bazie.
    /// <summary>
    /// Stan terminalny (bez unarchive): odrzucona sugestia schowana z widoku odrzuconych.
    /// Rekord zostaje w bazie — przy synchronizacji nadal blokuje ponowne utworzenie
    /// sugestii dla tego samego spotkania/pliku.
    /// </summary>
    Archived,
}
