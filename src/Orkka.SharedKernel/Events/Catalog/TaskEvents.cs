namespace Orkka.SharedKernel.Events.Catalog;

/// <summary>
/// Published when a task's pull request has actually landed in the base branch — by the
/// Tasks module (MarkTaskMergedAsync, in-app merge endpoints). Reactions only (e.g. Orkka
/// Local's auto-push-to-origin); the lane/merge state transition has already been saved
/// when this fires. Handlers must tolerate repeats — idempotent merges republish.
/// </summary>
public sealed record TaskMerged(
    Ulid TaskId,
    Ulid OrgId,
    Ulid ProjectId,
    Ulid? RepoId,
    // Merge commit SHA on the base branch when the merge path knows it (GitHub webhook
    // payload, merge API response); null otherwise — consumers fall back to
    // newest-deployment-after-merge correlation.
    string? MergeCommitSha = null,
    // Whether this merge actually changed the repo's DEFAULT branch.
    //
    // False for a task in a milestone: its PR lands on the milestone's integration branch, and
    // the default branch does not move until the whole milestone ships. Deploy-side consumers
    // key on this — triggering a production build per task would rebuild an unchanged default
    // branch N times, which is exactly the cost milestone branching exists to remove.
    //
    // Defaults to true so any merge path that predates this keeps its old behavior rather than
    // silently going quiet.
    bool LandedOnDefaultBranch = true) : IntegrationEvent;

/// <summary>
/// Published by the Tasks module (TasksDbContext save interception) when a task enters a
/// status that needs a human's attention (plan approval, questions, code/QA review, failure,
/// completion). Reactions only — the transition has already been saved when this fires.
/// Status is carried as the TaskStatus enum name (SharedKernel cannot reference
/// Tasks.Contracts). ActorUserId is the authenticated user who caused the
/// save, or null for agent/worker-driven transitions — handlers use it for self-suppression.
/// </summary>
public sealed record TaskStatusChanged(
    Ulid TaskId,
    Ulid OrgId,
    Ulid ProjectId,
    int TaskNumber,
    string TaskTitle,
    string? CreatedByUserId,
    string NewStatus,
    string? ActorUserId) : IntegrationEvent;

/// <summary>
/// Published by the Tasks module when a merge attempt records a failure reason on a task
/// (MergeFailureReason set to a non-null value; clearing a failure does not publish).
/// </summary>
public sealed record TaskMergeAttemptFailed(
    Ulid TaskId,
    Ulid OrgId,
    Ulid ProjectId,
    int TaskNumber,
    string TaskTitle,
    string? CreatedByUserId,
    string Reason,
    string? ActorUserId) : IntegrationEvent;

/// <summary>
/// Published by the Tasks module when an interactive session pauses on a command that needs a
/// human's ruling. The prompt itself rides the task's live stream (<c>ApprovalRequested</c>);
/// this event exists so the Organizations module can notify the session owner who is NOT
/// watching — a push to their phone is the whole point of an approval prompt.
/// <see cref="ApprovalId"/> lets the notification deep-link straight to the card.
/// </summary>
public sealed record InteractiveApprovalRequested(
    Ulid ApprovalId,
    Ulid TaskId,
    Ulid OrgId,
    Ulid ProjectId,
    int TaskNumber,
    string TaskTitle,
    /// <summary>Who opened the session — the person whose answer the agent is waiting for.</summary>
    string? SessionOwnerUserId,
    string Tool,
    string Command) : IntegrationEvent;

/// <summary>
/// The ruling (or expiry) that released a paused command. Lets the Organizations module clear
/// the matching notification so a badge does not keep pointing at a decision already made.
/// </summary>
public sealed record InteractiveApprovalSettled(
    Ulid ApprovalId,
    Ulid TaskId,
    Ulid OrgId,
    /// <summary>The <c>ApprovalStatus</c> name (Allowed, AllowedForSession, Denied, Expired).</summary>
    string Status) : IntegrationEvent;
