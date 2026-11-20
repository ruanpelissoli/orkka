namespace Orkka.Modules.Trackers.Contracts;

using Orkka.LocalRunner.Contracts;

/// <summary>
/// Cross-module surface of the Trackers module: where the Jira / Azure DevOps tickets that drive
/// a project come from. Orkka stores which connection a project uses and how its epics map onto
/// repositories; the connection's token never leaves the developer's machine — the runner holds
/// it and reports only metadata.
/// </summary>
public interface ITrackersApi
{
    /// <summary>
    /// Records the tracker connections a runner reported in its Hello — the whole set held on
    /// <paramref name="machineName"/>, so a connection removed there (<c>./orkka tracker
    /// remove</c>) disappears here too. Connections on the user's other machines are untouched.
    /// </summary>
    Task UpsertRunnerConnectionsAsync(
        Ulid orgId, string userId, string machineName,
        IReadOnlyList<RunnerTrackerConnection> connections, CancellationToken ct = default);

    /// <summary>The runner asks which of its owner's ticket sources to poll now; taking one
    /// takes its poll lease. <paramref name="supersedesOtherInstances"/>: the requester is the
    /// owner's ACTIVE runner connection, so a lease held by any other instance belongs to a
    /// process it replaced (a restart) and is taken over at once instead of waiting it out.</summary>
    Task<TrackerSyncResponse> SyncAsync(
        Ulid orgId, string userId, TrackerSyncRequest request, CancellationToken ct = default,
        bool supersedesOtherInstances = false);

    /// <summary>One poll's tickets: new ones become tasks, changed ones update theirs, missing
    /// ones are retired. Rejected when the reporter no longer holds the lease.</summary>
    Task<TrackerReportAck> ReportAsync(
        Ulid orgId, string userId, TrackerTicketReport report, CancellationToken ct = default);

    /// <summary>The status moves and pull-request checks the runner did after a sync.</summary>
    Task<TrackerReportAck> AckTransitionsAsync(
        Ulid orgId, string userId, TrackerTransitionReport report, CancellationToken ct = default);

    /// <summary>The ticket a task was imported from — for its prompt and its attachments. Null for
    /// tasks that did not come from a tracker.</summary>
    Task<TaskTicketContext?> GetTicketContextAsync(Ulid taskId, CancellationToken ct = default);

    /// <summary>The poll the project's ticket source would be handed now (its tracked keys
    /// included), for a dry run on the owner's runner. Read-only: no lease, nothing ended. Null
    /// when the project has no ticket source.</summary>
    Task<TrackerPreviewAssignment?> BuildPreviewAssignmentAsync(Ulid projectId, CancellationToken ct = default);

    /// <summary>What a poll returning <paramref name="tickets"/> would add to the board — decided by
    /// exactly the rules the real poll uses (Stories and their members, plain tasks), routed
    /// without the repo router, and creating nothing. Null when the project has no ticket source.</summary>
    Task<TrackerIntakePreview?> PreviewIntakeAsync(
        Ulid projectId, IReadOnlyList<TrackerTicket> tickets, CancellationToken ct = default);

    // ── Importing a ticket by its key ───────────────────────────────────

    /// <summary>The keys-only read of <paramref name="keys"/> through the project's ticket source,
    /// for its owner's runner (the import preview). Null when the project has no ticket source.</summary>
    Task<TrackerPreviewAssignment?> BuildImportAssignmentAsync(Ulid projectId, IReadOnlyList<string> keys, CancellationToken ct = default);

    /// <summary>The tracker kind of the project's ticket source (jira / ado / github), or null.</summary>
    Task<string?> SourceKindAsync(Ulid projectId, CancellationToken ct = default);

    /// <summary>A key as the developer typed or pasted it (a key, an id, a ticket URL) in the form
    /// the source's tracker uses (SHOP-10, 4567, owner/repo#12); null when it is not one.</summary>
    string? NormalizeTicketKey(string kind, string input);

    /// <summary>What importing <paramref name="key"/> would create, from the tickets the keys-only
    /// read returned (the ticket and its sub-tasks). Creates nothing.</summary>
    Task<TrackerImportPreview?> PreviewImportAsync(Ulid projectId, string key, IReadOnlyList<TrackerTicket> tickets, CancellationToken ct = default);

    /// <summary>Queues the import of <paramref name="key"/> with the picked sub-tasks for the next
    /// poll (which is made due now). A ticket already on the board is pinned and answered at once.</summary>
    Task<TrackerImportResult> RequestImportAsync(Ulid projectId, string userId, string key,
        IReadOnlyList<string> subTaskKeys, CancellationToken ct = default);

    /// <summary>An import request and how it went; null when unknown in this project.</summary>
    Task<TrackerImportStatus?> GetImportAsync(Ulid projectId, Ulid importId, CancellationToken ct = default);
}

/// <summary>
/// Importing a ticket by its key (or link), for the board's "Import ticket" dialog and the MCP
/// tools alike: the preview reads the ticket and its sub-tasks on the source owner's runner; the
/// import is queued for the next poll.
/// </summary>
public interface ITrackerImports
{
    Task<TrackerImportPreviewOutcome> PreviewAsync(Ulid orgId, string userId, Ulid projectId, string input, CancellationToken ct = default);

    Task<TrackerImportResult> RequestAsync(Ulid projectId, string userId, string key, IReadOnlyList<string> subTaskKeys,
        CancellationToken ct = default);

    Task<TrackerImportStatus?> GetAsync(Ulid projectId, Ulid importId, CancellationToken ct = default);
}

