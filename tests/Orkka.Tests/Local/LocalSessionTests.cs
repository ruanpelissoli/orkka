namespace Orkka.Tests.Local;

using System.Security.Claims;
using System.Text.Encodings.Web;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Orkka.Local;
using Orkka.Local.Auth;

/// <summary>Launch codes, the session store behind the <c>orkka_s</c> cookie, and the handler
/// that turns either proof into the local user.</summary>
public sealed class LocalSessionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "orkka-session-tests-" + Guid.NewGuid().ToString("N"));
    private readonly SteppedClock _clock = new(DateTimeOffset.Parse("2026-11-17T12:00:00Z"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private string SessionsPath => Path.Combine(_dir, "sessions.json");

    private LocalSessionStore NewStore() =>
        new(SessionsPath, NullLogger<LocalSessionStore>.Instance, _clock);

    // ── Launch codes ────────────────────────────────────────────────────────────────

    [Fact]
    public void LaunchCode_WorksExactlyOnce()
    {
        var codes = new LaunchCodeStore(_clock);
        var code = codes.Issue();

        codes.TryConsume(code).Should().BeTrue();
        codes.TryConsume(code).Should().BeFalse("a launch code is one-time");
    }

    [Fact]
    public void LaunchCode_ExpiresAfterSixtySeconds()
    {
        var codes = new LaunchCodeStore(_clock);
        var code = codes.Issue();

        _clock.Advance(TimeSpan.FromSeconds(61));

        codes.TryConsume(code).Should().BeFalse();
    }

    [Fact]
    public void LaunchCode_StillWorksJustBeforeExpiry()
    {
        var codes = new LaunchCodeStore(_clock);
        var code = codes.Issue();

        _clock.Advance(TimeSpan.FromSeconds(59));

        codes.TryConsume(code).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("made-up")]
    public void LaunchCode_UnknownCodeIsRefused(string? code)
        => new LaunchCodeStore(_clock).TryConsume(code).Should().BeFalse();

    [Fact]
    public void LaunchCode_IsUniquePerIssue()
    {
        var codes = new LaunchCodeStore(_clock);
        codes.Issue().Should().NotBe(codes.Issue());
    }

    // ── Session store ───────────────────────────────────────────────────────────────

    [Fact]
    public void Session_ValidatesAndStoresOnlyAHash()
    {
        var store = NewStore();
        var token = store.Create();

        store.Validate(token, out _).Should().BeTrue();
        File.ReadAllText(SessionsPath).Should().NotContain(token)
            .And.Contain(LocalSessionStore.Hash(token));
    }

    [Fact]
    public void Session_SurvivesARestart()
    {
        var token = NewStore().Create();

        NewStore().Validate(token, out _).Should().BeTrue();
    }

    [Fact]
    public void Session_UnknownTokenIsRefused()
    {
        NewStore().Validate("not-a-session", out _).Should().BeFalse();
    }

    [Fact]
    public void Session_ExpiresAfterThirtyDaysUnused()
    {
        var store = NewStore();
        var token = store.Create();

        _clock.Advance(TimeSpan.FromDays(30) + TimeSpan.FromMinutes(1));

        store.Validate(token, out _).Should().BeFalse();
    }

    [Fact]
    public void Session_SlidesWhenUsed()
    {
        var store = NewStore();
        var token = store.Create();

        _clock.Advance(TimeSpan.FromDays(20));
        store.Validate(token, out var refreshed).Should().BeTrue();
        refreshed.Should().BeTrue("a session used after the refresh window is extended");

        _clock.Advance(TimeSpan.FromDays(20));
        store.Validate(token, out _).Should().BeTrue("40 days in, but used 20 days ago");
    }

    [Fact]
    public void Session_IsNotRewrittenOnEveryRequest()
    {
        var store = NewStore();
        var token = store.Create();

        _clock.Advance(TimeSpan.FromMinutes(5));
        store.Validate(token, out var refreshed).Should().BeTrue();

        refreshed.Should().BeFalse();
    }

    [Fact]
    public void Session_Revoked_IsRefused()
    {
        var store = NewStore();
        var token = store.Create();

        store.Revoke(token);

        store.Validate(token, out _).Should().BeFalse();
    }

    // ── The handler ─────────────────────────────────────────────────────────────────

    private async Task<(AuthenticateResult Result, HttpContext Context)> AuthenticateAsync(
        LocalSessionStore store, Action<HttpContext> arrange)
    {
        var settings = new LocalSettings(6750, _dir, "ui-dist", "the-install-secret");
        var options = new Mock<IOptionsMonitor<AuthenticationSchemeOptions>>();
        options.Setup(o => o.Get(It.IsAny<string>())).Returns(new AuthenticationSchemeOptions());
        var handler = new LocalAuthHandler(options.Object, NullLoggerFactory.Instance, UrlEncoder.Default, settings, store);

        var context = new DefaultHttpContext();
        arrange(context);
        await handler.InitializeAsync(
            new AuthenticationScheme(LocalAuthHandler.SchemeName, null, typeof(LocalAuthHandler)), context);
        return (await handler.AuthenticateAsync(), context);
    }

    [Fact]
    public async Task Handler_SessionCookie_AuthenticatesAsTheLocalUser()
    {
        var store = NewStore();
        var token = store.Create();

        var (result, _) = await AuthenticateAsync(store,
            c => c.Request.Headers.Cookie = $"{LocalAuthHandler.SessionCookie}={token}");

        result.Succeeded.Should().BeTrue();
        result.Principal!.FindFirstValue(ClaimTypes.NameIdentifier).Should().Be(LocalAuthHandler.LocalUserId);
    }

    [Fact]
    public async Task Handler_InstallSecretBearer_AuthenticatesAsTheLocalUser()
    {
        var (result, _) = await AuthenticateAsync(NewStore(),
            c => c.Request.Headers.Authorization = "Bearer the-install-secret");

        result.Succeeded.Should().BeTrue();
        result.Principal!.FindFirstValue("sub").Should().Be(LocalAuthHandler.LocalUserId);
    }

    [Fact]
    public async Task Handler_InstallSecretAsHubAccessToken_Authenticates()
    {
        var (result, _) = await AuthenticateAsync(NewStore(), c =>
        {
            c.Request.Path = "/hubs/tasks";
            c.Request.QueryString = new QueryString("?access_token=the-install-secret");
        });

        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("Bearer wrong")]
    [InlineData("Bearer orkr_runner-token")]
    [InlineData("Basic dXNlcjpwYXNz")]
    public async Task Handler_OtherCredentials_AreNoResultNotFailure(string authorization)
    {
        var (result, _) = await AuthenticateAsync(NewStore(),
            c => c.Request.Headers.Authorization = authorization);

        result.None.Should().BeTrue("the runner hub screens its own orkr_ tokens");
    }

    [Fact]
    public async Task Handler_BearerTakesPrecedence_ACookieDoesNotRescueAWrongBearer()
    {
        var store = NewStore();
        var token = store.Create();

        var (result, _) = await AuthenticateAsync(store, c =>
        {
            c.Request.Headers.Authorization = "Bearer wrong";
            c.Request.Headers.Cookie = $"{LocalAuthHandler.SessionCookie}={token}";
        });

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task Handler_UnknownCookie_IsNotAuthenticated()
    {
        var (result, _) = await AuthenticateAsync(NewStore(),
            c => c.Request.Headers.Cookie = $"{LocalAuthHandler.SessionCookie}=forged");

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public void InstallSecret_Unconfigured_MatchesNothing()
    {
        var settings = new LocalSettings(6750, _dir, "ui-dist", "");

        settings.IsInstallSecret("").Should().BeFalse();
        settings.IsInstallSecret("anything").Should().BeFalse();
    }

    [Fact]
    public async Task Handler_RefreshedSession_ReissuesTheCookie()
    {
        var store = NewStore();
        var token = store.Create();
        _clock.Advance(TimeSpan.FromDays(2));

        var (result, context) = await AuthenticateAsync(store,
            c => c.Request.Headers.Cookie = $"{LocalAuthHandler.SessionCookie}={token}");

        result.Succeeded.Should().BeTrue();
        var setCookie = context.Response.Headers.SetCookie.ToString();
        setCookie.Should().Contain($"{LocalAuthHandler.SessionCookie}={token}")
            .And.Contain("httponly")
            .And.Contain("samesite=strict")
            .And.Contain("path=/")
            .And.Contain("max-age=2592000");
    }

    private sealed class SteppedClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
