namespace Orkka.LocalRunner.Contracts;

/// <summary>The trackers a runner can hold a connection to.</summary>
public static class TrackerKinds
{
    /// <summary>Jira Cloud (<c>https://{site}.atlassian.net</c>), email + API token.</summary>
    public const string Jira = "jira";

    /// <summary>Azure DevOps Services (<c>https://dev.azure.com/{org}</c>), personal access token.</summary>
    public const string AzureDevOps = "ado";
}

/// <summary>
/// One tracker connection held on the developer's machine, as the runner reports it in its
/// Hello. Metadata only: the token lives in the machine's secret store and never travels.
/// </summary>
/// <param name="Id">Stable on this machine (e.g. <c>jira-acme</c>); the handle every
/// <see cref="TrackerQueryRequest"/> and project configuration uses.</param>
/// <param name="Kind">A <see cref="TrackerKinds"/> value.</param>
/// <param name="SiteUrl">Jira: <c>https://acme.atlassian.net</c>. Azure DevOps: <c>https://dev.azure.com/acme</c>.</param>
/// <param name="AccountId">The tracker's id for the signed-in user (Jira accountId, ADO identity id).</param>
/// <param name="Healthy">Whether the last check with the stored token succeeded.</param>
/// <param name="HealthDetail">Why not, in words the settings page can show.</param>
public sealed record RunnerTrackerConnection(
    string Id,
    string Kind,
    string SiteUrl,
    string? AccountId,
    string? DisplayName,
    bool Healthy,
    string? HealthDetail,
    DateTimeOffset? CheckedAt);

/// <summary>What a <see cref="TrackerQueryRequest"/> asks.</summary>
public static class TrackerQueryKinds
{
    /// <summary>Checks the token now; answers <see cref="TrackerQueryResult.Connection"/>.</summary>
    public const string Test = "test";

    /// <summary>Jira projects / Azure DevOps projects; answers <see cref="TrackerQueryResult.Scopes"/>.</summary>
    public const string Scopes = "scopes";

    /// <summary>Epics in a scope matching <see cref="TrackerQueryRequest.Search"/> (title text or key).</summary>
    public const string Epics = "epics";

    /// <summary>The workflow statuses tickets in a scope can move to — what the status map picks from.</summary>
    public const string Statuses = "statuses";

    /// <summary>How many tickets the configuration would pick up right now, with a few examples.</summary>
    public const string Preview = "preview";

    /// <summary>The boards of a scope — Jira agile boards of the project, Azure DevOps teams (a
    /// team's board is its area paths); answers <see cref="TrackerQueryResult.Boards"/>.</summary>
    public const string Boards = "boards";
}

/// <param name="ConnectionId">A <see cref="RunnerTrackerConnection.Id"/> on this runner.</param>
/// <param name="Scope">Jira project key / Azure DevOps project name. Required for epics,
/// statuses and preview.</param>
/// <param name="Search">Epics: text in the title, or an exact key/id.</param>
/// <param name="EpicKeys">Preview: the configured epics.</param>
/// <param name="ExtraClause">Preview: the project's optional JQL (Jira) or WIQL condition (ADO),
/// AND-ed onto the generated query.</param>
/// <param name="IncludeDescendants">Preview: grandchildren too (stories under features under
/// epics), not only direct children.</param>
/// <param name="IntakeStatuses">Preview: the statuses a ticket assigned to the developer must be
/// in to be picked up (empty = the tracker's "to do" category).</param>
/// <param name="ScopeMode">Preview: a <see cref="TrackerScopeModes"/> value — every ticket in the
/// scope, or only those under <see cref="EpicKeys"/>.</param>
/// <param name="BoardId">Preview: the board intake is limited to (a Jira board id, an Azure
/// DevOps team id); null = every board in the scope.</param>
/// <param name="BoardName">Preview: the board's name, for the error when it is gone.</param>
public sealed record TrackerQueryRequest(
    string ConnectionId,
    string Kind,
    string? Scope = null,
    string? Search = null,
    string[]? EpicKeys = null,
    string? ExtraClause = null,
    bool IncludeDescendants = false,
    string[]? IntakeStatuses = null,
    string? ScopeMode = null,
    string? BoardId = null,
    string? BoardName = null);

