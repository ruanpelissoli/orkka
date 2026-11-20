using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orkka.Modules.Trackers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TrackerTicketLevels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BlockedByKeysJson",
                table: "ticket_links",
                type: "TEXT",
                maxLength: 8000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Level",
                table: "ticket_links",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "Unknown");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BlockedByKeysJson",
                table: "ticket_links");

            migrationBuilder.DropColumn(
                name: "Level",
                table: "ticket_links");
        }
    }
}
