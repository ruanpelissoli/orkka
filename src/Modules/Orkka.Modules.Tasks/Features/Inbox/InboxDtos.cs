namespace Orkka.Modules.Tasks.Features.Inbox;

using Orkka.Modules.Tasks.Features.Tasks;

/// <summary>
/// One thing waiting on a human, anywhere in the org. Carries enough context to be acted on
/// straight from the list — the point of the inbox is that opening the task should be optional.
/// </summary>
public sealed record InboxItemResponse(
    Ulid TaskId,
    Ulid ProjectId,
    string ProjectName,
    string ProjectKey,
    string Code,
    int Number,
    string Title,
    /// <summary>Machine-readable action kind — see <see cref="InboxItemKind"/>.</summary>
    string Kind,
    string Lane,
    string Status,
    string Priority,
    string? RepoName,
    string? PrUrl,
    string? CreatedByUserId,
    /// <summary>Agent that parked the task, when known (from the newest agent message).</summary>
    string? AgentName,
    /// <summary>Newest agent message, truncated — usually the question or the verdict.</summary>
    string? Preview,
    /// <summary>When the task entered its current waiting state.</summary>
    DateTimeOffset WaitingSince,
    /// <summary>Open tasks that cannot start until this one is resolved.</summary>
    int BlockedDependents,
    /// <summary>Ranking score; higher sorts first. See <see cref="InboxRanking"/>.</summary>
    int Score);

/// <summary>Per-kind counts for the inbox header, in descending count order.</summary>
public sealed record InboxCountResponse(string Kind, int Count);

public sealed record InboxResponse(
    List<InboxItemResponse> Items,
    List<InboxCountResponse> Counts,
    int Total);

/// <summary>
/// The action a human owes the task. Kept as strings on the wire so adding a kind never
/// breaks an older client — unknown kinds render with a neutral fallback.
/// </summary>
public static class InboxItemKind
{
    public const string PlanApproval = "plan_approval";
    public const string Question = "question";
    public const string CodeReview = "code_review";
    public const string QaReview = "qa_review";
    public const string Verification = "verification";
    public const string MergeConflict = "merge_conflict";
    public const string MergeFailed = "merge_failed";
    public const string Blocked = "blocked";
    public const string RunnerOffline = "runner_offline";
    public const string OutOfTokens = "out_of_tokens";
    public const string Error = "error";
}

public sealed record InboxFilterRequest
{
    /// <summary>"all" (default) or "mine" — items on tasks the caller created.</summary>
    public string Scope { get; init; } = "all";

    /// <summary>Optional single-project filter; omit for the cross-project view.</summary>
    public Ulid? ProjectId { get; init; }

    /// <summary>Optional kind filter, e.g. "code_review".</summary>
    public string? Kind { get; init; }

    public int Limit { get; init; } = 100;
}
