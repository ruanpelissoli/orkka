namespace Orkka.IntegrationTests.Fixtures;

using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;

public sealed class TestCurrentUser : ICurrentUser
{
    public static readonly Ulid TestOrgId = Ulid.NewUlid();
    public static readonly Ulid TestMemberId = Ulid.NewUlid();
    /// <summary>The local user: CurrentUserMiddleware resolves the install-secret bearer to this
    /// id, so seeding the test org's member under it keeps every request in the test org.</summary>
    public const string TestUserId = Orkka.Local.Auth.LocalAuthHandler.LocalUserId;

    /// <summary>
    /// Mutable so tests can flip to a restricted member. Reset in a finally block —
    /// integration tests in the collection share this static state.
    /// </summary>
    public static OrgRole CurrentRole { get; set; } = OrgRole.Owner;

    public static IReadOnlySet<Permission> CurrentPermissions { get; set; } = new HashSet<Permission>();

    /// <summary>Override the caller's org (e.g. to act inside a throwaway org). Reset in finally.</summary>
    public static Ulid? CurrentOrgId { get; set; }

    public static void Reset()
    {
        CurrentRole = OrgRole.Owner;
        CurrentPermissions = new HashSet<Permission>();
        CurrentOrgId = null;
    }

    public bool IsAuthenticated => true;
    public string UserId => TestUserId;
    public Ulid OrgId => CurrentOrgId ?? TestOrgId;
    public Ulid MemberId => TestMemberId;
    public OrgRole Role => CurrentRole;
    public bool IsOwner => Role == OrgRole.Owner;
    public IReadOnlySet<Permission> Permissions => CurrentPermissions;
    public string? Locale => null;

    public bool HasAnyRole(params OrgRole[] roles) => roles.Contains(Role);

    public bool HasPermission(Permission permission) => IsOwner || Permissions.Contains(permission);
}
