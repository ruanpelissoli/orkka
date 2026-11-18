namespace Orkka.Modules.Tasks.Domain;

using Orkka.Modules.Tasks.Contracts;
using Orkka.SharedKernel.Domain;

/// <summary>
/// A set of tasks that implement one request together, with a contract they share and a joint
/// completion rule.
///
/// It exists because a task is bound to exactly one repo — worktree, branch, PR, diff snapshot
/// and setup command all key on <c>TaskEntity.RepoId</c> — so work that spans repos has to be
/// several tasks. Left to dependency edges alone that work is strictly serial (an edge is unmet
/// until the blocker MERGES) and the halves land in production minutes apart. A group fixes both
/// ends: <see cref="Contract"/> is written before any member runs, so the consumer no longer has
/// to wait for the provider's diff to exist, and <see cref="MergePolicy"/> holds the PRs until
/// they can go together.
///
/// Named for the shape rather than the reason. Cross-repo is the only thing that creates one
/// today, but "a set of tasks with a shared artifact and a joint completion rule" is not
/// repo-specific, and <c>CrossRepoSplit</c> would have to be renamed the first time it isn't.
///
/// Deliberately NOT a parent task. A non-runnable parent row would force every queue, board and
/// merge query to learn the difference between a real task and a placeholder; a sibling set
/// leaves every existing query meaning exactly what it meant before.
/// </summary>
public sealed class TaskGroup : TenantEntity
{
    public Ulid ProjectId { get; set; }

    /// <summary>Inherited from the originating task, so a spanning request planned under a
    /// milestone keeps counting toward it. Null = backlog, same as a task's.</summary>
    public Ulid? MilestoneId { get; set; }

    public required string Title { get; set; }

    /// <summary>The human's original ask, before the split rewrote it into per-repo halves.
    /// Kept because each member's description is only its own half — without this there is no
    /// record of what was actually requested.</summary>
    public string? Request { get; set; }

    /// <summary>
    /// The interface the members code against — endpoints, request/response shapes, schema
    /// changes: whatever crosses the repo boundary. Markdown.
    ///
    /// Written BEFORE any member runs, which is the whole point. <c>TaskEntity.ApiSurface</c>
    /// solves the same problem in the other direction — extracted from the provider's diff after
    /// it finishes — and that ordering is exactly what makes cross-repo work serial. A contract
    /// that exists up front lets both halves run at once, which in turn is what makes an
    /// all-or-nothing merge gate possible instead of deadlocked.
    /// </summary>
    public string? Contract { get; set; }

    public ContractSource ContractSource { get; set; } = ContractSource.Declared;

    /// <summary>
    /// The repositories the human said this work touches, as a JSON array of ULIDs — the input
    /// to the split, not its output.
    ///
    /// Kept after the split has run because it records what was ASKED for, which the resulting
    /// tasks do not: a split that dropped a repo (the model found nothing to do there, or named
    /// it wrongly) is invisible from the members alone. See <see cref="SpannedRepoIds"/>.
    /// </summary>
    public string? SpansRepoIdsJson { get; set; }

    /// <summary>
    /// Repositories an agent asked to add mid-run, as a JSON array (see <see cref="RepoRequests"/>).
    ///
    /// A request rather than an action, and deliberately so. An agent that discovers it also needs
    /// to change a third repository is usually right, but acting on it unilaterally widens the
    /// blast radius and the bill without anyone deciding to — and a wrong guess files work in a
    /// repository nobody wanted touched. Approving one creates the sibling task; declining costs
    /// a line of text.
    /// </summary>
    public string? RepoRequestsJson { get; set; }

    public TaskGroupMergePolicy MergePolicy { get; set; } = TaskGroupMergePolicy.Gated;

    public TaskGroupStatus Status { get; set; } = TaskGroupStatus.Splitting;

    public string? CreatedByUserId { get; set; }

    /// <summary>Set when the last member merged, or when a partial merge stopped part-way.</summary>
    public DateTimeOffset? SettledAt { get; set; }

    /// <summary>What the group is for. <see cref="TaskGroupKind.CrossRepo"/> (0) keeps every
    /// existing row meaning exactly what it meant.</summary>
    public TaskGroupKind Kind { get; set; } = TaskGroupKind.CrossRepo;

    /// <summary>A Story's shared branch — every member's <c>TaskEntity.BranchName</c>, in every
    /// repository the story touches. Null for a cross-repo group.</summary>
    public string? BranchName { get; set; }

    /// <summary>A Story's ticket (SHOP-10) and its URL.</summary>
    public string? ExternalKey { get; set; }
    public string? ExternalUrl { get; set; }

    /// <summary>When the Story's pull request(s) were marked ready for review — the last member
    /// landed. Cleared when a late sub-task turns them back into drafts.</summary>
    public DateTimeOffset? ReadyAt { get; set; }

    /// <summary>Sub-tasks the tracker reported under the Story at the last poll that have no
    /// member yet. The story is not finished while any are pending.</summary>
    public int PendingChildCount { get; set; }

    // Navigation
    public ICollection<TaskEntity> Tasks { get; set; } = [];
    public ICollection<GroupBranch> Branches { get; set; } = [];
}
