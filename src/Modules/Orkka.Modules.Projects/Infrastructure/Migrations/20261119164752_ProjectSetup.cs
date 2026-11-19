using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orkka.Modules.Projects.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ProjectSetup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "SetupCompletedAt",
                table: "projects",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SetupStepsJson",
                table: "projects",
                type: "TEXT",
                maxLength: 500,
                nullable: false,
                defaultValue: "[]");

            // Every project that exists before the guided setup was already being polled: it
            // counts as started (at its creation, same tick encoding), with every step confirmed,
            // so nothing changes for it. Only projects created from here on wait for Start.
            migrationBuilder.Sql(
                """UPDATE "projects" SET "SetupCompletedAt" = "CreatedAt", "SetupStepsJson" = '["folder","machine","source","team"]' WHERE "SetupCompletedAt" IS NULL;""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SetupCompletedAt",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "SetupStepsJson",
                table: "projects");
        }
    }
}
