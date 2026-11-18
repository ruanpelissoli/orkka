namespace Orkka.Modules.Execution.Agents;

using System.Runtime.CompilerServices;
using ih0.Claude.Agent.SDK;
using ih0.Claude.Agent.SDK.Types;
using Microsoft.Extensions.Logging;

/// <summary>
/// One-shot SDK query wrapper. The host's logger factory is handed to the SDK so its own
/// diagnostics reach the worker log — above all <c>QueryHandler</c>'s "Error handling control
/// request" warning, which is where an in-process MCP server's handler failure surfaces on the
/// SDK side; without a factory the SDK logs to <c>NullLogger</c> and the only trace of such a
/// failure is the CLI's mcp-logs file.
/// </summary>
public sealed class ClaudeAgentClient(ILoggerFactory? loggerFactory = null) : IClaudeAgentClient
{
    public async IAsyncEnumerable<Message> QueryAsync(
        string prompt, ClaudeAgentOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var message in ClaudeAgent.QueryAsync(prompt, options, loggerFactory, ct))
        {
            yield return message;
        }
    }
}
