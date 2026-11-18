namespace Orkka.Modules.Tasks.Domain;

using Orkka.Modules.Tasks.Contracts;
using Orkka.SharedKernel.Domain;

/// <summary>
/// What a dev run changed — which files, how many lines — captured at the moment the branch
/// still had a diff to read. **Never the code itself:** Orkka stores no source code. The diff
/// is read live from the developer's runner whenever someone opens it
/// (<c>GetCodeChangeSnapshotDiffEndpoint</c>), and this row keeps only paths and counts, so the
/// change history survives a merged branch without a copy of the repository on Orkka's side.
///
/// One row per completed dev cycle: a task that bounces Developing → Review → Developing
/// accumulates history. CreatedAt is the capture time.
/// </summary>
public sealed class TaskCodeChangeSnapshot : TenantEntity
{
    /// <summary>Cap on the stored unified diff — see <see cref="CodeChangeSnapshotRules"/>.</summary>
    public const int MaxDiffChars = CodeChangeSnapshotRules.MaxDiffChars;

    public Ulid TaskId { get; set; }

    /// <summary>The execution that produced the change. Null only when the row was captured
    /// outside a run (e.g. a backfill for a task already in flight when this shipped).</summary>
    public Ulid? ExecutionId { get; set; }

    /// <summary>Cross-module reference to the Projects module — plain column, no FK.</summary>
    public Ulid RepoId { get; set; }

    /// <summary>The lane whose phase captured it: Developing (primary) or CodeReview (backfill).</summary>
    public Lane Lane { get; set; }

    public string BranchName { get; set; } = "";

    public string BaseBranch { get; set; } = "";

    /// <summary>True when the diff the stats were computed from hit the cap, so the counts are a
    /// lower bound.</summary>
    public bool IsTruncated { get; set; }

    /// <summary>SHA-256 hex of the diff the stats came from — a re-entered phase that changed
    /// nothing must not duplicate rows. A hash, not the content.</summary>
    public string DiffHash { get; set; } = "";

    public int FileCount { get; set; }

    public int Additions { get; set; }

    public int Deletions { get; set; }

    /// <summary>Per-file summary as JSON: [{"path","status","additions","deletions"}].</summary>
    public string? FilesJson { get; set; }

    /// <summary>
    /// True for the one row per task that mirrors a run in progress: the working tree as of
    /// <see cref="BaseEntity.UpdatedAt"/>, overwritten on every capture and deleted when the
    /// phase stores its permanent snapshot. Permanent rows are never live.
    /// </summary>
    public bool IsLive { get; set; }

    /// <summary>Monotonic per live row — bumps on every overwrite so clients can drop a
    /// stale broadcast. Always 0 on permanent rows.</summary>
    public long Revision { get; set; }

    public TaskEntity Task { get; set; } = null!;
}
