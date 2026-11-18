namespace Orkka.Modules.Tasks.Features.TaskGroups;

/// <summary>
/// A cross-repo group as the group panel renders it.
/// </summary>
/// <param name="Ready">The merge gate's own answer, not a re-derivation — see the endpoint.</param>
/// <param name="BlockedBy">Why the gate is still holding, in words. Null when it isn't.</param>
public sealed record TaskGroupResponse(
    Ulid Id,
    string Title,
    string? Request,
    string? Contract,
    string ContractSource,
    string MergePolicy,
    string Status,
    Ulid? MilestoneId,
    bool Ready,
    string? BlockedBy,
    DateTimeOffset? SettledAt,
    DateTimeOffset CreatedAt,
    List<TaskGroupMemberResponse> Members,
    // Agent-raised asks to add another repository, newest last. Only the open ones are worth a
    // human's attention, but the resolved ones explain why a repo is or isn't in the group.
    List<RepoRequestResponse> RepoRequests,
    // "CrossRepo" or "Story". A Story's shared branch, its ticket, when its pull requests were
    // marked ready, and one row per repository with that repository's pull request.
    string Kind = "CrossRepo",
    string? BranchName = null,
    string? ExternalKey = null,
    string? ExternalUrl = null,
    DateTimeOffset? ReadyAt = null,
    List<TaskGroupBranchResponse>? Branches = null);

/// <summary>One repository's shared branch of a Story and the forge pull request opened from it
/// ("draft" until the last sub-task lands, then "ready"; "merged" / "closed" as the forge says).</summary>
public sealed record TaskGroupBranchResponse(
    Ulid RepoId,
    string? RepoName,
    string BranchName,
    string? PrUrl,
    string PrState,
    DateTimeOffset? ReadyAt);

/// <summary>One repository's half of the work.</summary>
/// <param name="PrClosed">The PR was closed without merging. Distinct from "no PR yet": this half
/// is never going to land on its own, which is why the gate stops reporting the group as ready.
/// </param>
public sealed record TaskGroupMemberResponse(
    Ulid Id,
    int Number,
    string Code,
    string Title,
    Ulid? RepoId,
    string? RepoName,
    string? RepoColor,
    string CurrentLane,
    string Status,
    string? PrUrl,
    bool IsMerged,
    bool PrClosed,
    string? MergeFailureReason,
    // A Story member: its place in the story and its sub-task key.
    int? StoryOrder = null,
    string? ExternalKey = null);

/// <summary>Body of the contract edit. Only the contract is editable — the split decides the
/// membership, and re-pointing a half's repository is refused at the task endpoint.</summary>
public sealed record UpdateTaskGroupRequest(string? Contract);

/// <summary>Rules on one agent-raised repository request.</summary>
public sealed record ResolveRepoRequestRequest(string RepoName, bool Approve);

/// <summary>An agent's ask to add a repository, as the group panel renders it.</summary>
public sealed record RepoRequestResponse(
    string RepoName,
    string Reason,
    string Status,
    DateTimeOffset RequestedAt);
