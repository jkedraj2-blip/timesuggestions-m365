using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TimeSuggestions.Migrations
{
    /// <inheritdoc />
    public partial class AddIntegrityConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TimeEntries_Cases_CaseId",
                table: "TimeEntries");

            migrationBuilder.DropIndex(
                name: "IX_TimeEntries_SuggestionId",
                table: "TimeEntries");

            migrationBuilder.CreateIndex(
                name: "IX_TimeEntries_SuggestionId",
                table: "TimeEntries",
                column: "SuggestionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Cases_CaseNumber",
                table: "Cases",
                column: "CaseNumber",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_TimeEntries_Cases_CaseId",
                table: "TimeEntries",
                column: "CaseId",
                principalTable: "Cases",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TimeEntries_Cases_CaseId",
                table: "TimeEntries");

            migrationBuilder.DropIndex(
                name: "IX_TimeEntries_SuggestionId",
                table: "TimeEntries");

            migrationBuilder.DropIndex(
                name: "IX_Cases_CaseNumber",
                table: "Cases");

            migrationBuilder.CreateIndex(
                name: "IX_TimeEntries_SuggestionId",
                table: "TimeEntries",
                column: "SuggestionId");

            migrationBuilder.AddForeignKey(
                name: "FK_TimeEntries_Cases_CaseId",
                table: "TimeEntries",
                column: "CaseId",
                principalTable: "Cases",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
