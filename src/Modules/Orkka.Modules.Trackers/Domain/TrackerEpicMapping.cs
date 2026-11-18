namespace Orkka.Modules.Trackers.Domain;

using Orkka.SharedKernel.Domain;

/// <summary>
/// One rule of a project's ticket source. An <see cref="TrackerRuleKind.Epic"/> rule names an
/// epic: in <see cref="TrackerScopeMode.Epics"/> its child tickets are the ones picked up, and in
/// either mode, when it has a <see cref="RepoId"/>, they are worked there. The other kinds are
/// routing rules only — a ticket with that component, label or area path is worked in
/// <see cref="RepoId"/>. Rules are tried epics first, then the rest, each in
/// <see cref="Position"/> order; the first match wins.
/// </summary>
public sealed class TrackerEpicMapping : TenantEntity
{
    public Ulid ConfigId { get; set; }

    public TrackerRuleKind Kind { get; set; } = TrackerRuleKind.Epic;

    /// <summary>The rule value: Jira issue key (<c>SHOP-12</c>) / Azure DevOps work item id for an
    /// epic; a component name, a label / tag, or an area path (<c>Shop\Backend</c>, which also
    /// matches the paths under it) otherwise.</summary>
    public required string EpicKey { get; set; }

    /// <summary>The epic's title when it was picked — shown in settings, refreshed by polling.</summary>
    public string? EpicTitle { get; set; }

    /// <summary>Where matching tickets are worked. Optional for an epic that only scopes intake.</summary>
    public Ulid? RepoId { get; set; }

    /// <summary>A folder inside the repository, forward slashes, no leading slash, never
    /// <c>..</c>. Null for the whole repository.</summary>
    public string? Subpath { get; set; }

    /// <summary>Order among rules of the same group (epics, then the rest).</summary>
    public int Position { get; set; }
}

public enum TrackerRuleKind
{
    Epic = 0,
    Component = 1,
    Label = 2,
    AreaPath = 3,
}
