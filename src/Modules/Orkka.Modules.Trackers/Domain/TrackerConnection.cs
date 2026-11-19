namespace Orkka.Modules.Trackers.Domain;

using Orkka.SharedKernel.Domain;

/// <summary>
/// A tracker connection one of the developer's machines holds — Jira Cloud, Azure DevOps
/// Services or GitHub Projects. Metadata only, upserted from the runner's Hello: the token lives
/// in that machine's secret store (or, for GitHub, may be the developer's gh login) and Orkka
/// never sees it. A project's ticket source points at one of
/// these, which is also why only its owner can configure that source: only their runner can
/// answer for it.
/// </summary>
public sealed class TrackerConnection : TenantEntity
{
    /// <summary>The developer (user id) whose runner holds the connection.</summary>
    public required string UserId { get; set; }

    /// <summary>The runner's own id for the connection (<c>jira-acme</c>) — what every query to
    /// the runner names it by. Unique per (org, user).</summary>
    public required string ConnectionKey { get; set; }

    /// <summary>"jira", "ado" or "github" (<c>TrackerKinds</c>).</summary>
    public required string Kind { get; set; }

    /// <summary>GitHub: "gh" (the developer's gh login, nothing stored) or "token"; null for
    /// Jira and Azure DevOps, which always hold a token (<c>TrackerAuthModes</c>).</summary>
    public string? AuthMode { get; set; }

    public required string SiteUrl { get; set; }

    public string? AccountId { get; set; }

    public string? DisplayName { get; set; }

    public bool Healthy { get; set; }

    public string? HealthDetail { get; set; }

    /// <summary>The machine that reported it last. One user can run several machines; each
    /// Hello only speaks for its own.</summary>
    public required string MachineName { get; set; }

    public DateTimeOffset? CheckedAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }
}
