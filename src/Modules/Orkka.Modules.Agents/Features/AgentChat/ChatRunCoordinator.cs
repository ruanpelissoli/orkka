namespace Orkka.Modules.Agents.Features.AgentChat;

using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orkka.Modules.Agents.Contracts;

/// <summary>
/// Everything a chat turn needs, captured as plain data at the endpoint (the run executes
/// in its own DI scope after the request scope is gone, so nothing request-bound may leak
/// in). Both system-prompt variants are precomputed: the runner falls back to
/// <see cref="FreshSystemPrompt"/> when resuming <see cref="ResumeSessionId"/> fails —
/// they are the same string when there is no session to resume.
/// </summary>
public sealed record ChatTurnSpec(
    Ulid OrgId,
    string RequesterUserId,
    string? RequesterLocale,
    Ulid ProjectId,
    Ulid AgentId,
    Ulid ConversationId,
    AgentRole AgentRole,
    string AgentName,
    string Model,
    string WorkspacePath,
    string UserMessage,
    string SystemPrompt,
    string FreshSystemPrompt,
    string? ResumeSessionId,
    bool ConversationHasTitle,
    // Linked documents/attachments expanded for THIS turn — appended to the agent's copy
    // of the user message, never persisted (the visible markers in UserMessage are the
    // durable record).
    string? ContextBlock = null,
    // Platform tools (create documents/tasks/diagrams/designs as MCP tool calls) for this
    // turn. Decided by the endpoint: Anthropic provider + cloud route + feature flag. Off,
    // the turn uses the legacy fenced-block prompts and post-stream extraction.
    bool ToolsEnabled = false,
    // A project skill the user invoked with /slug (or a schedule ran as a playbook):
    // platform-authored block placed BEFORE the user's message on the agent's copy. Never
    // persisted — the visible message keeps the raw command.
    string? InvokedSkillBlock = null,
    // Repos that could not be put in the workspace, already worded for a human. Emitted to
    // the client before the agent starts and never sent to the agent: the prompt's job is
    // to describe the folder it really has, and a repo it cannot read has simply been left
    // out of it. Null when the whole codebase made it in, which is the normal case.
    string? WorkspaceNotice = null,
    // The gallery folder open when the turn started (Design page). New asset lineages are
    // filed here; an iteration still follows the asset it is based on.
    string? DesignFolderPath = null,
    // A task's Discuss turn: its usage is attributed to the task.
    Ulid? TaskId = null);

/// <summary>
/// Live handle to a detached chat run. The run emits SSE payload objects into the channel;
/// the (single) SSE observer relays them until it disconnects — after which the run keeps
/// going and only <see cref="Cancel"/> (user action) or host shutdown stops it.
/// </summary>
public sealed class ChatRunHandle
{
    private readonly CancellationTokenSource _cts;
    private readonly Channel<object> _events = Channel.CreateUnbounded<object>(
        new UnboundedChannelOptions { SingleReader = true });
    private volatile bool _detached;

    internal ChatRunHandle(CancellationToken hostStopping)
        => _cts = CancellationTokenSource.CreateLinkedTokenSource(hostStopping);

    /// <summary>Cancelled by an explicit user cancel or host shutdown — never by the SSE
    /// observer disconnecting.</summary>
    public CancellationToken Token => _cts.Token;

    public ChannelReader<object> Events => _events.Reader;

    /// <summary>True once the SSE observer stopped relaying (browser disconnect). The run
    /// checks this at completion to decide whether a bell notification is needed.</summary>
    public bool IsDetached => _detached;

    public void MarkDetached() => _detached = true;

    public void Cancel() => _cts.Cancel();

    internal void Emit(object payload) => _events.Writer.TryWrite(payload);

    internal void Complete()
    {
        _events.Writer.TryComplete();
        _cts.Dispose();
    }
}

/// <summary>
/// Starts and tracks detached agent-chat runs — one per conversation. Runs execute on a
/// background task in their own DI scope with a token linked only to host shutdown, so a
/// browser disconnect can neither cancel the agent invocation nor skip persistence.
/// In-memory by design: an SSE run lives and dies with the process that accepted it
/// (a marker column on the conversation covers cross-restart UI state).
/// </summary>
public sealed class ChatRunCoordinator(
    IServiceScopeFactory scopeFactory,
    IHostApplicationLifetime lifetime,
    ILogger<ChatRunCoordinator> logger)
{
    private readonly ConcurrentDictionary<Ulid, ChatRunHandle> _runs = new();

    /// <summary>Starts a run for the conversation, or returns null when one is already
    /// running (the caller should reject the message — concurrent turns in one
    /// conversation would interleave history).</summary>
    public ChatRunHandle? TryStartRun(ChatTurnSpec spec)
    {
        var handle = new ChatRunHandle(lifetime.ApplicationStopping);
        if (!_runs.TryAdd(spec.ConversationId, handle))
            return null;

        _ = Task.Run(() => ExecuteAsync(spec, handle));
        return handle;
    }

    public bool IsRunning(Ulid conversationId) => _runs.ContainsKey(conversationId);

    /// <summary>Explicit user cancel — flows into the invoker's token (the local edition's
    /// proxy turns it into a CancelRun op to the runner daemon).</summary>
    public bool Cancel(Ulid conversationId)
    {
        if (!_runs.TryGetValue(conversationId, out var handle))
            return false;
        handle.Cancel();
        return true;
    }

    private async Task ExecuteAsync(ChatTurnSpec spec, ChatRunHandle handle)
    {
        try
        {
            // Detached scope has no ICurrentUser — restore the requester ambient from
            // the spec so hybrid routing can target their runner.
            ChatRequesterContext.Set(spec.OrgId, spec.RequesterUserId);

            await using var scope = scopeFactory.CreateAsyncScope();
            var runner = scope.ServiceProvider.GetRequiredService<ChatTurnRunner>();
            await runner.RunAsync(spec, handle, handle.Token);
        }
        catch (Exception ex)
        {
            // The runner converts its own failures into SSE error events + a persisted
            // message; reaching here means even that failed — surface what we can.
            logger.LogError(ex, "Chat run crashed for conversation {ConversationId}", spec.ConversationId);
            handle.Emit(new { error = "The agent run failed unexpectedly.", done = true });
        }
        finally
        {
            _runs.TryRemove(spec.ConversationId, out _);
            handle.Complete();
        }
    }
}
