namespace Orkka.Runners.Server;

using System.Collections.Concurrent;
using System.Text.Json;
using ih0.Claude.Agent.SDK;
using Microsoft.Extensions.Logging;
using Orkka.LocalRunner.Contracts;

/// <summary>
/// Live project-context tool servers for runs currently proxied to a runner, keyed by task
/// id. The tool server is a host-side object (it answers from the host's database), but the
/// agent that calls its tools runs in the runner process — so the runner forwards each call
/// over <c>CallProjectTool</c>, and the hub method resolves the run's server here.
///
/// Entries carry the run's org because the hosted hub is multi-tenant: a runner may only
/// call into runs of its own org, verified against the connection's bound identity — the
/// task id in the payload is a lookup key, never an authorization.
///
/// One entry per task at a time, which the task lease already guarantees; registration is
/// scoped to the run by <see cref="RunnerAgentInvoker"/>'s try/finally, so a crashed run
/// cannot leak an entry past its stream.
/// </summary>
public sealed class ProjectToolRegistry
{
    public sealed record Entry(Ulid OrgId, ISdkMcpServer Server);

    private readonly ConcurrentDictionary<string, Entry> _byTask = new(StringComparer.Ordinal);

    public void Register(string taskId, Ulid orgId, ISdkMcpServer server) =>
        _byTask[taskId] = new Entry(orgId, server);

    public void Unregister(string taskId) => _byTask.TryRemove(taskId, out _);

    public Entry? TryGet(string taskId) =>
        _byTask.TryGetValue(taskId, out var entry) ? entry : null;
}

/// <summary>The hub-side half of a forwarded tool call, shared by the single-tenant local
/// hub and the multi-tenant hosted hub so the two cannot drift.</summary>
public static class ProjectToolCalls
{
    /// <param name="callerOrgId">The org the calling connection is bound to. Null skips the
    /// check — only valid on the single-tenant local hub, where the one connection IS the
    /// machine's runner.</param>
    public static async Task<RunnerToolCallResult> HandleAsync(
        ProjectToolRegistry registry, RunnerToolCallRequest request, Ulid? callerOrgId,
        ILogger logger, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var entry = registry.TryGet(request.TaskId);
        if (entry is null || (callerOrgId is { } org && entry.OrgId != org))
        {
            // Loud on purpose: from the agent's side this is indistinguishable from a tool
            // that simply does not work, and the cause (a run whose registration has ended,
            // or a call landing on a replica that never held it) is only visible here.
            logger.LogWarning(
                "Forwarded project tool {Tool} has no live registration for run {Key} — answering \"unavailable\"",
                request.Tool, request.TaskId);
            // Fail as an error RESULT: the agent should read "tool unavailable" and keep
            // working from the worktree, not lose its run to a stale registration.
            return new RunnerToolCallResult(
                "This tool is not available for the current run — continue from what is in "
                + "the worktree.", IsError: true);
        }

        try
        {
            using var arguments = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(request.ArgumentsJson) ? "{}" : request.ArgumentsJson);

            var result = await entry.Server.CallToolAsync(request.Tool, arguments.RootElement, ct);

            var text = string.Join("\n", result.Content
                .OfType<SdkMcpTextContent>()
                .Select(c => c.Text));

            logger.LogInformation(
                "Forwarded project tool {Tool} for run {Key} answered in {Elapsed}ms (error: {IsError})",
                request.Tool, request.TaskId, sw.ElapsedMilliseconds, result.IsError == true);

            return new RunnerToolCallResult(text, result.IsError == true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Forwarded project tool {Tool} failed for run {Key} after {Elapsed}ms",
                request.Tool, request.TaskId, sw.ElapsedMilliseconds);
            return new RunnerToolCallResult(
                $"The {request.Tool} tool failed on the host — continue from what is in the worktree.",
                IsError: true);
        }
    }
}
