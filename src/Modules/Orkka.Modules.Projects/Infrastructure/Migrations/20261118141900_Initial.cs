using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orkka.Modules.Projects.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "projects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Slug = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 5, nullable: false),
                    LastTaskNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedByUserId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Settings = table.Column<string>(type: "TEXT", nullable: true),
                    BaseFolder = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    BaseFolderShape = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    OrgId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_projects", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "project_repos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    GitUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    DefaultBranch = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, defaultValue: "main"),
                    WorkspaceStatus = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CardColor = table.Column<string>(type: "TEXT", nullable: false),
                    ForgeKind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CliAccount = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Stack = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    SetupCommand = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    QaRunCommand = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    BuildCommand = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CheckCommand = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    FolderPath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    MissingSince = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    OrgId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_repos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_project_repos_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "project_run_configs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    Fingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false, defaultValue: "Ai"),
                    DetectedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    OrgId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_run_configs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_project_run_configs_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_project_repos_OrgId",
                table: "project_repos",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_project_repos_ProjectId",
                table: "project_repos",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_project_run_configs_OrgId",
                table: "project_run_configs",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_project_run_configs_ProjectId",
                table: "project_run_configs",
                column: "ProjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_projects_OrgId",
                table: "projects",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_projects_OrgId_Key",
                table: "projects",
                columns: new[] { "OrgId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_projects_OrgId_Slug",
                table: "projects",
                columns: new[] { "OrgId", "Slug" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "project_repos");

            migrationBuilder.DropTable(
                name: "project_run_configs");

            migrationBuilder.DropTable(
                name: "projects");
        }
    }
}
