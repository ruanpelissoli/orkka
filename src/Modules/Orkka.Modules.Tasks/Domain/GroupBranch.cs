namespace Orkka.Modules.Tasks.Domain;

using Orkka.SharedKernel.Domain;

/// <summary>
/// One repository's half of a Story: the shared branch there and the single forge pull request
/// opened from it. A story whose sub-tasks span repositories has one row per repository — same
/// branch name, its own draft pull request, all readied together.
/// </summary>
public sealed class GroupBranch : TenantEntity
{
    public Ulid GroupId { get; set; }
    public Ulid RepoId { get; set; }
    public required string BranchName { get; set; }

    /// <summary>The forge pull request the developer's CLI opened (as a draft) after the first
    /// member landed. Null for a linked folder with no origin, and until then.</summary>
    public string? ForgePrUrl { get; set; }

    /// <summary>Still a draft on the forge (as far as Orkka last set it).</summary>
    public bool ForgePrDraft { get; set; }

    /// <summary>When this repository's pull request was marked ready.</summary>
    public DateTimeOffset? ReadyAt { get; set; }

    /// <summary>The forge pull request's state as the developer's CLI last reported it
    /// ("open", "merged", "closed") — what "every story PR merged" is read from.</summary>
    public string? ForgeState { get; set; }

    public TaskGroup Group { get; set; } = null!;
}
