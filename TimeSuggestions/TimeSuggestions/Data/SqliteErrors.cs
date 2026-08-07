using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace TimeSuggestions.Data;

/// <summary>
/// Rozpoznawanie konfliktów unikalności SQLite. Celowo tylko rozszerzony kod 2067
/// (SQLITE_CONSTRAINT_UNIQUE) — ogólny kod 19 obejmuje też naruszenia FK i NOT NULL,
/// których nie wolno mylić z duplikatem.
/// </summary>
public static class SqliteErrors
{
    public const int ConstraintUnique = 2067;

    public static bool IsUniqueConstraintViolation(DbUpdateException exception)
        => exception.InnerException is SqliteException { SqliteExtendedErrorCode: ConstraintUnique };
}
