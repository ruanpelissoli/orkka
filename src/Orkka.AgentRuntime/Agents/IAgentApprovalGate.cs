namespace Orkka.Modules.Execution.Agents;

/// <summary>A command an interactive session is waiting for a human to rule on.</summary>
/// <param name="TaskId">The session's task — also the SignalR group the prompt is pushed to.</param>
/// <param name="ExecutionId">The turn that wants to run it, for the audit trail.</param>
/// <param name="ToolUseId">The CLI's id for this tool call — the correlation key end to end.</param>
/// <param name="Command">The exact command line, shown to the human verbatim.</param>
/// <param name="ApprovalKey">Coarser shape an "always allow" applies to (see
/// <see cref="InteractiveCommandPolicy.ApprovalKey"/>).</param>
public sealed record AgentApprovalRequest(
    Ulid TaskId,
    Ulid? ExecutionId,
    string ToolUseId,
    string Command,
    string ApprovalKey);

/// <summary>How a human ruled, or how the platform ruled on their behalf.</summary>
public enum AgentApprovalOutcome
{
    /// <summary>Run it, this once.</summary>
    Allowed,

    /// <summary>Run it, and stop asking about this <see cref="AgentApprovalRequest.ApprovalKey"/>
    /// for the rest of the session.</summary>
    AllowedForSession,

    /// <summary>Refused by the human.</summary>
    Denied,

    /// <summary>Nobody answered in time, or the request could not be delivered. Always treated as
    /// a refusal — see <see cref="IAgentApprovalGate"/>.</summary>
    Expired,
}

/// <summary>
/// Asks a human to rule on a command mid-run.
///
/// Implemented on whichever side actually runs the agent (the runner daemon on Orkka Local, the
/// worker in cloud execution) and answered by the browser through the host, so the call blocks
/// for as long as the person takes to decide.
///
/// **Failure means refusal, never permission.** A timeout, a dropped connection, a missing
/// implementation — every path that does not produce an explicit human "yes" must resolve to
/// <see cref="AgentApprovalOutcome.Expired"/> or <see cref="AgentApprovalOutcome.Denied"/>. An
/// approval prompt that fails open is worse than no prompt at all, because the UI has told the
/// user the command is gated.
/// </summary>
public interface IAgentApprovalGate
{
    Task<AgentApprovalOutcome> RequestAsync(AgentApprovalRequest request, CancellationToken ct);
}

/// <summary>
/// The gate for runs that are not interactive sessions, and the fallback when a host has not
/// registered a real one.
///
/// Denies rather than allows: reaching here means the run has no human attached, and the only
/// commands that reach a gate at all are the ones the policy already decided need a person.
/// </summary>
public sealed class DenyingAgentApprovalGate : IAgentApprovalGate
{
    public Task<AgentApprovalOutcome> RequestAsync(AgentApprovalRequest request, CancellationToken ct)
        => Task.FromResult(AgentApprovalOutcome.Denied);
}
