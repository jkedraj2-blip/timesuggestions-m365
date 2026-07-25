namespace TimeSuggestions.Models;

/// <summary>
/// Zapis pojedynczego przebiegu synchronizacji — zasila kafelek
/// "ostatnia synchronizacja" i daje historię do pokazania na demo.
/// </summary>
public class SyncRun
{
    public int Id { get; set; }

    public DateTime RunAt { get; set; }

    public int Created { get; set; }

    public int SkippedExisting { get; set; }
}
