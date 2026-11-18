using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orkka.Modules.SourceControl.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "git_operations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Topology = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    OrgId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RepoId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TaskId = table.Column<Guid>(type: "TEXT", nullable: true),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    DurationMs = table.Column<int>(type: "INTEGER", nullable: false),
                    Succeeded = table.Column<bool>(type: "INTEGER", nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    LastCommand = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CommandCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_git_operations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "local_pr_comments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PullRequestId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Path = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Line = table.Column<int>(type: "INTEGER", nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: false),
                    Author = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    OrgId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_local_pr_comments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "local_pr_reviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PullRequestId = table.Column<Guid>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: true),
                    Author = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    OrgId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_local_pr_reviews", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "local_pull_requests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<int>(type: "INTEGER", nullable: false),
                    TaskId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RepoId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: true),
                    HeadBranch = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    BaseBranch = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    BaseSha = table.Column<string>(type: "TEXT", nullable: true),
                    Merged = table.Column<bool>(type: "INTEGER", nullable: false),
                    MergedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ForgeUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    ForgeState = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    ForgeCheckedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ClosedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    OrgId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_local_pull_requests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_git_operations_Kind_Topology_StartedAt",
                table: "git_operations",
                columns: new[] { "Kind", "Topology", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_git_operations_StartedAt",
                table: "git_operations",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_git_operations_TaskId_StartedAt",
                table: "git_operations",
                columns: new[] { "TaskId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_local_pr_comments_PullRequestId",
                table: "local_pr_comments",
                column: "PullRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_local_pr_reviews_PullRequestId",
                table: "local_pr_reviews",
                column: "PullRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_local_pull_requests_OrgId_Number",
                table: "local_pull_requests",
                columns: new[] { "OrgId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_local_pull_requests_OrgId_RepoId_HeadBranch",
                table: "local_pull_requests",
                columns: new[] { "OrgId", "RepoId", "HeadBranch" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "git_operations");

            migrationBuilder.DropTable(
                name: "local_pr_comments");

            migrationBuilder.DropTable(
                name: "local_pr_reviews");

            migrationBuilder.DropTable(
                name: "local_pull_requests");
        }
    }
}
