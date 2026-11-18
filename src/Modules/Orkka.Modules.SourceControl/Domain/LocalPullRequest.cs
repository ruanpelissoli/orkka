namespace Orkka.Modules.SourceControl.Domain;

using Orkka.SharedKernel.Domain;

/// <summary>
/// Orkka's own pull request, for every repository. The whole review flow (diff, comments,
/// approve) runs against this row plus the developer's runner; no forge API is involved and
/// Orkka holds no forge credential.
///
/// What "merge" does depends on where the repository lives. A folder on the developer's
/// machine is squash-merged in place. A remote is PUBLISHED: the developer's runner opens a
/// pull request on the forge with their own CLI, and that forge pull request — reviewed by
/// their team, under their name — is where the work goes from here (<see cref="ForgeUrl"/>).
/// </summary>
public sealed class LocalPullRequest : TenantEntity
{
    /// <summary>Monotonic per org. The forge-shaped call sites identify a pull request by
    /// number alone, so this is what stands in for owner/repo/number everywhere.</summary>
    public int Number { get; set; }
    public Ulid? TaskId { get; set; }
    public Ulid RepoId { get; set; }
    public required string Title { get; set; }
    public string? Body { get; set; }
    public required string HeadBranch { get; set; }
    public required string BaseBranch { get; set; }
    /// <summary>A Story member's starting commit on the shared branch: its diff is
    /// <c>BaseSha..head</c>, so the review shows only that member's change while the forge pull
    /// request shows the whole story. Null = the usual diff against <see cref="BaseBranch"/>.</summary>
    public string? BaseSha { get; set; }
    /// <summary>The work has left Orkka: squash-merged into a folder, or published to the
    /// forge (then <see cref="ForgeUrl"/> is set and the forge pull request is still open).</summary>
    public bool Merged { get; set; }
    public DateTimeOffset? MergedAt { get; set; }

    /// <summary>The pull request the developer's runner opened on the forge for a remote
    /// repository. Null for a folder, and until the work is published.</summary>
    public string? ForgeUrl { get; set; }

    /// <summary>The forge pull request's last known state — open, merged or closed — as the
    /// developer's CLI reported it at <see cref="ForgeCheckedAt"/>.</summary>
    public string? ForgeState { get; set; }
    public DateTimeOffset? ForgeCheckedAt { get; set; }

    /// <summary>Closed without merging — "reject this work" needs somewhere to go that isn't
    /// a merge. The head branch is deliberately left in the user's repository so the commits
    /// stay reachable.</summary>
    public DateTimeOffset? ClosedAt { get; set; }
}

public sealed class LocalPrReview : TenantEntity
{
    public Ulid PullRequestId { get; set; }

    /// <summary>APPROVED / CHANGES_REQUESTED / COMMENTED — GitHub's review-state vocabulary,
    /// so the shared review UI renders these unchanged.</summary>
    public required string State { get; set; }
    public string? Body { get; set; }
    public required string Author { get; set; }
}

public sealed class LocalPrComment : TenantEntity
{
    public Ulid PullRequestId { get; set; }
    public required string Path { get; set; }
    public int Line { get; set; }
    public required string Body { get; set; }
    public required string Author { get; set; }
}