/// <summary>Supported=false: the owner's runner is offline (or too old). Ok=false: refused — Error
/// says why (not found, an epic, not this tracker's key, no ticket source).</summary>
public sealed record TrackerImportPreviewOutcome(
    bool Supported, bool Ok, string? Error = null, TrackerImportPreview? Preview = null);

/// <summary>A ticket and its sub-tasks as the import dialog shows them before anything is created.</summary>
/// <param name="Level">"above" (refused: an epic or feature), "story", "subTask", "unknown".</param>
/// <param name="ExistingTaskId">Already on the board: the task (a Story's first member).</param>
/// <param name="WillBe">"story" when sub-tasks are picked, else "task" — with the default picks.</param>
public sealed record TrackerImportPreview(
    string Kind,
    string Key,
    string Title,
    string? Type,
    string? Status,
    string? Url,
    string Level,
    bool AssignedToMe,
    string Description,
    TrackerAttachmentMeta[] Attachments,
    TrackerImportSubTask[] SubTasks,
    string? RepoName,
    string Route,
    string? ParentKey,
    Ulid? ExistingTaskId,
    string WillBe);

/// <param name="Selected">Ticked by default: not done and not on the board yet.</param>
/// <param name="Found">False when the tracker no longer has it (or this account cannot see it).</param>
public sealed record TrackerImportSubTask(
    string Key,
    string Title,
    string? Status,
    string? StatusCategory,
    bool AssignedToMe,
    bool Selected,
    bool Found,
    Ulid? ExistingTaskId);

/// <param name="State">"pending" (waiting for the poll), "done" or "failed".</param>
public sealed record TrackerImportStatus(
    Ulid Id,
    string TicketKey,
    string[] SubTaskKeys,
    string State,
    Ulid? TaskId,
    string? Error,
    DateTimeOffset RequestedAt,
    DateTimeOffset? CompletedAt);

/// <summary>Either the queued (or already answered) import, or why it was refused.</summary>
public sealed record TrackerImportResult(TrackerImportStatus? Import, string? Error = null, int StatusCode = 200);

/// <param name="OwnerUserId">The source's owner — only their runner holds the token.</param>
/// <param name="ConnectionId">The connection row's id.</param>
public sealed record TrackerPreviewAssignment(string OwnerUserId, Ulid ConnectionId, TrackerPollAssignment Assignment);

/// <summary>The dry run of one poll: nothing here was created.</summary>
/// <param name="NewTaskCount">Tasks the poll would create: the plain ones plus every Story member.</param>
/// <param name="Tasks">Tickets that would become a task of their own, in import order.</param>
/// <param name="Stories">Tickets that would become a Story, with their member sub-tasks in order.</param>
/// <param name="AlreadyTracked">Tickets read that the board already has.</param>
/// <param name="Skipped">Tickets read (sub-tasks of a Story, mostly) that would not be picked up, and why.</param>
public sealed record TrackerIntakePreview(
    int NewTaskCount,
    TrackerPreviewTicket[] Tasks,
    TrackerPreviewStory[] Stories,
    TrackerPreviewTicket[] AlreadyTracked,
    TrackerPreviewSkipped[] Skipped);

/// <param name="Assignee">The developer's name in the tracker (every intake ticket is theirs).</param>
/// <param name="RepoId">The repository the routing rules pick; null when the repo router decides
/// at import (<paramref name="Route"/> "router-at-import") or nothing can.</param>
/// <param name="SpansRepos">Several repositories (a cross-repo group); empty otherwise.</param>
/// <param name="Route">How the repository was picked: rule:epic / rule:label / …, single-repo,
/// issue-repo, default, router-at-import, none; "story" for a Story itself, "tracked" for one the
/// board already has.</param>
public sealed record TrackerPreviewTicket(
    string Key,
    string Title,
    string? Type,
    string? Status,
    string? Url,
    string? Assignee,
    Ulid? RepoId,
    string? RepoName,
    string[] SpansRepos,
    string Route,
    string? Reason,
    // The ticket's level in the tracker: "above" (epic, feature), "story", "subTask", "unknown".
    string Level = "unknown");

/// <param name="Branch">The one branch the Story's members are worked on.</param>
public sealed record TrackerPreviewStory(TrackerPreviewTicket Story, string Branch, TrackerPreviewTicket[] Members);

/// <param name="Reason">A <see cref="TrackerPreviewSkipReasons"/> value.</param>
public sealed record TrackerPreviewSkipped(string Key, string Title, string? Status, string? Url, string Reason);

public static class TrackerPreviewSkipReasons
{
    /// <summary>Assigned to someone else, or to nobody — Orkka only works the developer's tickets.</summary>
    public const string NotAssignedToMe = "notAssignedToMe";

    /// <summary>Not in an intake status (already under way, in review, done).</summary>
    public const string NotInIntake = "notInIntake";

    /// <summary>Above a story (an epic, a feature): Orkka works the stories under it, never the
    /// container itself.</summary>
    public const string AboveStory = "aboveStory";
}

/// <param name="ConnectionKey">The runner's id for the connection holding the cached attachments.</param>
/// <param name="Subpath">The folder inside the repository the ticket's epic is mapped to.</param>
public sealed record TaskTicketContext(
    string Kind,
    string TicketKey,
    string TicketUrl,
    string? EpicKey,
    string? EpicTitle,
    string? Subpath,
    string ConnectionKey,
    IReadOnlyList<TrackerAttachmentMeta> Attachments,
    // The ticket source's branch template ({type}, {key}, {slug}); null when unset.
    string? BranchTemplate = null,
    // A sub-task worked on its own (its Story is someone else's, or the Story's pull request is
    // already closed): the Story it belongs to, for the "Parent story context" prompt section.
    string? ParentKey = null,
    string? ParentTitle = null,
    string? ParentDescription = null);
