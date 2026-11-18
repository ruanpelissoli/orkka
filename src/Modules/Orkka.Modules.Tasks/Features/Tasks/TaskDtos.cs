namespace Orkka.Modules.Tasks.Features.Tasks;

using Orkka.Modules.Tasks.Contracts;

public sealed record CreateTaskRequest(
    Ulid RepoId,
    string Title,
    string? Description,
    string Priority = "Medium",
    string Type = "Feature",
    List<string>? Tags = null,
    List<string>? ContextFiles = null,
    List<string>? ContextDocumentIds = null,
    bool SkipPlanning = false,
    bool SkipTesting = false,
    bool SkipCodeReview = false,
    // Pin the task to a specific dev agent for Planning/Developing (skips auto-assign).
    Ulid? AssignedAgentId = null,
    // Run the dev agent on this model instead of its own (null/empty = agent's default).
    string? Model = null,
    // Create straight into a milestone (null = the Backlog).
    Ulid? MilestoneId = null,
    // Other repositories this one piece of work also has to change. Empty (the normal case) is
    // an ordinary single-repo task. Non-empty creates a TaskGroup and, at submission, splits the
    // request into one task per repository behind a shared contract.
    //
    // RepoId stays required and keeps its meaning: it is where THIS task's own half lands, and
    // the task keeps its number and URL through the split.
    List<Ulid>? SpansRepoIds = null);

public sealed record UpdateTaskRequest(
    string? Title,
    string? Description,
    string? Priority,
    string? Type,
    Ulid? RepoId,
    List<string>? Tags,
    List<string>? ContextFiles = null,
    List<string>? ContextDocumentIds = null,
    bool? SkipPlanning = null,
    bool? SkipTesting = null,
    bool? SkipCodeReview = null,
    // Tri-state: null = unchanged, "" = clear (back to auto-assign), Ulid = pin to agent.
    string? AssignedAgentId = null,
    // Tri-state: null = unchanged, "" = move to the backlog, Ulid = attach to that milestone.
    string? MilestoneId = null,
    // Tri-state per-task model override: null = unchanged, "" = back to the agent's model,
    // id = pin. Pre-start only; a model the owner's runner has not signed in is said in the
    // task thread (see IRunnerCoverageResolver).
    string? Model = null);

public sealed record TaskResponse(
    Ulid Id,
    Ulid ProjectId,
    int Number,
    string Code,
    Ulid? RepoId,
    string? RepoName,
    string? RepoCardColor,
    // Non-null = this task is one repository's half of a cross-repo task. The detail page uses
    // it to link through to the group, which is the only place the shared contract and the
    // merge gate are visible.
    Ulid? GroupId,
    string Title,
    string? Description,
    string Priority,
    string Type,
    string CurrentLane,
    string Status,
    bool IsArchived,
    string? CreatedByUserId,
    string? BranchName,
    string? PrUrl,
    bool IsMerged,
    string? MergeFailureReason,
    int? QueuePosition,
    List<string>? Tags,
    List<string>? ContextFiles,
    List<string>? ContextDocumentIds,
    int TotalTokensUsed,
    bool HasUnmetDependencies,
    List<TaskDependencyItem> DependsOn,
    List<TaskDependencyItem> Dependents,
    bool SkipPlanning,
    bool SkipTesting,
    bool SkipCodeReview,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? ArchivedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    // Whose runner executes the task.
    string? RunnerUserId = null,
    // Explicitly pinned dev agent (null = auto-assign by role/repo).
    Ulid? AssignedAgentId = null,
    // Per-task model override for the dev agent (null = the agent's own model).
    string? Model = null,
    // Non-null = the repo's setup command failed for this task's worktree, so the app
    // cannot start in it. Surfaced next to the "open in editor" action, where the person
    // about to run it will actually see it.
    string? SetupFailure = null,
    // Milestone this task belongs to (null = the Backlog).
    Ulid? MilestoneId = null,
    // Interactive development: this task is driven by a human in a live session rather than by
    // the queue, and the session marker is non-null only while that session is open.
    bool IsInteractive = false,
    DateTimeOffset? InteractiveSessionStartedAt = null,
    // Non-null between pressing Finish and the worker actually landing the work — the window in
    // which the UI must say "wrapping up" rather than offering Finish again.
    DateTimeOffset? InteractiveFinishRequestedAt = null,
    // Advisory cheaper-model suggestion from the PM review (see TaskEntity.RecommendedModel).
    // The task page renders it as an Apply/Dismiss chip; null = nothing to suggest.
    string? RecommendedModel = null,
    // The forge pull request the developer's runner opened when the work was published.
    string? PublishedPrUrl = null,
    // The tracker ticket this task was imported from (SHOP-123 + its URL); null otherwise.
    string? ExternalKey = null,
    string? ExternalUrl = null,
    // The configured epic the ticket sits under ("SHOP-1 Checkout") — the board filters on it.
    string? ExternalEpic = null,
    // Why the ticket's status could not be moved in its tracker; null when all is well.
    string? ExternalSyncError = null,
    // A Story member's place in its story (GroupId is the story); the Story panel has the rest.
    int? StoryOrder = null);

