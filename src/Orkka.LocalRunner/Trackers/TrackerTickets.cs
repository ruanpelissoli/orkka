namespace Orkka.LocalRunner.Trackers;

using Orkka.LocalRunner.Contracts;

/// <summary>One attachment as the tracker lists it. <see cref="DownloadUrl"/> is fetched with
/// the connection's token, so it is only ever followed on the connection's own site.</summary>
public sealed record RawAttachment(string Id, string FileName, long Size, string? MimeType, string DownloadUrl);

/// <summary>One ticket as the tracker returned it: rich text still HTML, nothing capped.
/// <see cref="TicketNormalizer"/> turns it into the <see cref="TrackerTicket"/> the host gets.</summary>
public sealed record RawTicket(
    string Key,
    string Id,
    string Url,
    string? EpicKey,
    string Title,
    string? Type,
    string? Status,
    string? StatusCategory,
    string? Priority,
    string? DescriptionHtml,
    string? AcceptanceCriteriaHtml,
    string? ReproStepsHtml,
    string[] Labels,
    RawAttachment[] Attachments,
    DateTimeOffset? UpdatedAt)
{
    /// <summary>The direct parent (epic, story, feature) and its type name.</summary>
    public string? ParentKey { get; init; }
    public string? ParentType { get; init; }

    /// <summary>Direct children in the tracker's order (Jira's sub-task order, ADO's backlog rank).</summary>
    public string[] ChildKeys { get; init; } = [];

    /// <summary>Assigned to the developer whose token polled it.</summary>
    public bool AssignedToMe { get; init; }

    /// <summary>Matched the intake query (assigned to me, in an intake status, in scope).</summary>
    public bool InIntake { get; init; }

    /// <summary>Jira components (routing rules match them).</summary>
    public string[] Components { get; init; } = [];

    /// <summary>Azure DevOps area path (routing rules match it).</summary>
    public string? AreaPath { get; init; }

    /// <summary>GitHub: the issue's repository URL (repo routing matches it).</summary>
    public string? Repository { get; init; }

    /// <summary>The text fields are already markdown (GitHub), not HTML.</summary>
    public bool Markdown { get; init; }
}

/// <param name="BodyHtml">HTML, or markdown when <see cref="Markdown"/> is set (GitHub).</param>
public sealed record RawComment(string Id, string Author, DateTimeOffset CreatedAt, string BodyHtml)
{
    public bool Markdown { get; init; }
}

/// <summary>What the poller needs from a tracker: the complete ticket set of an assignment,
/// and a ticket's recent comments.</summary>
public interface ITrackerTicketSource
{
    /// <summary>Every ticket the intake query matches right now (<see cref="RawTicket.InIntake"/>),
    /// plus every tracked key (<see cref="TrackerPollAssignment.Known"/>) that still exists,
    /// whatever its status, plus the children of intake tickets. Never a partial set: a result
    /// that would be cut off by a paging cap throws instead, because a tracked key missing from
    /// a successful poll reads to the host as "this ticket no longer exists".</summary>
    Task<IReadOnlyList<RawTicket>> FetchTicketsAsync(TrackerCredential credential, TrackerPollAssignment assignment, CancellationToken ct);

    /// <summary>The newest comments (at most <paramref name="max"/>) created after
    /// <paramref name="after"/>, oldest first.</summary>
    Task<IReadOnlyList<RawComment>> FetchCommentsAsync(TrackerCredential credential, RawTicket ticket,
        DateTimeOffset? after, int max, CancellationToken ct);
}

/// <summary>The one thing Orkka writes to a tracker: a ticket's status.</summary>
public interface ITrackerStatusWriter
{
    /// <summary>Moves the ticket to <paramref name="targetStatus"/>. Returns false when it was
    /// already there (nothing written), true when it moved; throws <see cref="TrackerException"/>
    /// with words the user can act on when it cannot.</summary>
    Task<bool> TransitionAsync(TrackerCredential credential, string ticketKey, string ticketId, string targetStatus, CancellationToken ct);

    /// <summary>The move as the host handed it — GitHub needs its project and status field too.</summary>
    Task<bool> TransitionAsync(TrackerCredential credential, TrackerTransition transition, CancellationToken ct)
        => TransitionAsync(credential, transition.TicketKey, transition.TicketId, transition.TargetStatus, ct);
}
