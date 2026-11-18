namespace Orkka.Modules.Documents.Contracts;

public sealed record ChatDocumentSpec(string Title, string Path, string Content);

public enum ChatDocumentOutcome
{
    /// <summary>A new document row was inserted (revision 1).</summary>
    Created,

    /// <summary>An existing same-title+path document was revised (CurrentVersion bumped).</summary>
    Updated,

    /// <summary>An existing same-title+path document already had identical content — no new revision.</summary>
    Unchanged,
}

public sealed record CreatedChatDocument(
    Ulid Id, string Title, int Version = 1, ChatDocumentOutcome Outcome = ChatDocumentOutcome.Created);

public sealed record ChatDocumentLink(Ulid SourceMessageId, Ulid Id, string Title);

public sealed record DocumentContent(Ulid Id, string Title, string Path, string Content);

/// <summary>Light listing projection for agent tools — content stays out on purpose.
/// <paramref name="IsFile"/> marks an uploaded file (PDF today) rather than a markdown body:
/// its Content column is empty by design, so callers that reason over markdown must skip it.</summary>
public sealed record DocumentSummary(
    Ulid Id, string Title, string Path, int CurrentVersion, DateTimeOffset UpdatedAt,
    bool IsFile = false);

/// <summary>One document a completed task's documentation pass wants written.
/// <paramref name="DocumentId"/> null = create; set = revise that document. Updates target an
/// id rather than title+path because a rewrite may legitimately change both.</summary>
public sealed record TaskDocumentSpec(Ulid? DocumentId, string Title, string Path, string Content);

/// <summary>A document a task created or revised, for the task page's documentation card.</summary>
public sealed record TaskDocumentLink(
    Ulid Id, string Title, string Path, int Version, ChatDocumentOutcome Outcome);

public interface IDocumentsApi
{
    /// <summary>Persists documents extracted from an agent chat message. Upserts by title+path:
    /// a spec matching an existing document revises it in place (Content updated, CurrentVersion
    /// bumped, revision snapshot appended) instead of inserting a duplicate row; identical
    /// content is a no-op (no revision burned). New documents are created with revision 1.
    /// Specs are expected pre-sanitized by the caller. <paramref name="requesterUserId"/> is the
    /// user id of the chat user whose message prompted the agent — recorded on the revision
    /// as "who asked".</summary>
    Task<IReadOnlyList<CreatedChatDocument>> CreateChatDocumentsAsync(
        Ulid orgId, Ulid projectId, Ulid agentId, Ulid conversationId, Ulid messageId,
        string requesterUserId, IReadOnlyList<ChatDocumentSpec> specs, CancellationToken ct = default);

    /// <summary>Persists documents produced by the project-briefing flow (brief + PRD).
    /// No chat provenance — Source = Brief with null source ids. Idempotent by title+path:
    /// specs matching an existing document are skipped so generation retries don't
    /// duplicate. <paramref name="requesterUserId"/> is the project creator, recorded on
    /// the revision as "who asked".</summary>
    Task<IReadOnlyList<CreatedChatDocument>> CreateBriefDocumentsAsync(
        Ulid orgId, Ulid projectId, string? requesterUserId,
        IReadOnlyList<ChatDocumentSpec> specs, CancellationToken ct = default);

    /// <summary>Documents created OR revised from the given chat messages — used to decorate
    /// the conversation history with document cards on reload. Creation provenance lives on
    /// the document row; update provenance on revision rows — both are matched. Scoped to a
    /// project — the message-id match happens in memory, never as a Contains over the
    /// nullable, converter-mapped SourceMessageId column.</summary>
    Task<IReadOnlyList<ChatDocumentLink>> GetChatCreatedDocumentsAsync(
        Ulid projectId, IReadOnlyCollection<Ulid> sourceMessageIds, CancellationToken ct = default);

    /// <summary>Full content of the given documents for prompt injection. Callable from the
    /// Worker (no ICurrentUser → tenant filter bypassed), so org/project are filtered explicitly.
    /// Missing/deleted ids are silently omitted.</summary>
    Task<IReadOnlyList<DocumentContent>> GetDocumentsContentAsync(
        Ulid orgId, Ulid projectId, IReadOnlyCollection<Ulid> documentIds, CancellationToken ct = default);

    /// <summary>All documents in the project, path/title ordered, content omitted. Callable
    /// from scopes without an ICurrentUser (agent chat tool handlers), so org/project are
    /// filtered explicitly — those arguments are the isolation boundary.</summary>
    Task<IReadOnlyList<DocumentSummary>> ListDocumentSummariesAsync(
        Ulid orgId, Ulid projectId, CancellationToken ct = default);

    /// <summary>Persists the documents a finished task's documentation pass produced. A spec with
    /// no <see cref="TaskDocumentSpec.DocumentId"/> creates a document (Source = Task, revision 1);
    /// one with an id revises that document in place. Identical content is a no-op — a pass that
    /// decided nothing really changed must not burn a version. File documents (PDFs) are refused:
    /// their body is stored bytes, not markdown. Every write records
    /// <paramref name="taskId"/> on the revision, and on the document row when it creates it.
    /// Callable from the Worker (no ICurrentUser → tenant filter bypassed), so org/project are
    /// the isolation boundary.</summary>
    Task<IReadOnlyList<CreatedChatDocument>> SyncTaskDocumentsAsync(
        Ulid orgId, Ulid projectId, Ulid taskId, Ulid agentId, string? requesterUserId,
        IReadOnlyList<TaskDocumentSpec> specs, CancellationToken ct = default);

    /// <summary>Documents this task created or revised, for the task page. Creation provenance
    /// lives on the document row and update provenance on revision rows — both are matched, the
    /// same union <see cref="GetChatCreatedDocumentsAsync"/> performs for chat messages.</summary>
    Task<IReadOnlyList<TaskDocumentLink>> GetTaskDocumentLinksAsync(
        Ulid projectId, Ulid taskId, CancellationToken ct = default);
}
