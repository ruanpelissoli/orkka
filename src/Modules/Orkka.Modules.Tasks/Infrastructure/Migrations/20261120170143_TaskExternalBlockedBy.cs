using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orkka.Modules.Tasks.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TaskExternalBlockedBy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalBlockedBy",
                table: "tasks",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExternalBlockedBy",
                table: "tasks");
        }
    }
}
