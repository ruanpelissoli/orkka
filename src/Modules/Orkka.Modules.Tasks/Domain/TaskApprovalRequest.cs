namespace Orkka.Modules.Tasks.Domain;

using Orkka.Modules.Tasks.Contracts;
using Orkka.SharedKernel.Domain;

/// <summary>
/// A command an interactive session paused on, waiting for a human to rule.
///
/// Persisted rather than kept in memory for two reasons. It is an audit record — "who allowed the
/// agent to push, and when" is exactly the question SECURITY.md's Layer 7 exists to answer — and
/// it is what lets a reopened browser (or a second one) see a prompt that is still pending
/// instead of a session that appears to have silently stalled.
///
/// See .claude/docs/INTERACTIVE.md.
/// </summary>
public sealed class TaskApprovalRequest : TenantEntity
{
    public Ulid TaskId { get; set; }

    /// <summary>The turn that wants to run the command. Null only if the execution row vanished.</summary>
    public Ulid? ExecutionId { get; set; }

    /// <summary>The CLI's tool-use id — the correlation key from the hook to the browser and back.
    /// Unique per task so a decision can never be applied to the wrong prompt.</summary>
    public required string ToolUseId { get; set; }

    /// <summary>The tool being gated. "Bash" today; the column exists so adding Write or WebFetch
    /// later does not need a migration.</summary>
    public required string Tool { get; set; }

    /// <summary>The exact command line, shown to the human verbatim. Never summarised — a
    /// paraphrased command is not the thing they are approving.</summary>
    public required string Command { get; set; }

    /// <summary>The coarser shape an "always allow" applies to (e.g. <c>git push</c>), surfaced in
    /// the UI so the user can see what a session-wide grant actually covers.</summary>
    public required string ApprovalKey { get; set; }

    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;

    /// <summary>Set when a human answered. Null for Pending and Expired — nobody decided those.</summary>
    public string? DecidedByUserId { get; set; }

    public DateTimeOffset? DecidedAt { get; set; }
}
