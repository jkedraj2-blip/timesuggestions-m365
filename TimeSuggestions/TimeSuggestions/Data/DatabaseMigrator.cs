using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace TimeSuggestions.Data;

/// <summary>
/// Automatyczna migracja przy starcie z kopią bezpieczeństwa pliku bazy.
/// Migracje SQLite przebudowujące tabele wyłączają klucze obce
/// (PRAGMA foreign_keys=0), co nie działa w transakcji — EF jawnie ostrzega,
/// że przerwanie procesu w trakcie może zostawić bazę w stanie częściowym.
/// Dlatego przy oczekujących migracjach najpierw kopiujemy plik bazy obok
/// (np. timesuggestions.db.bak-20260807171308_AddIntegrityConstraints);
/// procedura odtworzenia z kopii jest opisana w README (sekcja Uruchomienie).
/// </summary>
public static class DatabaseMigrator
{
    public static void MigrateWithBackup(AppDbContext db)
    {
        var pendingMigrations = db.Database.GetPendingMigrations().ToList();
        if (pendingMigrations.Count > 0)
        {
            BackupDatabaseFile(db, pendingMigrations[^1]);
        }

        db.Database.Migrate();
    }

    private static void BackupDatabaseFile(AppDbContext db, string targetMigration)
    {
        var dataSource = new SqliteConnectionStringBuilder(db.Database.GetConnectionString()).DataSource;

        // Baza in-memory albo plik, który jeszcze nie istnieje (pierwsze uruchomienie) —
        // nie ma czego kopiować.
        if (string.IsNullOrEmpty(dataSource) || !File.Exists(dataSource))
        {
            return;
        }

        // Nazwa kopii zawiera docelową migrację; istniejącej kopii nie nadpisujemy —
        // pierwsza kopia sprzed danej migracji jest tą wartościową.
        var backupPath = $"{dataSource}.bak-{targetMigration}";
        if (!File.Exists(backupPath))
        {
            File.Copy(dataSource, backupPath);
        }
    }
}
