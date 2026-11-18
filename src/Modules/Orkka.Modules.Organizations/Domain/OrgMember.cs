namespace Orkka.Modules.Organizations.Domain;

using Orkka.SharedKernel.Domain;
using Orkka.SharedKernel.Security;

public sealed class OrgMember : TenantEntity
{
    public required string UserId { get; set; }
    public OrgRole Role { get; set; }

    /// <summary>Per-member UI preferences as JSON (see MemberPreferences). Null means
    /// all defaults — new preference keys never need a backfill.</summary>
    public string? Preferences { get; set; }

    /// <summary>
    /// When the user last switched to this workspace (one org per job). A request that names
    /// no workspace resolves to the most recently activated one — so the board a developer
    /// opens is the job they were last working in. Null until they first switch.
    /// </summary>
    public DateTimeOffset? ActivatedAt { get; set; }

    // Navigation
    public Organization Organization { get; set; } = null!;
    public ICollection<MemberPermission> Permissions { get; set; } = [];
}
