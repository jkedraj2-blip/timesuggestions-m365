using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TimeSuggestions.Data;

namespace TimeSuggestions.Tests;

/// <summary>
/// Kopia bezpieczeństwa pliku bazy przed automatycznym Migrate() — migracje
/// rebuild-table na SQLite nie są atomowe (PRAGMA foreign_keys=0 poza transakcją),
/// więc przy oczekujących migracjach najpierw powstaje kopia obok pliku bazy.
/// </summary>
public sealed class MigrationBackupTests : IDisposable
{
    private const string InitialMigration = "20260725183628_InitialCreate";

    private readonly string databasePath = Path.Combine(
        Path.GetTempPath(), $"timesuggestions-migration-{Guid.NewGuid():N}.db");

    private AppDbContext CreateContext() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseSqlite($"Data Source={databasePath}")
        .Options);

    public void Dispose()
    {
        // Pula połączeń SQLite trzyma uchwyty do pliku — bez wyczyszczenia
        // usuwanie na Windows potrafi się nie powieść.
        SqliteConnection.ClearAllPools();
        foreach (var file in FindDatabaseFiles())
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // Plik zostanie w katalogu tymczasowym — nieistotne dla wyniku testu.
            }
        }
    }

    private string[] FindDatabaseFiles()
        => Directory.GetFiles(Path.GetTempPath(), $"{Path.GetFileName(databasePath)}*");

    private string[] FindBackups()
        => Directory.GetFiles(Path.GetTempPath(), $"{Path.GetFileName(databasePath)}.bak-*");

    [Fact]
    public void MigrateWithBackup_PrzyOczekujacychMigracjachTworzyKopiePrzedMigracja()
    {
        // Baza zatrzymana na pierwszej migracji — kolejne są "oczekujące",
        // jak u użytkownika aktualizującego aplikację.
        using (var db = CreateContext())
        {
            db.GetService<IMigrator>().Migrate(InitialMigration);
        }

        using (var db = CreateContext())
        {
            DatabaseMigrator.MigrateWithBackup(db);

            Assert.Empty(db.Database.GetPendingMigrations());
        }

        var backupPath = Assert.Single(FindBackups());
        // Nazwa kopii wskazuje docelową (ostatnią oczekującą) migrację.
        Assert.Contains(".bak-", backupPath);
        Assert.True(new FileInfo(backupPath).Length > 0, "Kopia bazy jest pusta.");
    }

    [Fact]
    public void MigrateWithBackup_BezOczekujacychMigracjiNieTworzyKopii()
    {
        using (var db = CreateContext())
        {
            db.Database.Migrate();
        }

        using (var db = CreateContext())
        {
            DatabaseMigrator.MigrateWithBackup(db);
        }

        Assert.Empty(FindBackups());
    }

    [Fact]
    public void MigrateWithBackup_PierwszeUruchomienieBezPlikuBazyNieTworzyKopii()
    {
        // Świeża instalacja: pliku bazy jeszcze nie ma, więc nie ma czego kopiować —
        // wszystkie migracje wykonują się na nowym pliku.
        using (var db = CreateContext())
        {
            DatabaseMigrator.MigrateWithBackup(db);

            Assert.Empty(db.Database.GetPendingMigrations());
        }

        Assert.Empty(FindBackups());
    }
}
