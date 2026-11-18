namespace Orkka.Runners.Server;

using System.Text.Json;
using ih0.Claude.Agent.SDK;
using Orkka.Modules.Agents.Contracts;

/// <summary>
/// Presents a chat turn's <see cref="IChatToolServer"/> as the <see cref="ISdkMcpServer"/>
/// the <see cref="ProjectToolRegistry"/> holds, so a tool call forwarded from a runner is
/// answered by the same server an in-process turn would have called.
///
/// The Agents module has its own adapter for the in-process path; this one exists because
/// the registry (and the hub that reads it) live below that module and cannot see its
/// internals — the two are deliberately the same three lines of mapping.
/// </summary>
public sealed class ChatToolServerMcpAdapter(IChatToolServer inner) : ISdkMcpServer
{
    public string Name => inner.ServerName;

    public string Version => "1.0.0";

    public Task<IReadOnlyList<SdkMcpToolDefinition>> ListToolsAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SdkMcpToolDefinition> tools = [.. inner.ListTools().Select(t =>
        {
            using var schema = JsonDocument.Parse(t.InputSchemaJson);
            return new SdkMcpToolDefinition
            {
                Name = t.Name,
                Description = t.Description,
                // Clone detaches the element from the disposed document.
                InputSchema = schema.RootElement.Clone(),
            };
        })];
        return Task.FromResult(tools);
    }

    public async Task<SdkMcpToolResult> CallToolAsync(
        string name, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var result = await inner.CallToolAsync(name, arguments, cancellationToken);
        return new SdkMcpToolResult
        {
            Content = [new SdkMcpTextContent { Text = result.Text }],
            IsError = result.IsError,
        };
    }
}
