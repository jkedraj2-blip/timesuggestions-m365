using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TimeSuggestions.Migrations
{
    /// <inheritdoc />
    public partial class AddTimeEntryHours : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "EndedAt",
                table: "TimeEntries",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "StartedAt",
                table: "TimeEntries",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            // Backfill godzin: początek ze StartedAt najwcześniejszej sugestii wpisu
            // (relacja została już odwrócona w poprzedniej migracji), koniec = początek
            // + zapisany czas trwania. Wpis-sierota bez sugestii (nie powinien istnieć,
            // stary FK był wymagany) dostaje początek dnia z EntryDate zamiast wartości
            // domyślnej 0001-01-01, żeby nie kłamać o dacie.
            migrationBuilder.Sql(
                """
                UPDATE TimeEntries
                SET StartedAt = COALESCE(
                    (
                        SELECT MIN(s.StartedAt) FROM Suggestions s
                        WHERE s.TimeEntryId = TimeEntries.Id
                    ),
                    EntryDate || ' 00:00:00');

                UPDATE TimeEntries
                SET EndedAt = strftime('%Y-%m-%d %H:%M:%S', StartedAt, '+' || DurationMinutes || ' minutes');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EndedAt",
                table: "TimeEntries");

            migrationBuilder.DropColumn(
                name: "StartedAt",
                table: "TimeEntries");
        }
    }
}
