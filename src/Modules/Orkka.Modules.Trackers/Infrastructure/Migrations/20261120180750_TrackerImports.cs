using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orkka.Modules.Trackers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TrackerImports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExcludedChildKeysJson",
                table: "ticket_links",
                type: "TEXT",
                maxLength: 20000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Pinned",
                table: "ticket_links",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "tracker_imports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConfigId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TicketKey = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    SubTaskKeysJson = table.Column<string>(type: "TEXT", maxLength: 20000, nullable: false),
                    RequestedByUserId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    RequestedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    TaskId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    OrgId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tracker_imports", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tracker_imports_ConfigId_CompletedAt",
                table: "tracker_imports",
                columns: new[] { "ConfigId", "CompletedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_tracker_imports_ProjectId",
                table: "tracker_imports",
                column: "ProjectId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tracker_imports");

            migrationBuilder.DropColumn(
                name: "ExcludedChildKeysJson",
                table: "ticket_links");

            migrationBuilder.DropColumn(
                name: "Pinned",
                table: "ticket_links");
        }
    }
}
