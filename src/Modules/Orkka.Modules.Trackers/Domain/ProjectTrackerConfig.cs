namespace Orkka.Modules.Trackers.Domain;

using Orkka.SharedKernel.Domain;

/// <summary>
/// Where a project's tickets come from: one tracker connection, one scope inside it (a Jira
/// project, an Azure DevOps project), the epics whose child tickets the project works — each
/// mapped onto a repository and optionally a folder inside it — and how Orkka reports progress
/// back (the status map). One per project.
///
/// The owner is the developer whose runner holds the connection: their machine polls the
/// tracker with their token, so the tickets picked up are the ones assigned to them.
/// </summary>
public sealed class ProjectTrackerConfig : TenantEntity
{
    public Ulid ProjectId { get; set; }

    /// <summary>user id of the developer whose runner holds <see cref="TrackerConnectionId"/>.</summary>
    public required string OwnerUserId { get; set; }

    public Ulid TrackerConnectionId { get; set; }

    /// <summary>Jira project key / Azure DevOps project name / GitHub owner (user or
    /// organization login).</summary>
    public required string Scope { get; set; }

    /// <summary>The board intake is limited to — a Jira board id (its filter's JQL is AND-ed onto
    /// the intake query) or an Azure DevOps team id (the team's area paths). Null = every board in
    /// <see cref="Scope"/>. Resolved by the runner on every poll; a board that is gone fails the
    /// poll rather than widening it. Intake only, like <see cref="ExtraClause"/>. GitHub: the
    /// project's node id (<c>PVT_…</c>) — required, the project IS the source.</summary>
    public string? BoardId { get; set; }

    /// <summary>The board's name when it was picked (the team's name in Azure DevOps, the
    /// project's title on GitHub) — what the settings page and a "board not found" error show.</summary>
    public string? BoardName { get; set; }

    /// <summary>GitHub: the project's single-select field that holds the status; null = "Status".</summary>
    public string? StatusField { get; set; }

    /// <summary>GitHub: JSON array of repositories (<c>owner/name</c>) intake is limited to; empty
    /// = every repository on the project. Intake only: a tracked issue keeps syncing.</summary>
    public string RepoFilterJson { get; set; } = "[]";

    /// <summary>Optional JQL (Jira) or WIQL condition (Azure DevOps) AND-ed onto the generated
    /// query — "labels = backend", "[System.Tags] CONTAINS 'api'".</summary>
    public string? ExtraClause { get; set; }

    /// <summary>Grandchildren too (stories under features under an epic), not only direct children.</summary>
    public bool IncludeDescendants { get; set; }

    /// <summary>Submit an imported ticket straight into the pipeline. The plan still waits for
    /// approval unless the project auto-approves plans.</summary>
    public bool AutoSubmit { get; set; } = true;

    /// <summary>How often the runner asks the tracker, clamped to 60–300.</summary>
    public int PollIntervalSeconds { get; set; } = DefaultPollIntervalSeconds;

    /// <summary>Tracker status names to move a ticket to as its task progresses. JSON of
    /// <c>TrackerStatusMap</c>; a null entry means "don't move it".</summary>
    public string StatusMapJson { get; set; } = "{}";

    /// <summary>Task branch names: <c>{type}</c>, <c>{key}</c>, <c>{slug}</c>.</summary>
    public string BranchTemplate { get; set; } = DefaultBranchTemplate;

    public bool Enabled { get; set; } = true;

    public DateTimeOffset? LastPolledAt { get; set; }

    public string? LastPollError { get; set; }

    /// <summary>The runner process (Hello InstanceId) polling this source until
    /// <see cref="LeaseExpiresAt"/> — one machine at a time, so a developer with two machines
    /// holding the same connection does not import every ticket twice.</summary>
    public string? LeaseHolder { get; set; }

    public DateTimeOffset? LeaseExpiresAt { get; set; }

    /// <summary>Which tickets are picked up: every ticket assigned to the owner in an intake
    /// status (<see cref="TrackerScopeMode.All"/>), or only those under the epic rules.</summary>
    public TrackerScopeMode ScopeMode { get; set; } = TrackerScopeMode.All;

    /// <summary>JSON array of tracker status names a ticket must be in to become a task. Only
    /// intake: a ticket that moves on keeps being tracked.</summary>
    public string IntakeStatusesJson { get; set; } = DefaultIntakeStatusesJson;

    /// <summary>How a Story with sub-tasks is worked (stored for the stories phase; 1 = one
    /// branch, one pull request).</summary>
    public int StoryMode { get; set; } = 1;

    /// <summary>The repository a ticket goes to when no rule matched and the project has
    /// several — before the repo router is asked.</summary>
    public Ulid? DefaultRepoId { get; set; }

    /// <summary>The epics (scope in <see cref="TrackerScopeMode.Epics"/>, and routing) plus the
    /// component / label / area-path routing rules. Named for its first use; see
    /// <see cref="TrackerEpicMapping.Kind"/>.</summary>
    public List<TrackerEpicMapping> Epics { get; set; } = [];

    public const string DefaultIntakeStatusesJson = "[\"To Do\"]";
    public const int DefaultPollIntervalSeconds = 90;
    public const int MinPollIntervalSeconds = 60;
    public const int MaxPollIntervalSeconds = 300;
    public const string DefaultBranchTemplate = "{type}/{key}-{slug}";
}

public enum TrackerScopeMode
{
    /// <summary>Every ticket in the scope assigned to the owner in an intake status.</summary>
    All = 0,
    /// <summary>Only tickets under the configured epics.</summary>
    Epics = 1,
}

/// <summary>Tracker status names per pipeline milestone. Null = leave the ticket where it is.</summary>
/// <summary>Tracker status names per pipeline step (<see cref="TrackerPhase"/>). Null = leave the
/// ticket where it is at that step. Stored JSON written before the steps were widened carried
/// <c>started</c>, which reads as <see cref="Planning"/>.</summary>
public sealed record TrackerStatusMap(
    string? PickedUp = null,
    string? Planning = null,
    string? Developing = null,
    string? InReview = null,
    string? PrOpened = null,
    string? Merged = null);
