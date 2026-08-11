using Microsoft.EntityFrameworkCore;
using TimeSuggestions.Models;

namespace TimeSuggestions.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Case> Cases => Set<Case>();

    public DbSet<Suggestion> Suggestions => Set<Suggestion>();

    public DbSet<TimeEntry> TimeEntries => Set<TimeEntry>();

    public DbSet<SyncRun> SyncRuns => Set<SyncRun>();

    public DbSet<DocumentActivity> DocumentActivities => Set<DocumentActivity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Ochrona przed duplikatami: powtórna synchronizacja tego samego obiektu Graph
        // (dla dokumentów — tego samego pliku w tym samym dniu) nie tworzy drugiej sugestii,
        // a odrzucona sugestia nie wraca na listę.
        modelBuilder.Entity<Suggestion>()
            .HasIndex(s => new { s.Source, s.ExternalId, s.EntryDate })
            .IsUnique();

        // Jeden wpis czasu na sugestię — dwa równoległe zatwierdzenia rozstrzyga baza:
        // drugi INSERT kończy się konfliktem unikalności (SQLITE_CONSTRAINT_UNIQUE),
        // który serwis mapuje na 409, a nie duplikatem wpisu.
        modelBuilder.Entity<TimeEntry>()
            .HasIndex(entry => entry.SuggestionId)
            .IsUnique();

        // Dziennik wersji jest append-only: powtórny sync tych samych wersji nie może
        // duplikować faktów, więc klucz naturalny (plik, wersja) domyka indeks unikalny.
        modelBuilder.Entity<DocumentActivity>()
            .HasIndex(activity => new { activity.ExternalId, activity.VersionId })
            .IsUnique();

        // Filtrowanie aktywne/archiwum (ArchivedAt == null / != null) to główna oś
        // odczytu listy wpisów — zwykły indeks wystarcza dla SQLite w prototypie.
        modelBuilder.Entity<TimeEntry>()
            .HasIndex(entry => entry.ArchivedAt);

        // Unikalność numeru sprawy na poziomie bazy — kontroler sprawdza duplikat przed
        // zapisem, ale wyścig check-then-insert domyka dopiero indeks.
        modelBuilder.Entity<Case>()
            .HasIndex(legalCase => legalCase.CaseNumber)
            .IsUnique();

        // Restrict zamiast domyślnej kaskady: usunięcie sprawy nie może po cichu
        // skasować wpisów czasu (dane rozliczeniowe muszą przetrwać).
        modelBuilder.Entity<TimeEntry>()
            .HasOne(entry => entry.Case)
            .WithMany()
            .HasForeignKey(entry => entry.CaseId)
            .OnDelete(DeleteBehavior.Restrict);

        // Fikcyjne sprawy testowe zaprojektowane pod przypadki logiki dopasowania:
        // diakrytyki (Grzegrzółka), wspólne słowo kluczowe "Beta" (niejednoznaczność #4 vs #5).
        modelBuilder.Entity<Case>().HasData(
            new Case { Id = 1, Name = "Kowalski sp. z o.o. — obsługa korporacyjna", CaseNumber = "K-2026-001", ClientName = "Kowalski", Keywords = "", IsActive = true },
            new Case { Id = 2, Name = "NovaTech S.A. — umowa wdrożeniowa", CaseNumber = "NT-2026-113", ClientName = "NovaTech", Keywords = "", IsActive = true },
            new Case { Id = 3, Name = "Spór Grzegrzółka vs Żółć", CaseNumber = "GZ-2026-007", ClientName = "Grzegrzółka", Keywords = "", IsActive = true },
            new Case { Id = 4, Name = "Fuzja Alfa/Beta", CaseNumber = "AB-2026-021", ClientName = "Alfa Holding", Keywords = "Alfa;Beta", IsActive = true },
            new Case { Id = 5, Name = "Beta Logistics — audyt umów", CaseNumber = "BL-2026-030", ClientName = "Beta Logistics", Keywords = "Beta", IsActive = true });
    }
}
