using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orkka.Modules.Runners.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RunnerPresences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    WorkerInstance = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ConnectionId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    MachineName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    DaemonVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    TokenId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConnectedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSeenAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ProvidersJson = table.Column<string>(type: "TEXT", nullable: false),
                    CapabilitiesJson = table.Column<string>(type: "TEXT", nullable: true),
                    GitIdentityJson = table.Column<string>(type: "TEXT", nullable: true),
                    ForgeClisJson = table.Column<string>(type: "TEXT", nullable: true),
                    PendingCommandApprovalsJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    OrgId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunnerPresences", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RunnerProviderSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    MachineName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    DaemonVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ProvidersJson = table.Column<string>(type: "TEXT", nullable: false),
                    CapturedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    OrgId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunnerProviderSnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RunnerTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    LastUsedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastMachineName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    RevokedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    OrgId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunnerTokens", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RunnerPresences_OrgId_UserId",
                table: "RunnerPresences",
                columns: new[] { "OrgId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RunnerProviderSnapshots_OrgId_UserId",
                table: "RunnerProviderSnapshots",
                columns: new[] { "OrgId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RunnerTokens_OrgId_UserId",
                table: "RunnerTokens",
                columns: new[] { "OrgId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_RunnerTokens_TokenHash",
                table: "RunnerTokens",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RunnerPresences");

            migrationBuilder.DropTable(
                name: "RunnerProviderSnapshots");

            migrationBuilder.DropTable(
                name: "RunnerTokens");
        }
    }
}
