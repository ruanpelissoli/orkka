namespace Orkka.Local.Auth;


public sealed class CurrentUser(IHttpContextAccessor httpContextAccessor) : ICurrentUser
{
    public bool IsAuthenticated => httpContextAccessor.HttpContext?.Items.ContainsKey(CurrentUserKeys.UserId) is true;
    public string UserId => GetRequired<string>(CurrentUserKeys.UserId);
    public Ulid OrgId => GetRequired<Ulid>(CurrentUserKeys.OrgId);
    public Ulid MemberId => GetRequired<Ulid>(CurrentUserKeys.MemberId);
    public OrgRole Role => GetRequired<OrgRole>(CurrentUserKeys.Role);
    public bool IsOwner => Role == OrgRole.Owner;

    public IReadOnlySet<Permission> Permissions =>
        httpContextAccessor.HttpContext?.Items[CurrentUserKeys.Permissions] as IReadOnlySet<Permission>
        ?? EmptyPermissions;

    public string? Locale => httpContextAccessor.HttpContext?.Items[CurrentUserKeys.Locale] as string;

    public bool HasAnyRole(params OrgRole[] roles) => roles.Contains(Role);

    public bool HasPermission(Permission permission) => IsOwner || Permissions.Contains(permission);

    private static readonly IReadOnlySet<Permission> EmptyPermissions = new HashSet<Permission>();

    private T GetRequired<T>(string key)
    {
        var ctx = httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException("No HttpContext available.");

        if (ctx.Items.TryGetValue(key, out var value) && value is T typed)
            return typed;

        throw new InvalidOperationException(
            $"CurrentUser.{key} not resolved. Ensure CurrentUserMiddleware is registered and the user has an OrgMember record.");
    }
}

public static class CurrentUserKeys
{
    public const string UserId = "CurrentUser.UserId";
    public const string OrgId = "CurrentUser.OrgId";
    public const string MemberId = "CurrentUser.MemberId";
    public const string Role = "CurrentUser.Role";
    public const string IsOwner = "CurrentUser.IsOwner";
    public const string Locale = "CurrentUser.Locale";
    public const string Permissions = "CurrentUser.Permissions";
}
