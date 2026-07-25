using Microsoft.EntityFrameworkCore;
using TimeSuggestions.Models;

namespace TimeSuggestions.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Case> Cases => Set<Case>();

    public DbSet<Suggestion> Suggestions => Set<Suggestion>();

    public DbSet<TimeEntry> TimeEntries => Set<TimeEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Ochrona przed duplikatami: powtórna synchronizacja tego samego obiektu Graph
        // (dla dokumentów — tego samego pliku w tym samym dniu) nie tworzy drugiej sugestii,
        // a odrzucona sugestia nie wraca na listę.
        modelBuilder.Entity<Suggestion>()
            .HasIndex(s => new { s.Source, s.ExternalId, s.EntryDate })
            .IsUnique();

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
