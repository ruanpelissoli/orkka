namespace Orkka.Modules.Tasks.Features.CodeChanges;

/// <summary>One stored dev-cycle snapshot, without its diff — the list view's shape.</summary>
public sealed record CodeChangeSnapshotSummaryResponse(
    Ulid Id,
    Ulid? ExecutionId,
    string Lane,
    DateTimeOffset CapturedAt,
    string BranchName,
    string BaseBranch,
    int FileCount,
    int Additions,
    int Deletions,
    bool IsTruncated,
    /// <summary>True for the row mirroring a run in progress (overwritten on each capture);
    /// listed first when present. False for permanent end-of-phase snapshots.</summary>
    bool IsLive = false,
    /// <summary>Capture counter of a live row — matches WorkspaceChanged.Revision.</summary>
    long Revision = 0);

/// <summary>
/// A snapshot's unified diff, read live from the developer's runner (parsed client-side).
/// Orkka stores no code, so for a finished snapshot this is the branch as it is NOW
/// (<paramref name="CurrentBranch"/>), and when the runner is offline or the branch is gone
/// <paramref name="Diff"/> is empty and <paramref name="Unavailable"/> says why — the
/// snapshot's paths and counts are still in the list view.
/// </summary>
public sealed record CodeChangeSnapshotDiffResponse(
    Ulid Id,
    DateTimeOffset CapturedAt,
    string BranchName,
    string BaseBranch,
    bool IsTruncated,
    string Diff,
    bool IsLive = false,
    long Revision = 0,
    bool CurrentBranch = false,
    string? Unavailable = null);
