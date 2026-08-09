using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TimeSuggestions.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// UWAGA dla PRZYSZŁYCH migracji przebudowujących tabele (rebuild-table):
    /// na SQLite EF generuje je z PRAGMA foreign_keys=0, które nie działa
    /// w transakcji — przerwanie procesu w trakcie może zostawić bazę w stanie
    /// częściowym. Takie migracje wymagają ręcznego sekwencjonowania (osobne kroki,
    /// świadome uruchomienie), a automatyczny start robi wcześniej kopię pliku bazy
    /// (DatabaseMigrator.MigrateWithBackup). Tej już wykonanej migracji nie zmieniamy.
    /// </remarks>
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
