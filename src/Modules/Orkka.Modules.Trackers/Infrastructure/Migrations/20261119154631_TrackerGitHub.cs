using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orkka.Modules.Trackers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TrackerGitHub : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RepoFilterJson",
                table: "project_configs",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "StatusField",
                table: "project_configs",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AuthMode",
                table: "connections",
                type: "TEXT",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RepoFilterJson",
                table: "project_configs");

            migrationBuilder.DropColumn(
                name: "StatusField",
                table: "project_configs");

            migrationBuilder.DropColumn(
                name: "AuthMode",
                table: "connections");
        }
    }
}
