namespace Orkka.Modules.Tasks.Contracts;

/// <summary>
/// One sub-task of a tracker Story, as intake hands it to the Tasks module. A sub-task the repo
/// router placed in several repositories becomes one member per repository, kept in order.
/// </summary>
/// <param name="ExternalRef">The sub-task's idempotency key (<c>jira:{site}:{key}</c>); a second
/// repository's member gets <c>{ExternalRef}@{repo}</c>.</param>
/// <param name="RepoIds">Where it is worked, in order. Empty = nobody could tell; the member asks.</param>
public sealed record StorySubTaskSpec(
    string Key,
    string Url,
    string ExternalRef,
    string Title,
    string Description,
    TaskType Type,
    Priority Priority,
    IReadOnlyList<string> Tags,
    IReadOnlyList<Ulid> RepoIds,
    string? Subpath = null);

/// <summary>A tracker Story assigned to the developer with at least one open sub-task.</summary>
/// <param name="BranchName">The shared branch every member commits on, rendered from the
/// source's branch template for the Story's key.</param>
public sealed record TrackerStorySpec(
    Ulid OrgId,
    Ulid ProjectId,
    string OwnerUserId,
    string StoryKey,
    string StoryUrl,
    string Title,
    string Description,
    string BranchName,
    IReadOnlyList<StorySubTaskSpec> SubTasks,
    string? Epic = null);

/// <summary>A member task of a Story. <paramref name="Created"/> = made by this call.</summary>
public sealed record StoryMemberRef(Ulid TaskId, string SubTaskKey, Ulid? RepoId, int StoryOrder, bool Created);

public sealed record TrackerStoryResult(Ulid GroupId, IReadOnlyList<StoryMemberRef> Members, bool Created);

/// <summary>What appending a late sub-task did. <paramref name="WasReady"/>: the Story's pull
/// requests had already been marked ready, so they must go back to draft until the new member
/// lands. <paramref name="Closed"/>: the Story is past taking members (its pull requests merged
/// or closed, or it landed in a folder) — nothing was appended.</summary>
public sealed record StoryAppendResult(IReadOnlyList<StoryMemberRef> Members, bool WasReady, bool Closed);

/// <summary>A Story member as status write-back and the board read it.</summary>
/// <param name="Planning">An agent has picked it up (PM review or planning started).</param>
/// <param name="Developing">Its plan was approved — a Developing run happened.</param>
/// <param name="InReview">It reached Orkka's Code Review lane.</param>
/// <param name="Landed">Passed review and landed on the shared branch.</param>
public sealed record StoryMemberState(
    Ulid TaskId, string? SubTaskKey, int StoryOrder, Ulid? RepoId,
    bool Planning, bool Developing, bool InReview, bool Landed, bool IsArchived,
    int Number = 0, string? Title = null, string? Lane = null, string? Status = null);

/// <summary>One repository's shared branch and its pull request.</summary>
public sealed record StoryBranchState(
    Ulid RepoId, string BranchName, string? ForgePrUrl, bool Draft, DateTimeOffset? ReadyAt, string? ForgeState);

/// <summary>A Story group as a whole.</summary>
/// <param name="ReadyAt">Every pull request was marked ready — the Story and its sub-tasks move to
/// "PR opened" together.</param>
/// <param name="Settled">Landed for good: squash-merged into a linked folder.</param>
public sealed record TrackerGroupState(
    Ulid GroupId,
    TaskGroupKind Kind,
    string? ExternalKey,
    string? BranchName,
    DateTimeOffset? ReadyAt,
    bool Settled,
    int PendingChildCount,
    IReadOnlyList<StoryMemberState> Members,
    IReadOnlyList<StoryBranchState> Branches)
{
    /// <summary>Every member that is not archived landed, and no sub-task is still pending.</summary>
    public bool AllLanded => PendingChildCount == 0 && Members.Where(m => !m.IsArchived).All(m => m.Landed);

    /// <summary>Every published pull request merged (and at least one exists), or the Story
    /// landed in a folder.</summary>
    public bool Merged => Settled
        || (Branches.Any(b => b.ForgePrUrl is not null)
            && Branches.Where(b => b.ForgePrUrl is not null).All(b => b.ForgeState == "merged"));

    /// <summary>A published pull request was merged or closed — a late sub-task can no longer
    /// join the story's pull request.</summary>
    public bool PullRequestFinished => Settled
        || Branches.Any(b => b.ForgePrUrl is not null && b.ForgeState is "merged" or "closed");
}

/// <summary>A Story member's place in its story, for the landing flow and the prompts.</summary>
public sealed record StoryMemberContext(
    Ulid TaskId,
    int Number,
    string Title,
    int StoryOrder,
    Ulid? RepoId,
    TaskStatus Status,
    bool IsMerged,
    bool IsArchived,
    string? ReviewBaseSha,
    string? PlanSummary,
    IReadOnlyList<StoryChangedFile> Files,
    string? ExternalKey = null,
    string? ExternalUrl = null,
    string? RunnerUserId = null);

public sealed record StoryChangedFile(string Path, int Additions, int Deletions);

/// <summary>Everything about a Story that the landing service and the context section need.</summary>
public sealed record StoryContext(
    Ulid GroupId,
    Ulid OrgId,
    Ulid ProjectId,
    string Title,
    string? Description,
    string? StoryKey,
    string? StoryUrl,
    string BranchName,
    DateTimeOffset? ReadyAt,
    bool Settled,
    int PendingChildCount,
    IReadOnlyList<StoryMemberContext> Members,
    IReadOnlyList<StoryBranchState> Branches);
