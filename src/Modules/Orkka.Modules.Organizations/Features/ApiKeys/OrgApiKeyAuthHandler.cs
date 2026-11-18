namespace Orkka.Modules.Organizations.Features.ApiKeys;

using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orkka.Modules.Organizations.Contracts;
using Orkka.SharedKernel.Security;

/// <summary>
/// Bearer <c>orkka_key_…</c> authentication. Populates the same HttpContext.Items the local
/// path does (via CurrentUserMiddleware), so <c>ICurrentUser</c>, PermissionGuard, the tenant
/// filter and ProjectResolver all work unchanged — the key simply acts as its creator with a
/// permission subset and never as Owner. Hosts wire it as the target of a policy scheme that
/// forwards on the bearer prefix, so it composes with JWT / local auth.
/// </summary>
public sealed class OrgApiKeyAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOrgApiKeyValidator validator) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "OrgApiKey";
    public const string ClaimType = "org_api_key";
    public const string KeyIdClaim = "api_key_id";
    public const string ProjectClaim = "api_key_project";

    /// <summary>True when the request carries a bearer that looks like an org API key.</summary>
    public static bool Matches(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer " + ApiKeyValues.Prefix, StringComparison.OrdinalIgnoreCase);
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Matches(Request))
            return AuthenticateResult.NoResult();

        var raw = Request.Headers.Authorization.ToString()["Bearer ".Length..].Trim();
        var principal = await validator.ValidateAsync(raw, Context.RequestAborted);
        if (principal is null)
            return AuthenticateResult.Fail("Invalid or revoked API key");

        // The hosts' HttpContext.Items keys (AppHost/Local.Host `CurrentUserKeys`) — declared
        // per host, so the literals are repeated here; they are a frozen contract.
        Context.Items["CurrentUser.UserId"] = principal.UserId;
        Context.Items["CurrentUser.OrgId"] = principal.OrgId;
        Context.Items["CurrentUser.MemberId"] = principal.MemberId;
        Context.Items["CurrentUser.Role"] = OrgRole.Member;
        Context.Items["CurrentUser.Permissions"] = (IReadOnlySet<Permission>)principal.Permissions.ToHashSet();
        Context.Items[ApiKeyContextKeys.Principal] = principal;

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, principal.UserId),
            new("sub", principal.UserId),
            new("org_id", principal.OrgId.ToString()),
            new(KeyIdClaim, principal.KeyId.ToString()),
            new(ClaimType, "true"),
        };
        if (principal.ProjectId is { } projectId)
            claims.Add(new Claim(ProjectClaim, projectId.ToString()));

        var identity = new ClaimsIdentity(claims, SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}

/// <summary>HttpContext.Items keys set by <see cref="OrgApiKeyAuthHandler"/>.</summary>
public static class ApiKeyContextKeys
{
    public const string Principal = "ApiKey.Principal";
}
