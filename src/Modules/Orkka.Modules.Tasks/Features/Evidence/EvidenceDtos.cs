namespace Orkka.Modules.Tasks.Features.Evidence;

/// <summary>
/// Everything a reviewer needs to decide "is this done?" without reading the diff cold:
/// what was promised, what was delivered, what was verified, and what it cost.
/// </summary>
public sealed record TaskEvidenceResponse(
    Ulid TaskId,
    string Code,
    string Title,
    string Lane,
    string Status,
    string? BranchName,
    string? PrUrl,
    bool IsMerged,
    EvidencePlanResponse? Plan,
    /// <summary>Promised-vs-delivered file checklist, empty when the task had no plan.</summary>
    List<EvidenceFileCheckResponse> FileChecklist,
    /// <summary>Newest QA verdict, when the project runs the QA lane.</summary>
    EvidenceVerdictResponse? QaVerdict,
    /// <summary>Newest agent code-review verdict.</summary>
    EvidenceVerdictResponse? CodeReview,
    /// <summary>Per-lane run record — the audit trail behind the verdicts.</summary>
    List<EvidenceRunResponse> Runs,
    /// <summary>Screenshots and files captured during the run (QA browser evidence).</summary>
    List<EvidenceAttachmentResponse> Attachments,
    EvidenceTotalsResponse Totals);

public sealed record EvidencePlanResponse(
    Ulid PlanId,
    int Version,
    string Status,
    string Summary,
    string Approach,
    int? EstimatedTokens,
    DateTimeOffset? ApprovedAt);

/// <summary>
/// One row of the promised-vs-delivered checklist. <paramref name="State"/> is
/// "delivered" (planned and touched), "missing" (planned, never touched) or
/// "unplanned" (touched without being in the plan).
/// </summary>
public sealed record EvidenceFileCheckResponse(
    string Path,
    string State,
    string? PlannedAction,
    string? PlannedDescription);

public sealed record EvidenceVerdictResponse(
    /// <summary>"pass" | "fail" | "approve" | "request_changes" | "unknown".</summary>
    string Verdict,
    string Content,
    string? AgentName,
    DateTimeOffset CreatedAt);

public sealed record EvidenceRunResponse(
    Ulid ExecutionId,
    string Lane,
    string Status,
    string? AgentName,
    string? ErrorMessage,
    int FilesTouched,
    int TokensUsed,
    /// <summary>What this run cost, from the Usage module's rows. Normally zero: runs use the developer's own subscription
    /// (Orkka Local) and on runs executed against the user's own subscription.</summary>
    decimal Cost,
    double? DurationSeconds,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);

public sealed record EvidenceAttachmentResponse(
    Ulid Id,
    string FileName,
    string FileType,
    string FileUrl,
    long FileSize,
    DateTimeOffset CreatedAt);

public sealed record EvidenceTotalsResponse(
    int TotalTokens,
    decimal TotalCost,
    int RunCount,
    int FilesTouched,
    double? TotalDurationSeconds,
    int ReviewCycleCount,
    int QaCycleCount,
    int CheckCycleCount);
