namespace Orkka.LocalRunner;

using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Execution.Agents;

/// <summary>
/// The runner half of an interactive session's approval prompt.
///
/// The agent's Bash hook runs here, in the runner process; the human who has to rule on the
/// command is at a browser talking to the host. This relays the question over the runner's
/// existing hub connection and blocks on the answer — the only runner-initiated call that waits
/// on a person.
///
/// Every failure resolves to a refusal. A dropped link, a host that does not understand the
/// method, a malformed answer: none of them are a human saying yes, and the UI has already told
/// the user this command was gated. See <see cref="IAgentApprovalGate"/>.
/// </summary>
public sealed class RunnerApprovalGate(
    RunnerHostRouter router,
    ILogger<RunnerApprovalGate> logger) : IAgentApprovalGate
{
    public async Task<AgentApprovalOutcome> RequestAsync(
        AgentApprovalRequest request, CancellationToken ct)
    {
        // The session the run came from — with several workspaces, the question goes to the
        // Orkka (and the person) that started this task, never to another job's.
        var connection = router.Resolve(request.TaskId.ToString())?.Connection;
        if (connection is null || connection.State != HubConnectionState.Connected)
        {
            // The prompt cannot reach anyone. Denying is the honest outcome: the alternative is
            // running a gated command precisely when the platform has lost sight of the session.
            logger.LogWarning(
                "Approval for '{Command}' cannot be asked — the host link is down. Denying.",
                request.Command);
            return AgentApprovalOutcome.Denied;
        }

        try
        {
            var result = await connection.InvokeAsync<RunnerApprovalResult>(
                RunnerProtocol.RequestApproval,
                new RunnerApprovalRequest(
                    request.TaskId.ToString(),
                    request.ExecutionId?.ToString(),
                    request.ToolUseId,
                    "Bash",
                    request.Command,
                    request.ApprovalKey),
                ct);

            logger.LogInformation(
                "Approval for '{Command}' answered by the host as '{Outcome}'",
                request.Command, result?.Outcome ?? "(null)");

            return Parse(result?.Outcome);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Approval request for '{Command}' failed on the host link — denying", request.Command);
            return AgentApprovalOutcome.Denied;
        }
    }

    /// <summary>Anything unrecognised is a refusal, not a permission — including the answer an
    /// older host gives when it has never heard of this method.</summary>
    private static AgentApprovalOutcome Parse(string? outcome) => outcome switch
    {
        nameof(AgentApprovalOutcome.Allowed) => AgentApprovalOutcome.Allowed,
        nameof(AgentApprovalOutcome.AllowedForSession) => AgentApprovalOutcome.AllowedForSession,
        nameof(AgentApprovalOutcome.Expired) => AgentApprovalOutcome.Expired,
        _ => AgentApprovalOutcome.Denied,
    };
}
