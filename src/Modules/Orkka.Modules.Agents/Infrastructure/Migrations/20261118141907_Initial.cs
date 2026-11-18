using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orkka.Modules.Agents.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_audit_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TaskId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ExecutionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    EventType = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Actor = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    ActorKind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ActorUserId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Tool = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    MetadataJson = table.Column<string>(type: "TEXT", nullable: true),
                    PreviousHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    EntryHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    OrgId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_audit_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "agent_schedules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CronExpression = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    TimeZoneId = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    FireAt = table.Column<long>(type: "INTEGER", nullable: true),
                    NextRunAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastRunAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastRunStatus = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SkillId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Message = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AutopilotCapUsd = table.Column<decimal>(type: "TEXT", precision: 12, scale: 2, nullable: true),
                    CreatedByUserId = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    RequesterLocale = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    RunCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false),
                    LeaseOwner = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    LeaseExpiresAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    OrgId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_schedules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "agent_skills",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Slug = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsBuiltIn = table.Column<bool>(type: "INTEGER", nullable: false),
                    BuiltInKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CreatedByUserId = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    UpdatedByUserId = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    OrgId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_skills", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ai_models",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ModelId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    SupportsMcp = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    RequiresRunner = table.Column<bool>(type: "INTEGER", nullable: false),
                    ImageProviderKey = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    InputRatePer1M = table.Column<decimal>(type: "TEXT", precision: 12, scale: 4, nullable: true),
                    OutputRatePer1M = table.Column<decimal>(type: "TEXT", precision: 12, scale: 4, nullable: true),
                    RateSource = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false, defaultValue: "Seed"),
                    VendorInputRatePer1M = table.Column<decimal>(type: "TEXT", precision: 12, scale: 4, nullable: true),
                    VendorOutputRatePer1M = table.Column<decimal>(type: "TEXT", precision: 12, scale: 4, nullable: true),
                    CacheReadRatePer1M = table.Column<decimal>(type: "TEXT", precision: 12, scale: 4, nullable: true),
                    CacheWriteRatePer1M = table.Column<decimal>(type: "TEXT", precision: 12, scale: 4, nullable: true),
                    ContextWindow = table.Column<int>(type: "INTEGER", nullable: true),
                    MaxOutputTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    ReleaseDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    VendorStatus = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    VendorListed = table.Column<bool>(type: "INTEGER", nullable: true),
                    LastSyncedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Origin = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false, defaultValue: "Seed"),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    IsBuiltIn = table.Column<bool>(type: "INTEGER", nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_models", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ai_tier_defaults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Tier = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    ModelId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_tier_defaults", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "brand_kits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PaletteJson = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayFont = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    BodyFont = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Tone = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    OrgId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_brand_kits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "chat_attachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    MimeType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    BlobContainer = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    BlobName = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    UploadedByUserId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    OrgId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_attachments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "project_agents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RepoIds = table.Column<string>(type: "TEXT", nullable: false),
                    SkillIds = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Role = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Avatar = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ImageProvider = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    SystemPrompt = table.Column<string>(type: "text", nullable: false),
                    UserInstructions = table.Column<string>(type: "text", nullable: false),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    TotalTokensIn = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalTokensOut = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalCostUsd = table.Column<decimal>(type: "TEXT", precision: 12, scale: 4, nullable: false),
                    TasksCompleted = table.Column<int>(type: "INTEGER", nullable: false),
                    TasksFailed = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    OrgId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_agents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "agent_schedule_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ScheduleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    FinishedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Error = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Trigger = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    OrgId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_schedule_runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_agent_schedule_runs_agent_schedules_ScheduleId",
                        column: x => x.ScheduleId,
                        principalTable: "agent_schedules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "agent_conversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TaskId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ContextSummary = table.Column<string>(type: "TEXT", nullable: true),
                    SummarizedMessageCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ClaudeSessionId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    SessionProvider = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                    ActiveRunStartedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    OrgId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_conversations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_agent_conversations_project_agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "project_agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "design_assets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    MessageId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TaskId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    MimeType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    BlobContainer = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    BlobName = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Prompt = table.Column<string>(type: "TEXT", maxLength: 50000, nullable: true),
                    SourceModel = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ParentAssetId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RootAssetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Feedback = table.Column<string>(type: "TEXT", maxLength: 5000, nullable: true),
                    FolderPath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    ApprovedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    RequestedByUserId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ReviewedByUserId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ReviewedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    OrgId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_design_assets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_design_assets_project_agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "project_agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "project_memories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TaskId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ExecutionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Type = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CreatedByUserId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    DedupeKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    IsPinned = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsArchived = table.Column<bool>(type: "INTEGER", nullable: false),
                    UseCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastUsedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Metadata = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    OrgId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_memories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_project_memories_project_agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "project_agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "agent_messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Role = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    OrgId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_messages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_agent_messages_agent_conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "agent_conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "architecture_diagrams",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MessageId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    DiagramData = table.Column<string>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    FolderPath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    OrgId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_architecture_diagrams", x => x.Id);
                    table.ForeignKey(
                        name: "FK_architecture_diagrams_agent_conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "agent_conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_architecture_diagrams_agent_messages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "agent_messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_architecture_diagrams_project_agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "project_agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "architecture_diagram_versions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DiagramId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    DiagramData = table.Column<string>(type: "TEXT", nullable: false),
                    ChangedByUserId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ChangedByAgentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RequestedByUserId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    SourceConversationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SourceMessageId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    OrgId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_architecture_diagram_versions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_architecture_diagram_versions_architecture_diagrams_DiagramId",
                        column: x => x.DiagramId,
                        principalTable: "architecture_diagrams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agent_audit_events_OrgId_ActorUserId",
                table: "agent_audit_events",
                columns: new[] { "OrgId", "ActorUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_agent_audit_events_OrgId_CreatedAt",
                table: "agent_audit_events",
                columns: new[] { "OrgId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_agent_audit_events_OrgId_EventType",
                table: "agent_audit_events",
                columns: new[] { "OrgId", "EventType" });

            migrationBuilder.CreateIndex(
                name: "IX_agent_audit_events_ProjectId",
                table: "agent_audit_events",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_audit_events_TaskId",
                table: "agent_audit_events",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_conversations_AgentId_CreatedAt",
                table: "agent_conversations",
                columns: new[] { "AgentId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_agent_conversations_OrgId",
                table: "agent_conversations",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_conversations_ProjectId_AgentId",
                table: "agent_conversations",
                columns: new[] { "ProjectId", "AgentId" });

            migrationBuilder.CreateIndex(
                name: "IX_agent_conversations_TaskId_AgentId",
                table: "agent_conversations",
                columns: new[] { "TaskId", "AgentId" },
                unique: true,
                filter: "\"TaskId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_agent_messages_ConversationId_CreatedAt",
                table: "agent_messages",
                columns: new[] { "ConversationId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_agent_messages_OrgId",
                table: "agent_messages",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_schedule_runs_OrgId_ProjectId_StartedAt",
                table: "agent_schedule_runs",
                columns: new[] { "OrgId", "ProjectId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_agent_schedule_runs_ScheduleId_StartedAt",
                table: "agent_schedule_runs",
                columns: new[] { "ScheduleId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_agent_schedules_AgentId_Kind",
                table: "agent_schedules",
                columns: new[] { "AgentId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_agent_schedules_NextRunAt",
                table: "agent_schedules",
                column: "NextRunAt",
                filter: "\"IsEnabled\" = TRUE AND \"NextRunAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_agent_schedules_OrgId_ProjectId",
                table: "agent_schedules",
                columns: new[] { "OrgId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_agent_skills_OrgId_ProjectId_Slug",
                table: "agent_skills",
                columns: new[] { "OrgId", "ProjectId", "Slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agent_skills_ProjectId_BuiltInKey",
                table: "agent_skills",
                columns: new[] { "ProjectId", "BuiltInKey" },
                unique: true,
                filter: "\"BuiltInKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ai_models_Kind_IsEnabled",
                table: "ai_models",
                columns: new[] { "Kind", "IsEnabled" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_models_ModelId",
                table: "ai_models",
                column: "ModelId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ai_tier_defaults_Provider_Tier",
                table: "ai_tier_defaults",
                columns: new[] { "Provider", "Tier" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_architecture_diagram_versions_DiagramId_Version",
                table: "architecture_diagram_versions",
                columns: new[] { "DiagramId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_architecture_diagram_versions_OrgId",
                table: "architecture_diagram_versions",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_architecture_diagrams_AgentId",
                table: "architecture_diagrams",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_architecture_diagrams_ConversationId",
                table: "architecture_diagrams",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_architecture_diagrams_MessageId",
                table: "architecture_diagrams",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_architecture_diagrams_OrgId",
                table: "architecture_diagrams",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_architecture_diagrams_ProjectId_AgentId",
                table: "architecture_diagrams",
                columns: new[] { "ProjectId", "AgentId" });

            migrationBuilder.CreateIndex(
                name: "IX_brand_kits_OrgId",
                table: "brand_kits",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_brand_kits_ProjectId",
                table: "brand_kits",
                column: "ProjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_chat_attachments_OrgId_ProjectId_CreatedAt",
                table: "chat_attachments",
                columns: new[] { "OrgId", "ProjectId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_design_assets_AgentId",
                table: "design_assets",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_design_assets_OrgId_MessageId",
                table: "design_assets",
                columns: new[] { "OrgId", "MessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_design_assets_OrgId_ProjectId_Status",
                table: "design_assets",
                columns: new[] { "OrgId", "ProjectId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_design_assets_OrgId_RootAssetId",
                table: "design_assets",
                columns: new[] { "OrgId", "RootAssetId" });

            migrationBuilder.CreateIndex(
                name: "ix_project_agents_optional_role",
                table: "project_agents",
                columns: new[] { "ProjectId", "Role" },
                unique: true,
                filter: "\"Role\" IN ('GameDev','GameDesigner','DBA','Legal','MobileDev')");

            migrationBuilder.CreateIndex(
                name: "IX_project_agents_OrgId",
                table: "project_agents",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_project_agents_ProjectId_Role",
                table: "project_agents",
                columns: new[] { "ProjectId", "Role" });

            migrationBuilder.CreateIndex(
                name: "IX_project_memories_AgentId",
                table: "project_memories",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_project_memories_OrgId_ProjectId_IsArchived",
                table: "project_memories",
                columns: new[] { "OrgId", "ProjectId", "IsArchived" });

            migrationBuilder.CreateIndex(
                name: "IX_project_memories_ProjectId_DedupeKey",
                table: "project_memories",
                columns: new[] { "ProjectId", "DedupeKey" },
                unique: true,
                filter: "\"DedupeKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_project_memories_TaskId",
                table: "project_memories",
                column: "TaskId");

            // Full-text search over memories (MemorySearch): an FTS5 table with its own copy of
            // the text keyed by the memory's Id (not rowid, which VACUUM may renumber on a table
            // without an INTEGER PRIMARY KEY), kept in step by triggers. Not expressible in the model.
            migrationBuilder.Sql("""
                CREATE VIRTUAL TABLE project_memories_fts USING fts5(
                    "Id" UNINDEXED, "Title", "Content",
                    tokenize = 'porter unicode61 remove_diacritics 2');
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER project_memories_fts_insert AFTER INSERT ON "project_memories" BEGIN
                    INSERT INTO project_memories_fts ("Id", "Title", "Content")
                    VALUES (new."Id", new."Title", new."Content");
                END;
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER project_memories_fts_update AFTER UPDATE OF "Title", "Content" ON "project_memories" BEGIN
                    DELETE FROM project_memories_fts WHERE "Id" = old."Id";
                    INSERT INTO project_memories_fts ("Id", "Title", "Content")
                    VALUES (new."Id", new."Title", new."Content");
                END;
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER project_memories_fts_delete AFTER DELETE ON "project_memories" BEGIN
                    DELETE FROM project_memories_fts WHERE "Id" = old."Id";
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS project_memories_fts_delete;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS project_memories_fts_update;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS project_memories_fts_insert;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS project_memories_fts;");

            migrationBuilder.DropTable(
                name: "agent_audit_events");

            migrationBuilder.DropTable(
                name: "agent_schedule_runs");

            migrationBuilder.DropTable(
                name: "agent_skills");

            migrationBuilder.DropTable(
                name: "ai_models");

            migrationBuilder.DropTable(
                name: "ai_tier_defaults");

            migrationBuilder.DropTable(
                name: "architecture_diagram_versions");

            migrationBuilder.DropTable(
                name: "brand_kits");

            migrationBuilder.DropTable(
                name: "chat_attachments");

            migrationBuilder.DropTable(
                name: "design_assets");

            migrationBuilder.DropTable(
                name: "project_memories");

            migrationBuilder.DropTable(
                name: "agent_schedules");

            migrationBuilder.DropTable(
                name: "architecture_diagrams");

            migrationBuilder.DropTable(
                name: "agent_messages");

            migrationBuilder.DropTable(
                name: "agent_conversations");

            migrationBuilder.DropTable(
                name: "project_agents");
        }
    }
}
