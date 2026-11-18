namespace Orkka.Modules.Tasks.Contracts;

/// <summary>A command awaiting a human ruling, as pushed to the browser.</summary>
public sealed record PendingApproval(
    Ulid Id,
    Ulid TaskId,
    string ToolUseId,
    string Tool,
    string Command,
    string ApprovalKey,
    DateTimeOffset CreatedAt);

/// <summary>How a human ruled. Mirrors the runtime's outcome enum across the module boundary.</summary>
public enum ApprovalDecision
{
    Allow,
    AllowForSession,
    Deny,
}

/// <summary>
/// Holds interactive sessions' in-flight approval prompts: raises them, waits for a human, and
/// hands the answer back to whichever side is blocking on it.
///
/// In-memory by design, like <c>ChatRunCoordinator</c>. A pending prompt belongs to a live agent
/// run, and a process that restarts has already lost the run — the persisted row is the audit
/// trail and the reload state, not a resume point. Anything still Pending after its session's
/// lease lapses is swept to Expired rather than waited on forever.
///
/// Single-process today. The hosted multi-pod story (routing a decision to the worker holding the
/// run) is slice 4 — see .claude/docs/INTERACTIVE.md.
/// </summary>
public interface IInteractiveApprovalCoordinator
{
    /// <summary>
    /// Raises a prompt and blocks until a human answers or <paramref name="timeout"/> elapses.
    /// Never throws to the caller: every failure path resolves to a refusal, because the caller
    /// is a security hook whose only safe default is "no".
    /// </summary>
    Task<ApprovalOutcome> RequestAsync(
        Ulid orgId, Ulid taskId, Ulid? executionId, string toolUseId, string tool,
        string command, string approvalKey, TimeSpan timeout, CancellationToken ct);

    /// <summary>
    /// Applies a human's decision. False when the prompt is unknown or already settled — a second
    /// click, or an answer that arrived after the prompt expired.
    /// </summary>
    Task<bool> DecideAsync(
        Ulid orgId, Ulid taskId, Ulid approvalId, ApprovalDecision decision, string? userId,
        CancellationToken ct);

    /// <summary>Prompts still waiting, for a browser that just opened the task.</summary>
    Task<IReadOnlyList<PendingApproval>> ListPendingAsync(
        Ulid orgId, Ulid taskId, CancellationToken ct);

    /// <summary>
    /// Fails every prompt still pending for a task, releasing whatever is blocked on them. Called
    /// when a session ends or its worker dies — a prompt whose run is gone can never be answered,
    /// and leaving it on screen invites a click that does nothing.
    /// </summary>
    Task ExpirePendingAsync(Ulid taskId, CancellationToken ct);
}

/// <summary>The settled result of a prompt.</summary>
public enum ApprovalOutcome
{
    Allowed,
    AllowedForSession,
    Denied,
    Expired,
}
