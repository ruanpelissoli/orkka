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
}

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
    string? Reason);

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
