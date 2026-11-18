namespace Orkka.LocalRunner;

using System.Text.Json;
using ih0.Claude.Agent.SDK;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Orkka.LocalRunner.Contracts;

/// <summary>
/// The runner half of a run's project-context tools. The agent's CLI runs here, but the
/// tools answer from the host's database — so this server holds the definitions the host
/// sent with the run and forwards every call back over <see cref="RunnerProtocol.CallProjectTool"/>.
///
/// Every failure is an error RESULT, never an exception: a dropped link mid-call should
/// read to the agent as "that tool failed, keep working from the worktree", not take the
/// whole run down.
/// </summary>
internal sealed class HostProjectToolServer(
    string taskId,
    RunnerToolDefinition[] tools,
    RunnerHostLink hostLink,
    ILogger logger) : ISdkMcpServer
{
    public string Name => "orkka";

    /// <summary>How long a forwarded call may take when the host named no budget of its own —
    /// comfortably above a database read, and below the CLI's own patience, so a stuck host
    /// reads as a tool error rather than an interrupted call.</summary>
    private static readonly TimeSpan DefaultHostCallTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The host's budget for each tool, because only the host knows what its tools do. A flat
    /// 30s here cut off create_design_assets, which legitimately runs for minutes: the agent
    /// was told the tool had failed while the platform went on to create the assets anyway.
    /// </summary>
    private TimeSpan TimeoutFor(string tool) =>
        tools.FirstOrDefault(t => t.Name == tool)?.TimeoutSeconds is { } seconds and > 0
            ? TimeSpan.FromSeconds(seconds)
            : DefaultHostCallTimeout;

    public string Version => "1.0.0";

    public Task<IReadOnlyList<SdkMcpToolDefinition>> ListToolsAsync(
        CancellationToken cancellationToken = default)
    {
        var definitions = new List<SdkMcpToolDefinition>(tools.Length);
        foreach (var tool in tools)
        {
            try
            {
                using var schema = JsonDocument.Parse(tool.InputSchemaJson);
                definitions.Add(new SdkMcpToolDefinition
                {
                    Name = tool.Name,
                    Description = tool.Description,
                    // Clone detaches the element from the disposed document.
                    InputSchema = schema.RootElement.Clone(),
                });
            }
            catch (JsonException ex)
            {
                // One bad schema drops one tool, not the set.
                logger.LogWarning(ex, "Skipping project tool {Tool} — unparseable schema", tool.Name);
            }
        }
        return Task.FromResult<IReadOnlyList<SdkMcpToolDefinition>>(definitions);
    }

    public async Task<SdkMcpToolResult> CallToolAsync(
        string name, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var connection = hostLink.Connection;
        if (connection is null || connection.State != HubConnectionState.Connected)
        {
            return Error(
                $"The {name} tool is unavailable right now (the host link is down). "
                + "Continue from what is in the worktree.");
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var budget = TimeoutFor(name);
        try
        {
            // Bounded: a forwarded call that never comes back would otherwise leave the agent
            // waiting until the CLI gives up and reports the call as "interrupted", which says
            // nothing about where it died. A timeout is an answer the agent can act on.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(budget);

            var result = await connection.InvokeAsync<RunnerToolCallResult>(
                RunnerProtocol.CallProjectTool,
                new RunnerToolCallRequest(taskId, name, arguments.GetRawText()),
                timeout.Token);

            logger.LogInformation(
                "Project tool {Tool} answered by the host in {Elapsed}ms (run {Key}, error: {IsError})",
                name, sw.ElapsedMilliseconds, taskId, result.IsError);

            return new SdkMcpToolResult
            {
                Content = new[] { new SdkMcpTextContent { Text = result.Text } },
                IsError = result.IsError,
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Project tool {Tool} got no answer from the host within {Timeout}s (run {Key})",
                name, budget.TotalSeconds, taskId);
            // Deliberately NOT "try once more": the host may still be working, and a tool
            // that generates images bills for every attempt. Inviting a retry after a 30s
            // budget cut off a 400s design run, and the agent generated the same three
            // images a second time (prod, 2026-09-08).
            return Error(
                $"The {name} tool has not answered within {budget.TotalSeconds:F0}s. It may "
                + "still be running on the platform — do NOT call it again. Tell the user it "
                + "is taking longer than expected and continue with the rest of the turn.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Project tool {Tool} failed on the host link after {Elapsed}ms",
                name, sw.ElapsedMilliseconds);
            return Error(
                $"The {name} tool failed to reach the host. Continue from what is in the worktree.");
        }
    }

    private static SdkMcpToolResult Error(string message) => new()
    {
        Content = new[] { new SdkMcpTextContent { Text = message } },
        IsError = true,
    };
}
