namespace Orkka.Modules.Agents.Features.AgentChat.Tools;

using Orkka.Modules.Agents.Contracts;

/// <summary>
/// Server-side identity and plumbing of ONE chat turn, captured by ChatTurnRunner from the
/// authenticated turn spec. Tool handlers scope every read/write to these ids — they are
/// never tool inputs, so the model cannot address another org/project/conversation.
/// The created-artifact counters feed the runner's double-create guard (post-stream
/// fenced-block extraction is skipped for kinds the tools already created) and the
/// deliverables total. Counters are thread-safe: the model can batch parallel tool calls.
/// </summary>
internal sealed class ChatTurnToolContext
{
    public required Ulid OrgId { get; init; }
    public required Ulid ProjectId { get; init; }
    public required Ulid AgentId { get; init; }
    public required Ulid ConversationId { get; init; }

    /// <summary>Pre-allocated id of the assistant message being streamed — the message row
    /// does not exist yet while tools run, but artifacts link to it via SourceMessageId.</summary>
    public required Ulid AssistantMessageId { get; init; }

    public required string RequesterUserId { get; init; }
    public string? RequesterLocale { get; init; }
    public required AgentRole AgentRole { get; init; }

    /// <summary>Where the user is looking in the gallery, when the turn came from the Design
    /// page. New asset lineages are filed here rather than at the root.</summary>
    public string? DesignFolderPath { get; init; }

    /// <summary>Emits an SSE payload into the run handle's channel (thread-safe).</summary>
    public required Action<object> Emit { get; init; }

    /// <summary>Serializes (and remembers) the placeholder insert of the assistant message
    /// row — Agents-module artifacts (diagrams, design assets) have real FKs to
    /// agent_messages, and the streamed reply's row doesn't exist until post-stream save.
    /// The model can batch parallel tool calls, hence the gate.</summary>
    public SemaphoreSlim MessageRowGate { get; } = new(1, 1);

    public bool MessageRowEnsured { get; set; }

    private int _documents;
    private int _tasks;
    private int _diagrams;
    private int _designAssets;

    public int CreatedDocuments => Volatile.Read(ref _documents);
    public int CreatedTasks => Volatile.Read(ref _tasks);
    public int CreatedDiagrams => Volatile.Read(ref _diagrams);
    public int CreatedDesignAssets => Volatile.Read(ref _designAssets);

    public int TotalCreated => CreatedDocuments + CreatedTasks + CreatedDiagrams + CreatedDesignAssets;

    public void AddCreatedDocuments(int count) => Interlocked.Add(ref _documents, count);
    public void AddCreatedTasks(int count) => Interlocked.Add(ref _tasks, count);
    public void AddCreatedDiagrams(int count) => Interlocked.Add(ref _diagrams, count);
    public void AddCreatedDesignAssets(int count) => Interlocked.Add(ref _designAssets, count);
}
