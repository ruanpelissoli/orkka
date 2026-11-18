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
