namespace Orkka.Modules.Agents.Contracts;

using System.Text.Json;

/// <summary>One callable tool exposed to a chat agent. <paramref name="InputSchemaJson"/> is a
/// raw JSON Schema document — it travels over the MCP tools/list channel, not the system
/// prompt, so it can afford per-property descriptions.</summary>
/// <param name="TimeoutSeconds">How long a caller should wait for this tool. The default suits
/// a tool that reads or writes the database; a tool that generates something must say so — a
/// relayed turn bounds the wait on the runner, and a budget shorter than the work tells the
/// agent the tool failed while the platform quietly finishes it anyway.</param>
public sealed record ChatToolDefinition(
    string Name,
    string Description,
    string InputSchemaJson,
    int TimeoutSeconds = ChatToolDefinition.DefaultTimeoutSeconds)
{
    /// <summary>Generous for a database round-trip on a busy host, short enough that a tool
    /// which will never answer is reported as failed while the turn can still act on it.</summary>
    public const int DefaultTimeoutSeconds = 60;
}

/// <summary>Outcome of a chat tool call, returned to the model as the tool result. Errors go
/// back as <see cref="IsError"/> results (never exceptions) so the model can self-correct
/// within the same turn.</summary>
public sealed record ChatToolResult(string Text, bool IsError = false);

/// <summary>
/// Per-turn tool surface attached to a chat run. Implementations are scoped to ONE turn:
/// they carry the turn's org/project/conversation identity internally, so tool arguments
/// only ever contain content fields — the model cannot address another tenant. Invokers
/// that execute in-process bridge this to the Claude Agent SDK's in-process MCP server
/// mechanism; invokers that relay elsewhere ignore it (the caller decides eligibility).
/// </summary>
/// <summary>
/// Everything a chat turn's tool server needs to act as that turn: who is asking, which
/// project and conversation, and the assistant message artifacts attach to. Deliberately
/// ids and roles only — a tool call must never take its identity from tool INPUT.
///
/// It crosses a process boundary: a turn relayed to the user's own machine builds its tool
/// server on the Worker (which holds the database and the runner link), not on the API pod
/// that started the turn.
/// </summary>
public sealed record ChatToolServerContext(
    Ulid OrgId,
    Ulid ProjectId,
    Ulid AgentId,
    Ulid ConversationId,
    Ulid AssistantMessageId,
    string RequesterUserId,
    string? RequesterLocale,
    AgentRole AgentRole,
    /// <summary>Gallery folder open when the turn started; new design assets are filed
    /// there. Optional and last: a host that predates it simply sends nothing.</summary>
    string? DesignFolderPath = null);

/// <summary>
/// Builds a chat turn's tool server outside the Agents module — for the Worker's chat relay,
/// which has the modules and the database but cannot see the module's internals.
/// </summary>
public interface IChatToolServerFactory
{
    /// <param name="emit">Receives the same SSE payloads an in-process turn emits
    /// (<c>{ document = … }</c>, <c>{ task = … }</c>): the relay forwards them to the API as
    /// <see cref="ChatChunkType.Artifact"/> chunks so the panel renders tool-created
    /// artifacts live, exactly as it does for a cloud turn. Called from tool handlers, so it
    /// must be safe to call from any thread.</param>
    IChatToolServer Create(ChatToolServerContext context, Action<object> emit);
}

public interface IChatToolServer
{
    /// <summary>MCP server name — tools surface to the model as mcp__{ServerName}__{tool}.</summary>
    string ServerName { get; }

    /// <summary>Tool definitions for this turn, already filtered to the agent's role.</summary>
    IReadOnlyList<ChatToolDefinition> ListTools();

    Task<ChatToolResult> CallToolAsync(
        string toolName, JsonElement arguments, CancellationToken ct = default);
}
