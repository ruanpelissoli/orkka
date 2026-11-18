using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orkka.Modules.Trackers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TrackerBoard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BoardId",
                table: "project_configs",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BoardName",
                table: "project_configs",
                type: "TEXT",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BoardId",
                table: "project_configs");

            migrationBuilder.DropColumn(
                name: "BoardName",
                table: "project_configs");
        }
    }
}
