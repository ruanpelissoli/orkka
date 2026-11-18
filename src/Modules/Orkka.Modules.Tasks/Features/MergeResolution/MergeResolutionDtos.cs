namespace Orkka.Modules.Tasks.Features.MergeResolution;

/// <summary>One conflicted file for the merge page's advanced (per-file) mode.</summary>
public sealed record MergeConflictFileResponse(string Path, string Detail, string? Choice);

/// <summary>
/// Merge-conflict state of a task for the merge page.
///
/// Status: "none" (nothing to resolve), "resolved" (merged), "resolving" (a resolution is
/// queued or running), "blocked" (the user's environment must change first — no resolution can
/// run), "awaitingChoice" (a human decision would likely unstick it), "exhausted" (automation
/// ran its full budget, its output was verified, and the merge still is not finished).
///
/// "awaitingChoice" and "exhausted" are deliberately distinct. The first is an invitation — pick
/// a side and it will probably land. The second is a report: everything automatic has been tried
/// and a person has to look at the code. Showing the same screen for both taught users that the
/// chooser never works.
/// </summary>
/// <param name="FailureKind">Machine-readable companion to <paramref name="FailureReason"/> —
/// the <c>MergeFailureKind</c> name, or null on tasks that failed before it was recorded.</param>
public sealed record MergeConflictsResponse(
    string Status,
    string TaskTitle,
    string? Summary,
    List<MergeConflictFileResponse> Files,
    string? FailureReason,
    string? PendingStrategy,
    string? FailureKind,
    /// <summary>When the running attempt began, so the page shows a true elapsed time instead
    /// of counting from whenever the browser happened to open it. Null unless one is running.</summary>
    DateTimeOffset? StartedAt = null,
    /// <summary>A follow-up the user queued while the current attempt runs — it starts as soon
    /// as that one ends without merging.</summary>
    string? QueuedStrategy = null,
    string? QueuedGuidance = null);

/// <summary>
/// Human-picked resolution. Strategy: "combine" (AI merges both sides, optionally steered
/// by Guidance), "task" (this task's version wins overlaps), "current" (the app as it is
/// wins overlaps), "perFile" (Files carries path → "task" | "current" | "ai").
/// </summary>
public sealed record ResolveMergeRequest(
    string Strategy, string? Guidance, List<ResolveMergeFileChoice>? Files);

public sealed record ResolveMergeFileChoice(string Path, string Choice);
