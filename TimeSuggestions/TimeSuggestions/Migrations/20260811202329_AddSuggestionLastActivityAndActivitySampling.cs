using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TimeSuggestions.Migrations
{
    /// <inheritdoc />
    public partial class AddSuggestionLastActivityAndActivitySampling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DocumentActivities_ExternalId_VersionId",
                table: "DocumentActivities");

            migrationBuilder.AddColumn<bool>(
                name: "IsUserAdjusted",
                table: "Suggestions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastActivityAt",
                table: "Suggestions",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            // Zastane sugestie nie mają skąd wziąć prawdziwej ostatniej modyfikacji —
            // najbliższym prawdzie przybliżeniem jest koniec ich własnego przedziału.
            // Bez tego wszystkie miałyby rok 0001 i wpadły na koniec posortowanej listy,
            // a dla rozstrzygniętych zasięg [kotwica, ostatnia modyfikacja] byłby pusty.
            migrationBuilder.Sql(
                "UPDATE Suggestions SET LastActivityAt = " +
                "datetime(SessionAnchor, '+' || DurationMinutes || ' minutes');");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentActivities_ExternalId_VersionId_OccurredAt",
                table: "DocumentActivities",
                columns: new[] { "ExternalId", "VersionId", "OccurredAt" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DocumentActivities_ExternalId_VersionId_OccurredAt",
                table: "DocumentActivities");

            migrationBuilder.DropColumn(
                name: "IsUserAdjusted",
                table: "Suggestions");

            migrationBuilder.DropColumn(
                name: "LastActivityAt",
                table: "Suggestions");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentActivities_ExternalId_VersionId",
                table: "DocumentActivities",
                columns: new[] { "ExternalId", "VersionId" },
                unique: true);
        }
    }
}
