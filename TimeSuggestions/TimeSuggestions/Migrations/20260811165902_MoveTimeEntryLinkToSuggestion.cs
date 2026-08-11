using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TimeSuggestions.Migrations
{
    /// <inheritdoc />
    public partial class MoveTimeEntryLinkToSuggestion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Kolejność inna niż wygenerowana: najpierw nowa kolumna i przepisanie
            // istniejących powiązań w odwrotną stronę, dopiero potem zniknięcie
            // starej kolumny — inaczej dane relacji przepadłyby bezpowrotnie.
            migrationBuilder.AddColumn<int>(
                name: "TimeEntryId",
                table: "Suggestions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE Suggestions
                SET TimeEntryId = (
                    SELECT TimeEntries.Id FROM TimeEntries
                    WHERE TimeEntries.SuggestionId = Suggestions.Id
                );
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_TimeEntries_Suggestions_SuggestionId",
                table: "TimeEntries");

            migrationBuilder.DropIndex(
                name: "IX_TimeEntries_SuggestionId",
                table: "TimeEntries");

            migrationBuilder.DropColumn(
                name: "SuggestionId",
                table: "TimeEntries");

            migrationBuilder.CreateIndex(
                name: "IX_Suggestions_TimeEntryId",
                table: "Suggestions",
                column: "TimeEntryId");

            migrationBuilder.AddForeignKey(
                name: "FK_Suggestions_TimeEntries_TimeEntryId",
                table: "Suggestions",
                column: "TimeEntryId",
                principalTable: "TimeEntries",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Suggestions_TimeEntries_TimeEntryId",
                table: "Suggestions");

            migrationBuilder.DropIndex(
                name: "IX_Suggestions_TimeEntryId",
                table: "Suggestions");

            migrationBuilder.DropColumn(
                name: "TimeEntryId",
                table: "Suggestions");

            migrationBuilder.AddColumn<int>(
                name: "SuggestionId",
                table: "TimeEntries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_TimeEntries_SuggestionId",
                table: "TimeEntries",
                column: "SuggestionId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_TimeEntries_Suggestions_SuggestionId",
                table: "TimeEntries",
                column: "SuggestionId",
                principalTable: "Suggestions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
