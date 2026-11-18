using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orkka.Modules.Tasks.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "autopilot_sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StartedByUserId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    StoppedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    StopReason = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    CapUsd = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    AnswerEveryQuestion = table.Column<bool>(type: "INTEGER", nullable: false),
                    SpentUsd = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    TasksSubmitted = table.Column<int>(type: "INTEGER", nullable: false),
                    TasksCompleted = table.Column<int>(type: "INTEGER", nullable: false),
                    TasksFailed = table.Column<int>(type: "INTEGER", nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    OrgId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_autopilot_sessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "milestone_plan_jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MilestoneId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RepoId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Goal = table.Column<string>(type: "text", nullable: false),
                    RequesterUserId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    RequesterLocale = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false, defaultValue: "Pending"),
                    CurrentStep = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    ResultJson = table.Column<string>(type: "text", nullable: true),
                    FailureCode = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    OrgId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_milestone_plan_jobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "milestones",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RepoId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedByUserId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    BranchName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    IntegrationMode = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ShipState = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ShipFailureReason = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    ShippedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    SourceDocumentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    OrgId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_milestones", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "task_forecasts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TaskId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Surface = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    CostP25 = table.Column<decimal>(type: "TEXT", precision: 10, scale: 4, nullable: true),
                    CostMedian = table.Column<decimal>(type: "TEXT", precision: 10, scale: 4, nullable: true),
                    CostP75 = table.Column<decimal>(type: "TEXT", precision: 10, scale: 4, nullable: true),
                    AgentSecondsP25 = table.Column<int>(type: "INTEGER", nullable: false),
                    AgentSecondsMedian = table.Column<int>(type: "INTEGER", nullable: false),
                    AgentSecondsP75 = table.Column<int>(type: "INTEGER", nullable: false),
                    AssumptionsJson = table.Column<string>(type: "TEXT", nullable: false),
                    ActualCost = table.Column<decimal>(type: "TEXT", precision: 10, scale: 4, nullable: true),
                    ActualAgentSeconds = table.Column<int>(type: "INTEGER", nullable: true),
                    ActualLanesJson = table.Column<string>(type: "TEXT", nullable: true),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ScoredAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    OrgId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_task_forecasts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "task_groups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MilestoneId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Request = table.Column<string>(type: "TEXT", maxLength: 10000, nullable: true),
                    Contract = table.Column<string>(type: "TEXT", nullable: true),
                    ContractSource = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    SpansRepoIdsJson = table.Column<string>(type: "TEXT", nullable: true),
                    RepoRequestsJson = table.Column<string>(type: "TEXT", nullable: true),
                    MergePolicy = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CreatedByUserId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    SettledAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    BranchName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ExternalKey = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ExternalUrl = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    ReadyAt = table.Column<long>(type: "INTEGER", nullable: true),
                    PendingChildCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    OrgId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_task_groups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "milestone_dependencies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MilestoneId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DependsOnMilestoneId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    OrgId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_milestone_dependencies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_milestone_dependencies_milestones_DependsOnMilestoneId",
                        column: x => x.DependsOnMilestoneId,
                        principalTable: "milestones",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_milestone_dependencies_milestones_MilestoneId",
                        column: x => x.MilestoneId,
                        principalTable: "milestones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "group_branches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    GroupId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RepoId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BranchName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ForgePrUrl = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    ForgePrDraft = table.Column<bool>(type: "INTEGER", nullable: false),
                    ReadyAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ForgeState = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    OrgId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_group_branches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_group_branches_task_groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "task_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RepoId = table.Column<Guid>(type: "TEXT", nullable: true),
                    MilestoneId = table.Column<Guid>(type: "TEXT", nullable: true),
                    GroupId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Number = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 10000, nullable: true),
                    Priority = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CurrentLane = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    IsArchived = table.Column<bool>(type: "INTEGER", nullable: false),
                    ArchivedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedByUserId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    RequesterLocale = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    DependencyAnalysisStartedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    TotalTokensUsed = table.Column<int>(type: "INTEGER", nullable: false),
                    HasAgentWorking = table.Column<bool>(type: "INTEGER", nullable: false),
                    BranchName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    StoryOrder = table.Column<int>(type: "INTEGER", nullable: true),
                    ReviewBaseSha = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    BaseBranch = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    PrUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    PublishedPrUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    QueuePosition = table.Column<int>(type: "INTEGER", nullable: true),
                    Tags = table.Column<string>(type: "TEXT", nullable: true),
                    ExternalRef = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    ExternalKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ExternalUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    ExternalEpic = table.Column<string>(type: "TEXT", maxLength: 600, nullable: true),
                    WorkingSubpath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ExternalSyncError = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ContextFiles = table.Column<string>(type: "TEXT", nullable: true),
                    ContextDocumentIds = table.Column<string>(type: "TEXT", nullable: true),
                    SkipPlanning = table.Column<bool>(type: "INTEGER", nullable: false),
                    SkipPmReview = table.Column<bool>(type: "INTEGER", nullable: false),
                    SkipTesting = table.Column<bool>(type: "INTEGER", nullable: false),
                    SkipCodeReview = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsMerged = table.Column<bool>(type: "INTEGER", nullable: false),
                    MergeFailureReason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    MergeFailureKind = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                    PrClosedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    SetupFailure = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    MergeConflictsJson = table.Column<string>(type: "TEXT", nullable: true),
                    MergeResolutionStrategy = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    MergeResolutionGuidance = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    MergeResolutionStartedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    QueuedMergeStrategy = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    QueuedMergeGuidance = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    ReviewCycleCount = table.Column<int>(type: "INTEGER", nullable: false),
                    QaCycleCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CheckCycleCount = table.Column<int>(type: "INTEGER", nullable: false),
                    BaseSyncCycleCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SpendApprovedUsd = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: true),
                    AutoRecoveryCount = table.Column<int>(type: "INTEGER", nullable: false),
                    QuestionSourceLane = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                    AssignedAgentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Model = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    RecommendedModel = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    CurrentAgentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SourceMessageId = table.Column<Guid>(type: "TEXT", nullable: true),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    RunnerUserId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ApiSurface = table.Column<string>(type: "TEXT", nullable: true),
                    DocSyncStatus = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    DocSyncCompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    DocSyncAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    AutopilotSessionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    IsInteractive = table.Column<bool>(type: "INTEGER", nullable: false),
                    InteractiveSessionStartedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    InteractiveAnsweredThroughAt = table.Column<long>(type: "INTEGER", nullable: true),
                    InteractiveFinishRequestedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    InteractiveSessionEndedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LeaseOwner = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    LeaseExpiresAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ResetRequestedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ResetCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    OrgId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tasks_milestones_MilestoneId",
                        column: x => x.MilestoneId,
                        principalTable: "milestones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_tasks_task_groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "task_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "task_approval_requests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TaskId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExecutionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ToolUseId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Tool = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Command = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: false),
                    ApprovalKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    DecidedByUserId = table.Column<string>(type: "TEXT", nullable: true),
                    DecidedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    OrgId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_task_approval_requests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_task_approval_requests_tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "task_attachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TaskId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FileUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    FileType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    FileSize = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    OrgId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_task_attachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_task_attachments_tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "task_code_change_snapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TaskId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExecutionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RepoId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Lane = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    BranchName = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    BaseBranch = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    IsTruncated = table.Column<bool>(type: "INTEGER", nullable: false),
                    DiffHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    FileCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Additions = table.Column<int>(type: "INTEGER", nullable: false),
                    Deletions = table.Column<int>(type: "INTEGER", nullable: false),
                    FilesJson = table.Column<string>(type: "TEXT", nullable: true),
                    IsLive = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 0L),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    OrgId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_task_code_change_snapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_task_code_change_snapshots_tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "task_dependencies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TaskId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DependsOnTaskId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    OrgId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_task_dependencies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_task_dependencies_tasks_DependsOnTaskId",
                        column: x => x.DependsOnTaskId,
                        principalTable: "tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_task_dependencies_tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "task_executions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TaskId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectAgentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Lane = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    AgentLog = table.Column<string>(type: "TEXT", nullable: true),
                    FilesChanged = table.Column<string>(type: "TEXT", nullable: true),
                    TokensUsed = table.Column<int>(type: "INTEGER", nullable: false),
                    PromptTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    CompletionTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    OrgId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_task_executions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_task_executions_tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "task_messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TaskId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Role = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    MessageType = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    SenderId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Metadata = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    OrgId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_task_messages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_task_messages_tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "task_plans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TaskId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Feedback = table.Column<string>(type: "TEXT", maxLength: 5000, nullable: true),
                    ApprovedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    OrgId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_task_plans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_task_plans_tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_autopilot_sessions_OrgId_ProjectId",
                table: "autopilot_sessions",
                columns: new[] { "OrgId", "ProjectId" },
                unique: true,
                filter: "\"StopReason\" = 'Running'");

            migrationBuilder.CreateIndex(
                name: "IX_autopilot_sessions_ProjectId_StartedAt",
                table: "autopilot_sessions",
                columns: new[] { "ProjectId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_group_branches_GroupId_RepoId",
                table: "group_branches",
                columns: new[] { "GroupId", "RepoId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_group_branches_OrgId",
                table: "group_branches",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_milestone_dependencies_DependsOnMilestoneId",
                table: "milestone_dependencies",
                column: "DependsOnMilestoneId");

            migrationBuilder.CreateIndex(
                name: "IX_milestone_dependencies_MilestoneId_DependsOnMilestoneId",
                table: "milestone_dependencies",
                columns: new[] { "MilestoneId", "DependsOnMilestoneId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_milestone_dependencies_OrgId",
                table: "milestone_dependencies",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_milestone_plan_jobs_OrgId_ProjectId_RequesterUserId_CreatedAt",
                table: "milestone_plan_jobs",
                columns: new[] { "OrgId", "ProjectId", "RequesterUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_milestone_plan_jobs_Status",
                table: "milestone_plan_jobs",
                column: "Status",
                filter: "\"Status\" IN ('Pending', 'Running')");

            migrationBuilder.CreateIndex(
                name: "IX_milestones_OrgId_ProjectId_Status",
                table: "milestones",
                columns: new[] { "OrgId", "ProjectId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_milestones_ProjectId_Position",
                table: "milestones",
                columns: new[] { "ProjectId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_milestones_ShipState",
                table: "milestones",
                column: "ShipState",
                filter: "\"ShipState\" IN ('Pending', 'Shipping')");

            migrationBuilder.CreateIndex(
                name: "IX_task_approval_requests_OrgId",
                table: "task_approval_requests",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_task_approval_requests_TaskId",
                table: "task_approval_requests",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_task_approval_requests_TaskId_ToolUseId",
                table: "task_approval_requests",
                columns: new[] { "TaskId", "ToolUseId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_task_attachments_OrgId",
                table: "task_attachments",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_task_attachments_TaskId",
                table: "task_attachments",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_task_code_change_snapshots_OrgId",
                table: "task_code_change_snapshots",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "ix_task_code_change_snapshots_task_live",
                table: "task_code_change_snapshots",
                columns: new[] { "TaskId", "IsLive" },
                unique: true,
                filter: "\"IsLive\" = true");

            migrationBuilder.CreateIndex(
                name: "IX_task_code_change_snapshots_TaskId",
                table: "task_code_change_snapshots",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_task_dependencies_DependsOnTaskId",
                table: "task_dependencies",
                column: "DependsOnTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_task_dependencies_OrgId",
                table: "task_dependencies",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_task_dependencies_TaskId_DependsOnTaskId",
                table: "task_dependencies",
                columns: new[] { "TaskId", "DependsOnTaskId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_task_executions_OrgId",
                table: "task_executions",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_task_executions_OrgId_Status",
                table: "task_executions",
                columns: new[] { "OrgId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_task_executions_TaskId_StartedAt",
                table: "task_executions",
                columns: new[] { "TaskId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_task_forecasts_OrgId_TaskId",
                table: "task_forecasts",
                columns: new[] { "OrgId", "TaskId" });

            migrationBuilder.CreateIndex(
                name: "IX_task_forecasts_Outcome",
                table: "task_forecasts",
                column: "Outcome",
                filter: "\"Outcome\" = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_task_groups_OrgId_ProjectId_Status",
                table: "task_groups",
                columns: new[] { "OrgId", "ProjectId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_task_groups_Status",
                table: "task_groups",
                column: "Status",
                filter: "\"Status\" IN ('Active', 'AwaitingMerge')");

            migrationBuilder.CreateIndex(
                name: "IX_task_messages_OrgId",
                table: "task_messages",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_task_messages_TaskId_CreatedAt",
                table: "task_messages",
                columns: new[] { "TaskId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_task_plans_OrgId",
                table: "task_plans",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_task_plans_TaskId_Version",
                table: "task_plans",
                columns: new[] { "TaskId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tasks_CurrentLane_DocSyncStatus_CompletedAt",
                table: "tasks",
                columns: new[] { "CurrentLane", "DocSyncStatus", "CompletedAt" },
                filter: "\"DocSyncStatus\" <> 'Completed'");

            migrationBuilder.CreateIndex(
                name: "IX_tasks_CurrentLane_IsMerged",
                table: "tasks",
                columns: new[] { "CurrentLane", "IsMerged" });

            migrationBuilder.CreateIndex(
                name: "IX_tasks_CurrentLane_PrUrl",
                table: "tasks",
                columns: new[] { "CurrentLane", "PrUrl" });

            migrationBuilder.CreateIndex(
                name: "IX_tasks_GroupId",
                table: "tasks",
                column: "GroupId",
                filter: "\"GroupId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_tasks_MilestoneId",
                table: "tasks",
                column: "MilestoneId");

            migrationBuilder.CreateIndex(
                name: "IX_tasks_OrgId",
                table: "tasks",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_tasks_OrgId_MilestoneId",
                table: "tasks",
                columns: new[] { "OrgId", "MilestoneId" });

            migrationBuilder.CreateIndex(
                name: "IX_tasks_OrgId_ProjectId_CurrentLane",
                table: "tasks",
                columns: new[] { "OrgId", "ProjectId", "CurrentLane" });

            migrationBuilder.CreateIndex(
                name: "IX_tasks_OrgId_ProjectId_ExternalRef",
                table: "tasks",
                columns: new[] { "OrgId", "ProjectId", "ExternalRef" },
                unique: true,
                filter: "\"ExternalRef\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_tasks_OrgId_ProjectId_IsArchived_CurrentLane",
                table: "tasks",
                columns: new[] { "OrgId", "ProjectId", "IsArchived", "CurrentLane" });

            migrationBuilder.CreateIndex(
                name: "IX_tasks_OrgId_QueuePosition",
                table: "tasks",
                columns: new[] { "OrgId", "QueuePosition" });

            migrationBuilder.CreateIndex(
                name: "IX_tasks_OrgId_Status_Priority",
                table: "tasks",
                columns: new[] { "OrgId", "Status", "Priority" });

            migrationBuilder.CreateIndex(
                name: "IX_tasks_ProjectId_CreatedAt",
                table: "tasks",
                columns: new[] { "ProjectId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_tasks_ProjectId_Number",
                table: "tasks",
                columns: new[] { "ProjectId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tasks_RepoId",
                table: "tasks",
                column: "RepoId");

            migrationBuilder.CreateIndex(
                name: "IX_tasks_ResetRequestedAt",
                table: "tasks",
                column: "ResetRequestedAt",
                filter: "\"ResetRequestedAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_tasks_SourceMessageId",
                table: "tasks",
                column: "SourceMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_tasks_Status_IsArchived",
                table: "tasks",
                columns: new[] { "Status", "IsArchived" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "autopilot_sessions");

            migrationBuilder.DropTable(
                name: "group_branches");

            migrationBuilder.DropTable(
                name: "milestone_dependencies");

            migrationBuilder.DropTable(
                name: "milestone_plan_jobs");

            migrationBuilder.DropTable(
                name: "task_approval_requests");

            migrationBuilder.DropTable(
                name: "task_attachments");

            migrationBuilder.DropTable(
                name: "task_code_change_snapshots");

            migrationBuilder.DropTable(
                name: "task_dependencies");

            migrationBuilder.DropTable(
                name: "task_executions");

            migrationBuilder.DropTable(
                name: "task_forecasts");

            migrationBuilder.DropTable(
                name: "task_messages");

            migrationBuilder.DropTable(
                name: "task_plans");

            migrationBuilder.DropTable(
                name: "tasks");

            migrationBuilder.DropTable(
                name: "milestones");

            migrationBuilder.DropTable(
                name: "task_groups");
        }
    }
}
