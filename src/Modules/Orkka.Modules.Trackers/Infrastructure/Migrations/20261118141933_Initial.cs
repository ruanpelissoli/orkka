using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orkka.Modules.Trackers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "connections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ConnectionKey = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    SiteUrl = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    AccountId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Healthy = table.Column<bool>(type: "INTEGER", nullable: false),
                    HealthDetail = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    MachineName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CheckedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastSeenAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    OrgId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_connections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "project_configs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    TrackerConnectionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Scope = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ExtraClause = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    IncludeDescendants = table.Column<bool>(type: "INTEGER", nullable: false),
                    AutoSubmit = table.Column<bool>(type: "INTEGER", nullable: false),
                    PollIntervalSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    StatusMapJson = table.Column<string>(type: "TEXT", nullable: false),
                    BranchTemplate = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastPolledAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastPollError = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    LeaseHolder = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    LeaseExpiresAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ScopeMode = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false, defaultValue: "All"),
                    IntakeStatusesJson = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "[\"To Do\"]"),
                    StoryMode = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 1),
                    DefaultRepoId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    OrgId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_configs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ticket_links",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConfigId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TaskId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    SiteUrl = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    TicketKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TicketId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TicketUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    EpicKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    RepoId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ParentKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Role = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false, defaultValue: "Standalone"),
                    GroupId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ChildKeysJson = table.Column<string>(type: "TEXT", nullable: true),
                    ParentTitle = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ParentDescription = table.Column<string>(type: "TEXT", maxLength: 20000, nullable: true),
                    TrackingEndedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    SyncedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Subpath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    LastCommentAt = table.Column<long>(type: "INTEGER", nullable: true),
                    AssignedToOwner = table.Column<bool>(type: "INTEGER", nullable: false),
                    RemoteStatus = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    AttachmentsJson = table.Column<string>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<long>(type: "INTEGER", nullable: false),
                    SyncedPhase = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    PendingPhase = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    PendingSince = table.Column<long>(type: "INTEGER", nullable: true),
                    TransitionAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LastTransitionError = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    PrState = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    PrCheckedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    OrgId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ticket_links", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "epic_mappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConfigId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false, defaultValue: "Epic"),
                    EpicKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    EpicTitle = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    RepoId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Subpath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    OrgId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_epic_mappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_epic_mappings_project_configs_ConfigId",
                        column: x => x.ConfigId,
                        principalTable: "project_configs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_connections_OrgId_UserId_ConnectionKey",
                table: "connections",
                columns: new[] { "OrgId", "UserId", "ConnectionKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_epic_mappings_ConfigId_Kind_EpicKey",
                table: "epic_mappings",
                columns: new[] { "ConfigId", "Kind", "EpicKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_epic_mappings_RepoId",
                table: "epic_mappings",
                column: "RepoId");

            migrationBuilder.CreateIndex(
                name: "IX_project_configs_ProjectId",
                table: "project_configs",
                column: "ProjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_project_configs_TrackerConnectionId",
                table: "project_configs",
                column: "TrackerConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_ticket_links_ConfigId_TicketKey",
                table: "ticket_links",
                columns: new[] { "ConfigId", "TicketKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ticket_links_GroupId",
                table: "ticket_links",
                column: "GroupId",
                filter: "\"GroupId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ticket_links_TaskId",
                table: "ticket_links",
                column: "TaskId",
                unique: true,
                filter: "\"Role\" <> 'Story'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "connections");

            migrationBuilder.DropTable(
                name: "epic_mappings");

            migrationBuilder.DropTable(
                name: "ticket_links");

            migrationBuilder.DropTable(
                name: "project_configs");
        }
    }
}
