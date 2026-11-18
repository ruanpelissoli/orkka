namespace Orkka.Modules.Organizations.Domain;

using Orkka.SharedKernel.Domain;
using Orkka.SharedKernel.Security;

/// <summary>
/// A single permission grant. ProjectId null means org-wide; per-project grants are
/// reserved for a future release (the enforcement path only reads org-wide rows today).
/// </summary>
public sealed class MemberPermission : TenantEntity
{
    public required Ulid MemberId { get; set; }
    public Permission Permission { get; set; }
    public Ulid? ProjectId { get; set; }
    public Ulid GrantedBy { get; set; }

    // Navigation
    public OrgMember Member { get; set; } = null!;
}
