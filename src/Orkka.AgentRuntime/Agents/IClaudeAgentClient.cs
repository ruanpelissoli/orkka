namespace Orkka.Modules.Execution.Agents;

using ih0.Claude.Agent.SDK;
using ih0.Claude.Agent.SDK.Types;

/// <summary>
/// Thin wrapper around ClaudeAgent.QueryAsync for testability.
/// </summary>
public interface IClaudeAgentClient
{
    IAsyncEnumerable<Message> QueryAsync(
        string prompt, ClaudeAgentOptions options, CancellationToken ct = default);
}
