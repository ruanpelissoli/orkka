namespace Orkka.SharedKernel.Security;

public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    string UserId { get; }
    Ulid OrgId { get; }
    Ulid MemberId { get; }
    OrgRole Role { get; }
    bool IsOwner { get; }

    /// <summary>Org-wide grants for this member. Empty for Owners — they bypass checks via <see cref="HasPermission"/>.</summary>
    IReadOnlySet<Permission> Permissions { get; }

    string? Locale { get; }
    bool HasAnyRole(params OrgRole[] roles);

    /// <summary>True when the member holds the permission or is an Owner.</summary>
    bool HasPermission(Permission permission);
}
