namespace TimeSuggestions.Models;

/// <summary>Sprawa (projekt klienta), do której dopasowywane są sugestie i przypisywane wpisy czasu.</summary>
public class Case
{
    public int Id { get; set; }

    public required string Name { get; set; }

    public required string CaseNumber { get; set; }

    public required string ClientName { get; set; }

    /// <summary>
    /// Dodatkowe aliasy używane przy dopasowaniu, rozdzielone średnikiem.
    /// Celowo pojedyncza kolumna zamiast osobnej tabeli — prostota wystarcza (YAGNI).
    /// </summary>
    public string Keywords { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
}
