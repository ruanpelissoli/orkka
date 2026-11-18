namespace Orkka.Tests.Local;

using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Orkka.Local;
using Orkka.Local.Auth;

/// <summary>
/// The guard is what makes a loopback-only host safe to have running while the user browses
/// the web: any page can make the browser send requests to 127.0.0.1, so Host, Origin and the
/// CSRF header are the boundary — not the bind address.
/// </summary>
public sealed class LocalRequestGuardTests
{
    private const int Port = 6750;
    private const string WorkerKey = "per-process-worker-key";

    private static readonly LocalSettings Settings = new(Port, Path.GetTempPath(), "ui-dist", "install-secret");

    private static async Task<(int Status, bool NextCalled)> RunAsync(Action<HttpContext> arrange)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["WorkerAuth:ApiKey"] = WorkerKey })
            .Build();
        var nextCalled = false;
        var guard = new LocalRequestGuard(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, Settings, configuration);

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Host = new HostString("127.0.0.1", Port);
        context.Request.Path = "/api/org/me";
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Response.Body = new MemoryStream();
        arrange(context);

        await guard.InvokeAsync(context);
        return (context.Response.StatusCode, nextCalled);
    }

    private static void WithSessionCookie(HttpContext context) =>
        context.Request.Headers.Cookie = $"{LocalAuthHandler.SessionCookie}=abc";

    // ── Host allowlist (DNS rebinding) ──────────────────────────────────────────────

    [Theory]
    [InlineData("127.0.0.1:6750")]
    [InlineData("localhost:6750")]
    [InlineData("LOCALHOST:6750")]
    [InlineData("[::1]:6750")]
    public async Task AllowedHost_PassesThrough(string host)
    {
        var (status, next) = await RunAsync(c => c.Request.Host = HostString.FromUriComponent(host));

        next.Should().BeTrue();
        status.Should().Be(StatusCodes.Status200OK);
    }

    [Theory]
    [InlineData("evil.com")]
    [InlineData("evil.com:6750")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.1:6751")]
    [InlineData("localhost")]
    [InlineData("127.0.0.2:6750")]
    [InlineData("0.0.0.0:6750")]
    public async Task UnknownHost_Is421(string host)
    {
        var (status, next) = await RunAsync(c => c.Request.Host = HostString.FromUriComponent(host));

        next.Should().BeFalse();
        status.Should().Be(StatusCodes.Status421MisdirectedRequest);
    }

    [Fact]
    public async Task MissingHost_Is421()
    {
        var (status, next) = await RunAsync(c => c.Request.Host = default);

        next.Should().BeFalse();
        status.Should().Be(StatusCodes.Status421MisdirectedRequest);
    }

    // ── Cookie-authenticated writes: Origin + X-Orkka-Csrf ─────────────────────────

    [Fact]
    public async Task CookieWrite_WithSameOriginAndCsrfHeader_PassesThrough()
    {
        var (_, next) = await RunAsync(c =>
        {
            WithSessionCookie(c);
            c.Request.Method = HttpMethods.Post;
            c.Request.Headers.Origin = "http://127.0.0.1:6750";
            c.Request.Headers[LocalRequestGuard.CsrfHeader] = "1";
        });

        next.Should().BeTrue();
    }

    [Theory]
    [InlineData("http://evil.com", "1")]
    [InlineData("http://localhost:6750", "1")]      // a different origin than the Host
    [InlineData("https://127.0.0.1:6750", "1")]
    [InlineData(null, "1")]
    [InlineData("http://127.0.0.1:6750", null)]
    [InlineData("http://127.0.0.1:6750", "0")]
    public async Task CookieWrite_WithoutMatchingOriginAndCsrfHeader_Is403(string? origin, string? csrf)
    {
        var (status, next) = await RunAsync(c =>
        {
            WithSessionCookie(c);
            c.Request.Method = HttpMethods.Delete;
            if (origin is not null)
                c.Request.Headers.Origin = origin;
            if (csrf is not null)
                c.Request.Headers[LocalRequestGuard.CsrfHeader] = csrf;
        });

        next.Should().BeFalse();
        status.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task CookieRead_NeedsNoCsrfHeader()
    {
        var (_, next) = await RunAsync(WithSessionCookie);

        next.Should().BeTrue();
    }

    [Fact]
    public async Task BearerWrite_IsNotSubjectToCsrfRules()
    {
        // No ambient credential: a cross-site page cannot attach an Authorization header.
        var (_, next) = await RunAsync(c =>
        {
            WithSessionCookie(c);
            c.Request.Method = HttpMethods.Post;
            c.Request.Headers.Authorization = "Bearer install-secret";
        });

        next.Should().BeTrue();
    }

    [Fact]
    public async Task UiPathCookieWrite_NeedsSameOriginOnly()
    {
        // Paths of the static UI: it answers no write at all, but a cross-site form post with
        // the cookie is still refused before it gets there.
        var (_, sameOrigin) = await RunAsync(c =>
        {
            WithSessionCookie(c);
            c.Request.Path = "/en/p/shop/board";
            c.Request.Method = HttpMethods.Post;
            c.Request.Headers.Origin = "http://127.0.0.1:6750";
        });
        var (status, crossSite) = await RunAsync(c =>
        {
            WithSessionCookie(c);
            c.Request.Path = "/en/p/shop/board";
            c.Request.Method = HttpMethods.Post;
            c.Request.Headers.Origin = "http://evil.com";
        });

        sameOrigin.Should().BeTrue();
        crossSite.Should().BeFalse();
        status.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task SignedBlobUpload_IsAuthorizedBySignatureNotCookie()
    {
        var (_, next) = await RunAsync(c =>
        {
            WithSessionCookie(c);
            c.Request.Path = "/blobs/design-assets/a/b.svg";
            c.Request.QueryString = new QueryString("?exp=1&sig=abc");
            c.Request.Method = HttpMethods.Put;
        });

        next.Should().BeTrue();
    }

    // ── WebSocket upgrades ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CookieWebSocket_FromAnotherOrigin_Is403()
    {
        var (status, next) = await RunAsync(c =>
        {
            WithSessionCookie(c);
            c.Request.Path = "/hubs/tasks";
            c.Request.Headers.Upgrade = "websocket";
            c.Request.Headers.Origin = "http://evil.com";
        });

        next.Should().BeFalse();
        status.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task CookieWebSocket_WithoutOrigin_Is403()
    {
        var (status, next) = await RunAsync(c =>
        {
            WithSessionCookie(c);
            c.Request.Path = "/hubs/tasks";
            c.Request.Headers.Upgrade = "websocket";
        });

        next.Should().BeFalse();
        status.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task CookieWebSocket_FromOwnOrigin_PassesThrough()
    {
        var (_, next) = await RunAsync(c =>
        {
            WithSessionCookie(c);
            c.Request.Path = "/hubs/tasks";
            c.Request.Headers.Upgrade = "websocket";
            c.Request.Headers.Origin = "http://127.0.0.1:6750";
        });

        next.Should().BeTrue();
    }

    [Fact]
    public async Task RunnerWebSocket_WithoutCookie_NeedsNoOrigin()
    {
        var (_, next) = await RunAsync(c =>
        {
            c.Request.Path = "/hubs/runner";
            c.Request.Headers.Upgrade = "websocket";
            c.Request.Headers.Authorization = "Bearer orkr_token";
        });

        next.Should().BeTrue();
    }

    // ── /internal: loopback + the per-process key ─────────────────────────────────

    [Fact]
    public async Task Internal_FromLoopbackWithKey_PassesThrough()
    {
        var (_, next) = await RunAsync(c =>
        {
            c.Request.Path = "/internal/runner/utility";
            c.Request.Method = HttpMethods.Post;
            c.Request.Headers["X-Worker-Key"] = WorkerKey;
        });

        next.Should().BeTrue();
    }

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("192.168.1.20")]
    [InlineData(null)]
    public async Task Internal_FromNonLoopback_Is403EvenWithTheKey(string? remoteIp)
    {
        var (status, next) = await RunAsync(c =>
        {
            c.Request.Path = "/internal/runner/utility";
            c.Request.Method = HttpMethods.Post;
            c.Request.Headers["X-Worker-Key"] = WorkerKey;
            c.Connection.RemoteIpAddress = remoteIp is null ? null : IPAddress.Parse(remoteIp);
        });

        next.Should().BeFalse();
        status.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-key")]
    public async Task Internal_WithoutTheKey_Is403(string? key)
    {
        var (status, next) = await RunAsync(c =>
        {
            c.Request.Path = "/internal/runner/utility";
            c.Request.Method = HttpMethods.Post;
            if (key is not null)
                c.Request.Headers["X-Worker-Key"] = key;
        });

        next.Should().BeFalse();
        status.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Internal_SessionCookieIsNoSubstituteForTheKey()
    {
        var (status, next) = await RunAsync(c =>
        {
            WithSessionCookie(c);
            c.Request.Path = "/internal/runner/utility";
            c.Request.Method = HttpMethods.Post;
            c.Request.Headers.Origin = "http://127.0.0.1:6750";
            c.Request.Headers[LocalRequestGuard.CsrfHeader] = "1";
        });

        next.Should().BeFalse();
        status.Should().Be(StatusCodes.Status403Forbidden);
    }
}
