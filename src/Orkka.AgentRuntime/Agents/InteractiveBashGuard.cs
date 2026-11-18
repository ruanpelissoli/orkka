namespace Orkka.Modules.Execution.Agents;

using System.Collections.Concurrent;
using System.Text.Json;
using ih0.Claude.Agent.SDK.Types;
using Microsoft.Extensions.Logging;

/// <summary>
/// The PreToolUse Bash hook for interactive development sessions: the same command guard as
/// <see cref="AgentBashGuard"/>, plus a third outcome the batch pipeline has no use for — asking
/// the human who is sitting right there.
///
/// **One guard, not two.** This deliberately replaces <see cref="AgentBashGuard"/> for interactive
/// runs rather than being registered alongside it. Two hooks on the same event would make
/// correctness depend on how the CLI composes their answers, and the failure mode of getting that
/// wrong is that the command runs anyway. The blocklist decision itself is shared, not
/// reimplemented: <see cref="AgentBashGuard.IsBlockedCommand"/> is the single source of truth, so
/// the two guards cannot drift about what "restricted" means.
///
/// See .claude/docs/INTERACTIVE.md.
/// </summary>
public sealed class InteractiveBashGuard(
    Ulid taskId,
    Ulid? executionId,
    string agentName,
    string agentRole,
    string? worktreePath,
    IAgentApprovalGate gate,
    ILogger logger,
    Func<string, Task>? onBlocked = null)
{
    /// <summary>
    /// Keys the human said "always allow" to, for this guard's lifetime — which is one agent run.
    ///
    /// Deliberately not persisted across turns: a session can run for an hour, and a blanket
    /// permission the user granted at the start is not one they can still see when it is used.
    /// Re-asking once per turn is a small price for a permission that stays visible.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _sessionAllowed = new(StringComparer.Ordinal);

    private int _blockedAttempts;

    public async Task<HookOutput> EvaluateCommand(HookInput input, string? toolUseId, HookContext? context)
    {
        var command = ExtractCommand(input);
        if (string.IsNullOrWhiteSpace(command))
            return AgentHookResponses.Allow();

        var disposition = InteractiveCommandPolicy.Classify(command, worktreePath);

        if (disposition == CommandDisposition.Blocked)
            return await DenyBlockedAsync(command);

        if (disposition == CommandDisposition.Allowed)
        {
            logger.LogDebug("Interactive agent {AgentName} bash (auto): {Command}", agentName, command);
            return AgentHookResponses.Allow();
        }

        // EVERY ask-worthy part of the line must be covered, not just the part this command
        // happens to key on. Matching a single key against the whole line let the rest of it in
        // free: a grant for `git push` would have admitted `git push && rm -rf /`.
        if (InteractiveCommandPolicy.IsCoveredBySessionGrants(command, worktreePath, _sessionAllowed.ContainsKey))
        {
            logger.LogInformation(
                "Interactive agent {AgentName} bash (covered by earlier approvals this run): {Command}",
                agentName, command);
            return AgentHookResponses.Allow();
        }

        // Empty when the line has several ask-worthy parts — there is no one label that describes
        // what a session-wide grant would cover, so only Allow once and Deny are offered.
        var key = InteractiveCommandPolicy.ApprovalKey(command, worktreePath);

        return await AskHumanAsync(command, key, toolUseId);
    }

    private async Task<HookOutput> AskHumanAsync(string command, string key, string? toolUseId)
    {
        // The CLI's tool-use id is the correlation key the browser's answer comes back on. Its
        // absence is not a reason to run the command unasked — synthesize one so the request is
        // still routable and still requires an answer.
        var correlationId = string.IsNullOrWhiteSpace(toolUseId)
            ? $"synthetic-{Ulid.NewUlid()}"
            : toolUseId;

        logger.LogInformation(
            "Interactive session {TaskId} asking for approval of '{Command}' (key '{Key}')",
            taskId, command, key);

        AgentApprovalOutcome outcome;
        try
        {
            outcome = await gate.RequestAsync(
                new AgentApprovalRequest(taskId, executionId, correlationId, command, key),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Fail closed. A gate that threw told us nothing about what the human wanted, and the
            // UI has already promised them this command was gated.
            logger.LogError(ex,
                "Approval gate failed for interactive session {TaskId} — denying '{Command}'",
                taskId, command);
            outcome = AgentApprovalOutcome.Expired;
        }

        switch (outcome)
        {
            case AgentApprovalOutcome.AllowedForSession when key.Length > 0:
                _sessionAllowed.TryAdd(key, 0);
                goto case AgentApprovalOutcome.Allowed;

            case AgentApprovalOutcome.AllowedForSession:
            case AgentApprovalOutcome.Allowed:
                logger.LogInformation(
                    "Interactive session {TaskId} approved '{Command}'", taskId, command);
                return AgentHookResponses.Allow();

            case AgentApprovalOutcome.Expired:
                // Worded as expiry, not restriction. An agent told "this is restricted" concludes
                // the tool is closed to it and stops trying; an agent told nobody answered knows
                // it may ask again, which is the truth.
                return AgentHookResponses.Deny(
                    "The approval request for this command expired before anyone answered it. "
                    + "Nothing is wrong with the command or your tools — say what you were about "
                    + "to do and ask the human to approve it, or take a different route.");

            default:
                return AgentHookResponses.Deny(
                    "The human declined this command. Do not retry it. Continue with the rest of "
                    + "the work, or ask them what they would prefer you do instead.");
        }
    }

    /// <summary>Layer 3, unchanged and unaskable. Mirrors <see cref="AgentBashGuard"/>'s wording
    /// and its three-strike audit escalation.</summary>
    private async Task<HookOutput> DenyBlockedAsync(string command)
    {
        _blockedAttempts++;

        logger.LogWarning(
            "SECURITY: Interactive agent {AgentName} ({Role}) blocked command: {Command} (attempt {Count})",
            agentName, agentRole, command, _blockedAttempts);

        if (onBlocked is not null)
        {
            try
            {
                await onBlocked(command);
            }
            catch
            {
                // Auditing must never interfere with the guard decision.
            }
        }

        if (_blockedAttempts >= 3)
        {
            logger.LogCritical(
                "SECURITY: Interactive agent {AgentName} — {Count} blocked command attempts. Last: {Command}",
                agentName, _blockedAttempts, command);

            return AgentHookResponses.Deny(
                "This command is restricted, and repeated attempts are being audited. Your other "
                + "tools are unaffected — keep working inside the repository, and stop retrying "
                + "credential reads, pushes, remote hosts and cloud CLIs.");
        }

        return AgentHookResponses.Deny(
            "It cannot be approved by anyone, including the human in this session. "
            + AgentBashGuard.BlockedReason);
    }

    private static string ExtractCommand(HookInput input)
    {
        if (input is PreToolUseHookInput preToolUse
            && preToolUse.ToolInput.ValueKind == JsonValueKind.Object
            && preToolUse.ToolInput.TryGetProperty("command", out var commandProp))
        {
            return commandProp.GetString() ?? "";
        }

        return "";
    }
}