/// <summary>Which tickets of a scope a source picks up.</summary>
public static class TrackerScopeModes
{
    /// <summary>Every ticket in the scope assigned to the developer in an intake status.</summary>
    public const string All = "all";

    /// <summary>Only tickets under the configured epics.</summary>
    public const string Epics = "epics";

    public static bool IsEpics(string? mode) => string.Equals(mode, Epics, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Exactly one of the payload fields is set, matching the request's kind. A failure
/// carries the tracker's own explanation (a bad token, a JQL syntax error) in <see cref="Error"/>.</summary>
public sealed record TrackerQueryResult(
    bool Ok,
    string? Error = null,
    RunnerTrackerConnection? Connection = null,
    TrackerScope[]? Scopes = null,
    TrackerEpic[]? Epics = null,
    TrackerStatus[]? Statuses = null,
    int? MatchCount = null,
    TrackerTicketPreview[]? Sample = null,
    // The query the preview actually ran (JQL / WIQL), so the settings page can show it.
    string? EffectiveQuery = null,
    TrackerBoard[]? Boards = null);

public sealed record TrackerScope(string Key, string Name);

/// <summary>A board a ticket source can be limited to.</summary>
/// <param name="Id">Jira: the agile board id. Azure DevOps: the team id.</param>
/// <param name="Name">Jira: the board's name. Azure DevOps: the team's name.</param>
/// <param name="Type">Jira: "scrum" / "kanban" / "simple"; Azure DevOps: null.</param>
public sealed record TrackerBoard(string Id, string Name, string? Type = null);

public sealed record TrackerEpic(string Key, string Title, string? Status, string? Url);

/// <param name="Category">"todo", "inprogress" or "done" — Jira's status category, or the
/// Azure DevOps state category mapped onto the same three.</param>
/// <param name="TrackerCategory">The tracker's own category name when it says more than the
/// three above — Azure DevOps' "Proposed", "InProgress", "Resolved", "Completed". What the
/// default status map picks "PR opened" from (the first Resolved state).</param>
public sealed record TrackerStatus(string Name, string? Category, string? TrackerCategory = null);

public sealed record TrackerTicketPreview(
    string Key, string Title, string? Type, string? Status, string? Url);

// ── Polling (runner → host) ─────────────────────────────────────────────────

/// <summary>
/// The runner asking which ticket sources it should poll now. The host answers with the
/// projects whose owner is this runner's user, whose connection this machine holds, which are
/// due, and whose poll lease this runner now holds — one machine polls a project at a time.
/// </summary>
/// <param name="ConnectionIds">The <see cref="RunnerTrackerConnection.Id"/>s this machine holds.</param>
/// <param name="InstanceId">The runner process's id (the Hello's InstanceId), the lease holder.</param>
/// <param name="Features">What this runner's poll understands beyond protocol v5
/// (<see cref="TrackerSyncFeatures"/>). The host never hands a source to a runner that would
/// ignore part of it — an old runner ignoring <see cref="TrackerPollAssignment.BoardId"/> would
/// silently widen intake to the whole project.</param>
public sealed record TrackerSyncRequest(string[] ConnectionIds, string InstanceId, string[]? Features = null);

/// <summary>Values of <see cref="TrackerSyncRequest.Features"/>.</summary>
public static class TrackerSyncFeatures
{
    /// <summary>Honours <see cref="TrackerPollAssignment.BoardId"/>: intake limited to one board.</summary>
    public const string Boards = "boards";
}

/// <param name="Transitions">Ticket status moves due on this runner's connections — the only
/// thing Orkka ever writes to a tracker. Applied with the developer's token, then acknowledged
/// with <c>AckTransitions</c>.</param>
/// <param name="PrChecks">Published forge pull requests whose merge this runner should look up
/// with the developer's CLI, so a "Merged" status can follow.</param>
public sealed record TrackerSyncResponse(
    TrackerPollAssignment[] Assignments,
    TrackerTransition[]? Transitions = null,
    TrackerPrCheck[]? PrChecks = null);

/// <summary>
/// Move one ticket to <see cref="TargetStatus"/>. Idempotent on the tracker side: a ticket already
/// in that status is a success. Jira moves through the workflow transition whose destination
/// status has that name; Azure DevOps sets <c>System.State</c>.
/// </summary>
/// <param name="TransitionId">Echoed on the ack.</param>
/// <param name="Phase">"Started", "PrOpened" or "Merged" — informational, for the runner's log.</param>
public sealed record TrackerTransition(
    string TransitionId,
    string ConnectionId,
    string TicketKey,
    string TicketId,
    string TargetStatus,
    string Phase);

/// <summary>A published pull request to look up with the developer's forge CLI.</summary>
/// <param name="ForgeKind">"github", "azuredevops", "gitlab" (ForgeKind, lower case).</param>
public sealed record TrackerPrCheck(
    string CheckId,
    string RepoUrl,
    string ForgeKind,
    string? CliAccount,
    string PullRequestUrl);

/// <summary>What the runner did with a sync's transitions and pull-request checks.</summary>
public sealed record TrackerTransitionReport(
    string InstanceId,
    TrackerTransitionAck[] Transitions,
    TrackerPrState[] PrStates);

/// <param name="Error">Why the move failed, in the tracker's words — a workflow with no
/// transition to that status, a missing permission. Never contains the token.</param>
public sealed record TrackerTransitionAck(string TransitionId, bool Ok, string? Error = null);

/// <param name="State">"open", "merged" or "closed"; null with an error when the CLI could not say.</param>
public sealed record TrackerPrState(string CheckId, string? State, string? Error = null);

/// <summary>One ticket source to poll now. Everything the query needs; nothing about Orkka's tasks.</summary>
/// <param name="ConfigId">Echoed back on <see cref="TrackerTicketReport"/>.</param>
/// <remarks>
/// Two queries per poll. <b>Intake</b>: tickets assigned to the developer, in one of
/// <see cref="IntakeStatuses"/>, in the scope (and under <see cref="EpicKeys"/> when
/// <see cref="ScopeMode"/> is <see cref="TrackerScopeModes.Epics"/>), plus the extra clause —
/// what new tasks are made from. <b>Tracking</b>: every key in <see cref="Known"/>, whatever
/// its status or assignee, so a ticket that moved to In Progress keeps syncing its comments
/// and status. The epics are sent in both modes: in "all" mode they only say which epic a
/// ticket sits under (routing), not whether it is picked up. <see cref="BoardId"/> narrows
/// intake only, like the extra clause: a tracked ticket that leaves the board keeps syncing.
/// A board that no longer exists fails the poll — it never widens to the whole scope.
/// </remarks>
public sealed record TrackerPollAssignment(
    string ConfigId,
    string ConnectionId,
    string Kind,
    string Scope,
    string[] EpicKeys,
    string? ExtraClause,
    bool IncludeDescendants,
    // Tickets the host tracks, key → last content hash it stored: the runner reads them by key
    // (any status), skips re-sending unchanged bodies, and only fetches comments newer than the
    // host's last one.
    TrackerKnownTicket[] Known,
    string ScopeMode = TrackerScopeModes.All,
    // Empty = the tracker's "to do" category (Jira statusCategory "To Do", ADO Proposed states).
    string[]? IntakeStatuses = null,
    // The board intake is limited to (Jira board id: its filter's JQL is AND-ed on; Azure DevOps
    // team id: its area paths, read live each poll). Null = every board in the scope.
    string? BoardId = null,
    // The board's name, for the error when it is gone.
    string? BoardName = null);

public sealed record TrackerKnownTicket(string Key, string? ContentHash, DateTimeOffset? LastCommentAt);

/// <summary>
/// The result of one poll: every ticket the intake query matched (<see cref="TrackerTicket.InIntake"/>),
/// every tracked key that still exists (whatever its status), and the children of intake
/// tickets that have any. A tracked key missing from a successful report no longer exists for
/// this account — deleted, moved to another project, or no longer visible. A failed poll
/// (<see cref="Ok"/> false) says nothing about membership.
/// </summary>
public sealed record TrackerTicketReport(
    string ConfigId,
    string InstanceId,
    bool Ok,
    string? Error,
    DateTimeOffset PolledAt,
    TrackerTicket[] Tickets);

/// <summary>
/// One ticket, normalized by the runner. Text is markdown (converted from Jira's rendered HTML /
/// Azure DevOps' HTML), capped in size, with inline images rewritten to their attachment file
/// names. Attachments themselves stay on the developer's machine; only their metadata travels.
/// </summary>
/// <param name="Body">Null when unchanged since <see cref="TrackerKnownTicket.ContentHash"/> —
/// the key is still listed so membership stays exact.</param>
/// <param name="Status">The ticket's status now — always sent, even when the body is not.</param>
/// <param name="StatusCategory">"todo", "inprogress" or "done" (see <see cref="TrackerStatus.Category"/>).</param>
/// <param name="AssignedToMe">Assigned to the developer whose token polled it.</param>
/// <param name="InIntake">Matched the intake query — only these become new tasks.</param>
/// <param name="ParentKey">The direct parent (an epic, a story, a feature), if any.</param>
/// <param name="ParentType">The parent's type name ("Epic", "Story", "User Story").</param>
/// <param name="ChildKeys">Direct children in the tracker's order (Jira's sub-task order, ADO's
/// backlog rank) — empty for most tickets.</param>
public sealed record TrackerTicket(
    string Key,
    string Id,
    string Url,
    string? EpicKey,
    string ContentHash,
    TrackerTicketBody? Body,
    TrackerTicketComment[] NewComments,
    string? Status = null,
    string? StatusCategory = null,
    bool AssignedToMe = false,
    bool InIntake = false,
    string? ParentKey = null,
    string? ParentType = null,
    string[]? ChildKeys = null);

/// <param name="Type">The tracker's own type name ("Bug", "Story", "Task", "User Story").</param>
/// <param name="Priority">The tracker's own priority name ("Highest", "1", …).</param>
public sealed record TrackerTicketBody(
    string Title,
    string? Type,
    string? Status,
    string? StatusCategory,
    string? Priority,
    string Description,
    string? AcceptanceCriteria,
    string? ReproSteps,
    string[] Labels,
    TrackerAttachmentMeta[] Attachments,
    DateTimeOffset? UpdatedAt,
    // Jira components — what a "component" routing rule matches. Not part of the content hash.
    string[]? Components = null,
    // Azure DevOps area path (Shop\Backend) — what an "areaPath" routing rule matches.
    string? AreaPath = null);

public sealed record TrackerTicketComment(
    string Id, string Author, DateTimeOffset CreatedAt, string Body);

/// <param name="Cached">Whether the runner holds the file locally (videos, archives and files
/// over the size caps are listed but not downloaded).</param>
public sealed record TrackerAttachmentMeta(
    string FileName, long Size, string? MimeType, bool Cached);

public sealed record TrackerReportAck(bool Accepted, string? Error = null);

/// <summary>Which ticket's cached attachments to copy into a run's worktree
/// (<c>.orkka/context/attachments/</c>) before the agent starts.</summary>
public sealed record TrackerTicketRef(string ConnectionId, string TicketKey);

// ── Connections from the UI (host → runner, write-only) ─────────────────────

/// <summary>
/// Adds (or replaces the token of) a tracker connection on the developer's machine, at the
/// developer's request from the settings page. The ONE host → runner message that carries a
/// credential: it travels over loopback to the developer's own runner, which tests it, stores
/// it in the OS secret store and answers with metadata only. The host never stores, logs or
/// echoes it, and nothing ever sends it back.
/// </summary>
/// <param name="Kind">A <see cref="TrackerKinds"/> value.</param>
/// <param name="Site">Jira: the site (<c>acme.atlassian.net</c>). Azure DevOps: the organization
/// (<c>acme</c> or <c>https://dev.azure.com/acme</c>).</param>
/// <param name="Email">Jira only: the Atlassian account email the API token belongs to.</param>
public sealed record AddTrackerConnectionRequest(string Kind, string Site, string? Email, string Token)
{
    // A record's generated ToString prints every property; this one must never print the token.
    public override string ToString() => $"AddTrackerConnectionRequest {{ Kind = {Kind}, Site = {Site}, Token = *** }}";
}

/// <summary>Metadata only — never the token.</summary>
public sealed record AddTrackerConnectionResult(bool Ok, string? Error = null, RunnerTrackerConnection? Connection = null);

/// <param name="Id">A <see cref="RunnerTrackerConnection.Id"/> on this runner.</param>
public sealed record RemoveTrackerConnectionRequest(string Id);

public sealed record RemoveTrackerConnectionResult(bool Ok, string? Error = null);
