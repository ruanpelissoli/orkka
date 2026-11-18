namespace Orkka.Modules.Tasks.Contracts;

/// <summary>
/// A milestone was created, edited, auto-transitioned, or deleted. Board and roadmap viewers use
/// it to move progress bars without a refresh. <paramref name="Deleted"/> carries no counts —
/// the client drops the row.
/// </summary>
public sealed record MilestoneChangedEvent(
    Ulid MilestoneId,
    string? Status,
    int Total,
    int Done,
    int InFlight,
    int NeedsAttention,
    int NotStarted,
    bool Deleted,
    DateTimeOffset Timestamp);

public sealed record LaneChangedEvent(
    Ulid TaskId,
    string TaskTitle,
    string FromLane,
    string ToLane,
    string Status,
    DateTimeOffset Timestamp);

public sealed record StatusChangedEvent(
    Ulid TaskId,
    string NewStatus,
    DateTimeOffset Timestamp);

public sealed record NewMessageEvent(
    Ulid TaskId,
    Ulid MessageId,
    string Role,
    string Content,
    string MessageType,
    string? SenderId,
    DateTimeOffset CreatedAt);

/// <summary>
/// Board-scoped preview of the newest agent message on a task (card preview). Content is
/// server-truncated; the full message still flows via NewMessage to task:{taskId}.
/// </summary>
public sealed record TaskLastMessageChangedEvent(
    Ulid TaskId,
    string Role,
    string Preview,
    string MessageType,
    string? AgentName,
    DateTimeOffset CreatedAt)
{
    public const int PreviewMaxLength = 200;

    /// <summary>Collapses whitespace/newlines and truncates to PreviewMaxLength with an ellipsis.</summary>
    public static string BuildPreview(string content)
    {
        var collapsed = System.Text.RegularExpressions.Regex.Replace(content, @"\s+", " ").Trim();
        return collapsed.Length <= PreviewMaxLength
            ? collapsed
            : collapsed[..PreviewMaxLength].TrimEnd() + "…";
    }
}

public sealed record TaskCreatedEvent(
    Ulid TaskId,
    int Number,
    string Code,
    string Title,
    string Priority,
    string Type,
    string CurrentLane,
    string Status,
    // Null for a tracker task whose repository is still being asked for.
    Ulid? RepoId,
    string? RepoName,
    string? RepoColor,
    List<string>? Tags,
    DateTimeOffset CreatedAt);

public sealed record TaskDeletedEvent(
    Ulid TaskId,
    DateTimeOffset Timestamp);

public sealed record TaskUpdatedEvent(
    Ulid TaskId,
    string? Title,
    string? Priority,
    string? Status,
    string? CurrentLane,
    bool? IsArchived,
    DateTimeOffset Timestamp);

/// <summary>Lightweight reference to a task that blocks another (unmet dependency).</summary>
public sealed record BlockingTaskRef(
    Ulid TaskId,
    string Code,
    string Status,
    bool IsMerged);

/// <summary>
/// A task's dependency set changed (AI analysis, manual add/remove, or a blocker completed).
/// Carries the resulting set of unmet blockers — clients replace, never merge.
/// </summary>
public sealed record TaskDependenciesChangedEvent(
    Ulid TaskId,
    List<BlockingTaskRef> BlockedBy,
    DateTimeOffset Timestamp);

/// <summary>Merge attempt outcome for a task's PR. Reason=null means a prior failure cleared.</summary>
public sealed record TaskMergeFailedEvent(
    Ulid TaskId,
    string? Reason,
    DateTimeOffset Timestamp);

public sealed record PlanReviewedEvent(
    Ulid TaskId,
    Ulid PlanId,
    string Decision,
    string? Feedback,
    DateTimeOffset Timestamp);

public sealed record CodeReviewedEvent(
    Ulid TaskId,
    string Decision,
    string? Body,
    DateTimeOffset Timestamp);

/// <summary>One file in a <see cref="WorkspaceChangedEvent"/>: repo-relative path, git
/// status word (added/modified/removed/renamed), line counts.</summary>
public sealed record WorkspaceChangedFile(string Path, string Status, int Additions, int Deletions);

/// <summary>
/// The worker re-diffed a running task's working tree and stored it as the task's live
/// code-change snapshot. Pushed to task:{taskId} so the Code View can refresh its file tree
/// without polling; the diff itself is fetched by <paramref name="SnapshotId"/> — a full
/// patch does not belong on a broadcast. <paramref name="Revision"/> increases per capture,
/// so a late or reordered event can be ignored.
/// </summary>
public sealed record WorkspaceChangedEvent(
    Ulid TaskId,
    Ulid SnapshotId,
    long Revision,
    string BranchName,
    string BaseBranch,
    int FileCount,
    int Additions,
    int Deletions,
    bool IsTruncated,
    IReadOnlyList<WorkspaceChangedFile> Files,
    DateTimeOffset Timestamp);

/// <summary>
/// A persisted in-app notification pushed to the recipient's user:{userId} group. Carries
/// everything the bell needs to render and deep-link (/p/{projectSlug}/tasks/{taskNumber}
/// for task notifications, /p/{projectSlug}/team?agent={agentId} for agent-chat ones)
/// without a refetch. Type is the NotificationType enum name (PascalCase).
/// </summary>
public sealed record NotificationCreatedEvent(
    Ulid Id,
    string UserId,
    Ulid? TaskId,
    string Type,
    string Title,
    string? Body,
    Ulid? ProjectId,
    string? ProjectSlug,
    int? TaskNumber,
    DateTimeOffset CreatedAt,
    Ulid? AgentId = null,
    Ulid? ConversationId = null);

/// <summary>
/// An agent-chat message was persisted — pushed to the conversation's agentchat:{conversationId}
/// group so an open chat panel updates live even when it is not the SSE observer (detached
/// runs completing after the panel was closed and reopened). Field shape mirrors the chat
/// API's message DTO; extraction extras (diagrams/tasks/documents) arrive on the next fetch.
/// </summary>
public sealed record AgentChatMessageEvent(
    string Id,
    string ConversationId,
    string Role,
    string Content,
    DateTimeOffset CreatedAt);

/// <summary>
/// An asynchronous milestone-planning job moved: claimed, changed step, finished, or failed.
///
/// Carries no result payload. The proposal is thousands of characters and the client has to be
/// able to pick it up after a refresh anyway, so completion is a signal to GET the job — the
/// same invalidate-and-refetch shape <c>DesignBriefUpdated</c> uses, and for the same reason.
/// </summary>
/// <param name="Step">One of <see cref="MilestonePlanSteps"/>, or null once finished.</param>
/// <param name="FailureCode">One of <see cref="MilestonePlanFailureCodes"/> when
/// <paramref name="Status"/> is Failed, else null.</param>
public sealed record MilestonePlanChangedEvent(
    Ulid JobId,
    Ulid ProjectId,
    Ulid? MilestoneId,
    string Status,
    string? Step,
    string? FailureCode,
    string? ErrorMessage,
    DateTimeOffset? StartedAt,
    DateTimeOffset Timestamp);
