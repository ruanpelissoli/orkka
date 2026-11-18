namespace Orkka.Local.Auth;

using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

/// <summary>
/// Orkka.Local is single-user: a request that proves it comes from that user authenticates as
/// <see cref="LocalUserId"/>. Two proofs, nothing else:
/// <list type="bullet">
/// <item>the <c>orkka_s</c> session cookie (the browser; issued by <c>/launch</c>), used only
/// when the request carries no bearer;</item>
/// <item><c>Authorization: Bearer &lt;install secret&gt;</c> (the launcher, the CLI, the UI's
/// server side). SignalR hubs also accept it as the <c>access_token</c> query parameter, which
/// is how SignalR clients send a bearer on a WebSocket.</item>
/// </list>
/// Any other bearer (<c>orkr_</c> runner tokens, which the runner hub screens itself) is
/// <see cref="AuthenticateResult.NoResult"/>, never a failure. <c>orkka_key_</c> keys never reach
/// this handler: the default policy scheme forwards them to the org API key handler.
/// </summary>
public sealed class LocalAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    LocalSettings settings,
    LocalSessionStore sessions) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "OrkkaLocal";

    /// <summary>Stable pseudo user id of the single local user; OrgMember rows key off it.</summary>
    public const string LocalUserId = "local_user";

    public const string SessionCookie = "orkka_s";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var bearer = GetBearer(Request);
        if (bearer is not null)
        {
            return Task.FromResult(settings.IsInstallSecret(bearer)
                ? Success("install-secret")
                : AuthenticateResult.NoResult());
        }

        if (Request.Cookies.TryGetValue(SessionCookie, out var cookie)
            && sessions.Validate(cookie, out var refreshed))
        {
            if (refreshed)
                AppendSessionCookie(Response, cookie!);
            return Task.FromResult(Success("session"));
        }

        return Task.FromResult(AuthenticateResult.NoResult());
    }

    /// <summary>The bearer a request presents: the Authorization header, or — on a hub path —
    /// the <c>access_token</c> query parameter. Null when it presents none.</summary>
    public static string? GetBearer(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return header["Bearer ".Length..].Trim();

        if (request.Path.StartsWithSegments("/hubs")
            && request.Query["access_token"].ToString() is { Length: > 0 } token)
            return token;

        return null;
    }

    /// <summary>True when the session cookie is what would authenticate this request — it has
    /// one and presents no bearer. The request guard's CSRF/Origin rules key off this.</summary>
    public static bool IsCookieAuthenticated(HttpRequest request) =>
        GetBearer(request) is null
        && string.IsNullOrEmpty(request.Headers.Authorization.ToString())
        && request.Cookies.ContainsKey(SessionCookie);

    public static void AppendSessionCookie(HttpResponse response, string token) =>
        response.Cookies.Append(SessionCookie, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            // http://127.0.0.1 — there is no TLS on loopback; the host never listens elsewhere.
            Secure = false,
            MaxAge = LocalSessionStore.Lifetime,
            IsEssential = true,
        });

    private AuthenticateResult Success(string method)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, LocalUserId),
            new Claim("sub", LocalUserId),
            new Claim("auth_method", method),
        };
        var identity = new ClaimsIdentity(claims, SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