public sealed record TaskSummaryResponse(
    Ulid Id,
    int Number,
    string Code,
    string Title,
    string Priority,
    string Type,
    string CurrentLane,
    string Status,
    Ulid? RepoId,
    string? RepoName,
    // Named RepoColor, not RepoCardColor, to match what the clients actually read. The board's
    // SignalR event has always sent RepoColor and every card component reads task.repoColor, so
    // while this DTO said RepoCardColor the initial board fetch produced grey cards that turned
    // their real colour only if a live event happened to replace them.
    string? RepoColor,
    // Non-null = one repository's half of a cross-repo task. The card shows a chip so the
    // reason its pull request is sitting unmerged is visible without opening it.
    Ulid? GroupId,
    string? CreatedByUserId,
    int? QueuePosition,
    bool HasUnmetDependencies,
    List<BlockingTaskRef> BlockedBy,
    string? MergeFailureReason,
    DateTimeOffset CreatedAt,
    // Whose runner executes the task.
    string? RunnerUserId = null,
    TaskLastAgentMessageResponse? LastAgentMessage = null,
    // Drives board grouping by milestone (null = the Backlog).
    Ulid? MilestoneId = null,
    // Board cards badge a live interactive session — work a human is doing right now, which the
    // board would otherwise render as an ordinary card sitting in Developing.
    bool IsInteractive = false,
    DateTimeOffset? InteractiveSessionStartedAt = null,
    // Only ever true when the caller asked for archived tasks — the board and the task list
    // both hide them by default. Cards use it to render as archived rather than as live work.
    bool IsArchived = false,
    // The Code View rail: BranchName gates which Pending Questions tasks have code to show,
    // PrUrl short-circuits its baseline fetch, CompletedAt drives the "recently done" window.
    string? BranchName = null,
    string? PrUrl = null,
    DateTimeOffset? CompletedAt = null,
    // The tracker ticket the card came from — the card shows the key as a badge.
    string? ExternalKey = null,
    string? ExternalUrl = null,
    // The configured epic the ticket sits under ("SHOP-1 Checkout") — the board filters on it.
    string? ExternalEpic = null,
    // Why the ticket's status could not be moved in its tracker; null when all is well.
    string? ExternalSyncError = null,
    // A Story member (GroupId is the story): its place in the story, the Story's key and the
    // number of sub-tasks — the card's story chip.
    int? StoryOrder = null,
    string? StoryKey = null,
    int? StoryCount = null);

/// <summary>Newest agent message on a task, truncated for the board card preview.</summary>
public sealed record TaskLastAgentMessageResponse(
    string Preview,
    string MessageType,
    string? AgentName,
    DateTimeOffset CreatedAt);

/// <summary>One edge of a task's dependency graph, seen from the task being viewed.</summary>
public sealed record TaskDependencyItem(
    Ulid TaskId,
    int Number,
    string Code,
    string Title,
    string Status,
    bool IsMerged,
    /// <summary>Both ends are members of the same cross-repo group, so this edge clears at
    /// Completed rather than at merged. Carried on the DTO because the UI renders "blocked by"
    /// from it and would otherwise show a satisfied edge as still blocking.</summary>
    bool SameGroup,
    string? Reason,
    string Source);

public sealed record AddTaskDependencyRequest(
    Ulid DependsOnTaskId,
    string? Reason = null);

public sealed record TaskDependenciesResponse(
    List<TaskDependencyItem> DependsOn,
    List<TaskDependencyItem> Dependents);

public sealed record TaskBoardResponse(
    List<BoardLaneResponse> Lanes);

public sealed record BoardLaneResponse(
    string Key,
    string Label,
    int Order,
    List<TaskSummaryResponse> Tasks);

public sealed record MoveTaskLaneRequest(string TargetLane);


public sealed record TaskFilterRequest
{
    public string? Search { get; init; }
    public Ulid? RepoId { get; init; }
    public string? Status { get; init; }
    public bool? IsArchived { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
}

public sealed record SearchBoardTasksRequest
{
    public string? Q { get; init; }
    public bool IncludeMessages { get; init; }
    public bool IncludeArchived { get; init; }
}
