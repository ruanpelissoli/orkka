namespace Orkka.Local.Auth;

using System.Net;
using Orkka.Runners.Server.Hosting;

/// <summary>
/// First middleware in the pipeline. The host listens on 127.0.0.1 only, but any web page the
/// user opens can still make their browser send requests to it, so:
/// <list type="number">
/// <item><b>Host allowlist</b> — only <c>127.0.0.1:{port}</c>, <c>localhost:{port}</c> and
/// <c>[::1]:{port}</c>; anything else is 421. That is what stops DNS rebinding (a hostile name
/// re-pointed at 127.0.0.1 keeps its own Host header).</item>
/// <item><b>Cookie-authenticated writes</b> — an unsafe method that the session cookie would
/// authenticate needs <c>Origin</c> equal to this host's own origin AND <c>X-Orkka-Csrf: 1</c>
/// (a header no cross-site form can send), else 403. The static UI accepts no writes at all, so
/// on its paths only the Origin must match (the write then finds nothing to answer it).</item>
/// <item><b>Cookie-authenticated WebSockets</b> — need a matching <c>Origin</c> (browsers do not
/// apply CORS to WebSocket handshakes).</item>
/// <item><b><c>/internal/*</c></b> — the in-process relays: a loopback remote address AND the
/// per-process worker key, else 403.</item>
/// </list>
/// Bearer-authenticated requests (install secret, runner token, API key) carry no ambient
/// credential, so rules 2–3 do not apply to them. A <c>/blobs</c> request carrying a signature
/// is authorized by the signature alone, so it is not cookie-authenticated either.
/// </summary>
public sealed class LocalRequestGuard(RequestDelegate next, LocalSettings settings, IConfiguration configuration)
{
    public const string CsrfHeader = "X-Orkka-Csrf";

    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;

        if (!IsAllowedHost(request.Host, settings.Port))
        {
            await RejectAsync(context, StatusCodes.Status421MisdirectedRequest, "Unknown host.");
            return;
        }

        if (request.Path.StartsWithSegments("/internal"))
        {
            if (!IsLoopback(context.Connection.RemoteIpAddress)
                || !InternalRelayAuth.KeyMatches(
                    configuration["WorkerAuth:ApiKey"],
                    request.Headers[InternalRelayAuth.HeaderName].FirstOrDefault()))
            {
                await RejectAsync(context, StatusCodes.Status403Forbidden, "Forbidden.");
                return;
            }

            await next(context);
            return;
        }

        if (LocalAuthHandler.IsCookieAuthenticated(request) && !IsSignedBlobRequest(request))
        {
            var sameOrigin = IsSameOrigin(request);

            if (context.WebSockets.IsWebSocketRequest || IsWebSocketUpgrade(request))
            {
                if (!sameOrigin)
                {
                    await RejectAsync(context, StatusCodes.Status403Forbidden, "Cross-origin WebSocket refused.");
                    return;
                }
            }
            else if (!IsSafeMethod(request.Method))
            {
                var csrfOk = string.Equals(request.Headers[CsrfHeader].ToString(), "1", StringComparison.Ordinal);
                var allowed = LocalPaths.IsHostOwned(request.Path) ? sameOrigin && csrfOk : sameOrigin;
                if (!allowed)
                {
                    await RejectAsync(context, StatusCodes.Status403Forbidden, "Cross-site request refused.");
                    return;
                }
            }
        }

        await next(context);
    }

    public static bool IsAllowedHost(HostString host, int port)
    {
        if (!host.HasValue || host.Port != port)
            return false;
        return host.Host is "127.0.0.1" or "[::1]"
            || string.Equals(host.Host, "localhost", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Origin is this host's own (scheme http, same host:port as the request).</summary>
    public static bool IsSameOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        return origin.Length > 0
            && string.Equals(origin, $"http://{request.Host.Value}", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLoopback(IPAddress? address) => address is not null && IPAddress.IsLoopback(address);

    private static bool IsSafeMethod(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method)
        || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method);

    private static bool IsWebSocketUpgrade(HttpRequest request) =>
        request.Headers.Upgrade.ToString().Contains("websocket", StringComparison.OrdinalIgnoreCase);

    private static bool IsSignedBlobRequest(HttpRequest request) =>
        request.Path.StartsWithSegments("/blobs") && request.Query.ContainsKey("sig");

    private static Task RejectAsync(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain; charset=utf-8";
        return context.Response.WriteAsync(message, context.RequestAborted);
    }
}
