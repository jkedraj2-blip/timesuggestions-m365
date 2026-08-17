using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TimeSuggestions.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentActivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DocumentActivities",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ExternalId = table.Column<string>(type: "TEXT", nullable: false),
                    VersionId = table.Column<string>(type: "TEXT", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    RecordedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentActivities", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentActivities_ExternalId_VersionId",
                table: "DocumentActivities",
                columns: new[] { "ExternalId", "VersionId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DocumentActivities");
        }
    }
}
