using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TimeSuggestions.Migrations
{
    /// <inheritdoc />
    public partial class AddSyncRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SyncRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RunAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Created = table.Column<int>(type: "INTEGER", nullable: false),
                    SkippedExisting = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncRuns", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SyncRuns");
        }
    }
}
