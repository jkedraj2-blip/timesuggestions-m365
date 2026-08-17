using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TimeSuggestions.Migrations
{
    /// <inheritdoc />
    public partial class AddSuggestionSessionAnchor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Suggestions_Source_ExternalId_EntryDate",
                table: "Suggestions");

            migrationBuilder.AddColumn<DateTime>(
                name: "SessionAnchor",
                table: "Suggestions",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            // Backfill kotwicy dla istniejących wierszy — dokładnie te wartości, które
            // wyliczyłby SuggestionBuilder: dokument (Source=1) bez historii wersji →
            // początek dnia biznesowego (odpowiednik dawnego klucza po EntryDate);
            // kalendarz (Source=0) → lokalny początek spotkania (StartedAt). Format
            // 'YYYY-MM-DD HH:MM:SS' zgodny z zapisem Microsoft.Data.Sqlite, żeby
            // indeks unikalny porównywał identyczne reprezentacje tekstowe.
            migrationBuilder.Sql(
                """
                UPDATE Suggestions
                SET SessionAnchor = CASE
                    WHEN Source = 1 THEN EntryDate || ' 00:00:00'
                    ELSE StartedAt
                END;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Suggestions_Source_ExternalId_SessionAnchor",
                table: "Suggestions",
                columns: new[] { "Source", "ExternalId", "SessionAnchor" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Suggestions_Source_ExternalId_SessionAnchor",
                table: "Suggestions");

            migrationBuilder.DropColumn(
                name: "SessionAnchor",
                table: "Suggestions");

            migrationBuilder.CreateIndex(
                name: "IX_Suggestions_Source_ExternalId_EntryDate",
                table: "Suggestions",
                columns: new[] { "Source", "ExternalId", "EntryDate" },
                unique: true);
        }
    }
}
