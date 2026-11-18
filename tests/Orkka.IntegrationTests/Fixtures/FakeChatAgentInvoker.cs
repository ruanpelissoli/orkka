namespace Orkka.IntegrationTests.Fixtures;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Orkka.Modules.Agents.Contracts;

/// <summary>
/// Controllable <see cref="IChatAgentInvoker"/> for agent-chat SSE tests. Tests enqueue a
/// script per expected invocation; without one, a run yields a single text chunk and
/// completes. Scripts can block on a gate so tests can disconnect/cancel mid-stream.
/// </summary>
public sealed class FakeChatAgentInvoker : IChatAgentInvoker
{
    public sealed record Invocation(string WorkspacePath, string SystemPrompt, string UserMessage, string Model, string? ResumeSessionId, Ulid? ConversationId = null, IChatToolServer? ToolServer = null);

    public ConcurrentQueue<Invocation> Invocations { get; } = new();

    // Reports tool support so the endpoint's eligibility check can enable tools in tests
    // (still gated on the AgentChat:Tools:Enabled flag, default on; the suite sets it
    // explicitly so the fixture stays independent of the shipped default).
    public bool SupportsChatTools => true;

    private readonly ConcurrentQueue<Func<IChatToolServer?, CancellationToken, IAsyncEnumerable<ChatChunk>>> _scripts = new();

    public void EnqueueScript(Func<CancellationToken, IAsyncEnumerable<ChatChunk>> script) =>
        _scripts.Enqueue((_, ct) => script(ct));

    /// <summary>Script that also receives the turn's attached tool server (null when the
    /// turn ran without tools) — lets tests invoke platform tools mid-stream like the CLI would.</summary>
    public void EnqueueToolScript(Func<IChatToolServer?, CancellationToken, IAsyncEnumerable<ChatChunk>> script) =>
        _scripts.Enqueue(script);

    /// <summary>Script that emits the given chunks and completes immediately.</summary>
    public void EnqueueChunks(params ChatChunk[] chunks) =>
        _scripts.Enqueue((_, _) => ToAsyncEnumerable(chunks));

    /// <summary>Script that emits <paramref name="before"/>, waits for the returned gate to
    /// be released, then emits <paramref name="after"/>. Cancelling the run token while
    /// blocked throws OperationCanceledException — like a real invoker.</summary>
    public TaskCompletionSource EnqueueGatedScript(ChatChunk[] before, ChatChunk[] after)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _scripts.Enqueue((_, ct) => GatedRun(before, gate, after, ct));
        return gate;
    }

    /// <summary>Every workspace request, so task-chat tests can assert the task scope and branch.</summary>
    public ConcurrentQueue<(Ulid ProjectId, Ulid? TaskId, IReadOnlyList<ChatWorkspaceRepo> Repos)> WorkspaceRequests { get; } = new();

    /// <summary>Set to make the route unavailable (an offline runner) — see GetRouteBlockerAsync.</summary>
    public string? RouteBlocker { get; set; }

    public Task<string?> GetRouteBlockerAsync(CancellationToken ct = default) => Task.FromResult(RouteBlocker);

    public Task<ChatWorkspacePreparation> EnsureChatWorkspaceAsync(
        Ulid orgId, Ulid projectId, IReadOnlyList<ChatWorkspaceRepo> repos,
        Ulid? taskId = null, CancellationToken ct = default)
    {
        WorkspaceRequests.Enqueue((projectId, taskId, repos));
        return Task.FromResult(ChatWorkspacePreparation.Complete(
            taskId is { } t ? $"fake-task-chat-workspace/{t}" : $"fake-chat-workspace/{projectId}"));
    }

    public IAsyncEnumerable<ChatChunk> InvokeAgentAsync(
        string workspacePath, string systemPrompt, string userMessage,
        string model, string? resumeSessionId = null, Ulid? conversationId = null,
        IChatToolServer? toolServer = null,
        ChatToolServerContext? toolContext = null,
        CancellationToken ct = default)
    {
        Invocations.Enqueue(new Invocation(workspacePath, systemPrompt, userMessage, model, resumeSessionId, conversationId, toolServer));

        if (_scripts.TryDequeue(out var script))
            return script(toolServer, ct);

        return ToAsyncEnumerable(
        [
            new ChatChunk(ChatChunkType.Text, "Hello from the fake agent."),
            new ChatChunk(ChatChunkType.Done, "", PromptTokens: 10, CompletionTokens: 5),
        ]);
    }

    private static async IAsyncEnumerable<ChatChunk> ToAsyncEnumerable(ChatChunk[] chunks)
    {
        foreach (var chunk in chunks)
        {
            yield return chunk;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<ChatChunk> GatedRun(
        ChatChunk[] before, TaskCompletionSource gate, ChatChunk[] after,
        [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var chunk in before)
        {
            yield return chunk;
            await Task.Yield();
        }

        await gate.Task.WaitAsync(ct);

        foreach (var chunk in after)
            yield return chunk;
    }
}
