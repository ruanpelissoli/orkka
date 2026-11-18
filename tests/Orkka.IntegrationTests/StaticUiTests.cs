namespace Orkka.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Orkka.IntegrationTests.Fixtures;
using Orkka.Local;

/// <summary>
/// The host serves the built UI itself (<see cref="StaticUi"/>): build files are public, pages
/// need a session, unprefixed paths get a locale, and the host's own paths are never swallowed
/// by the SPA fallback.
/// </summary>
// In the shared collection only to run serially with it: it builds its own hosts, and two extra
// hosts migrating in parallel make the timing-sensitive tests there flaky.
[Collection("Integration")]
public sealed class StaticUiTests : IAsyncLifetime
{
    private const string IndexHtml = "<!doctype html><html><body><div id=\"root\">orkka-spa</div></body></html>";

    private readonly OrkkaApiFactory _factory = new();
    private readonly string _dist = Path.Combine(Path.GetTempPath(), "orkka-dist-" + Guid.NewGuid().ToString("N"));

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_dist, "assets"));
        File.WriteAllText(Path.Combine(_dist, "index.html"), IndexHtml);
        File.WriteAllText(Path.Combine(_dist, "assets", "index-abc123.js"), "console.log('orkka');");
        File.WriteAllText(Path.Combine(_dist, "favicon.svg"), "<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
        File.WriteAllBytes(Path.Combine(_dist, "assets", "inter-latin.woff2"), [0x77, 0x4F, 0x46, 0x32]);
        Directory.CreateDirectory(Path.Combine(_dist, "excalidraw", "fonts"));
        File.WriteAllBytes(Path.Combine(_dist, "excalidraw", "fonts", "Virgil.woff2"), [0x77, 0x4F, 0x46, 0x32]);
        _factory.Settings["Local:UiDir"] = _dist;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        try { Directory.Delete(_dist, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>No bearer, no cookie, no redirects followed: what an unknown local caller sees.</summary>
    private HttpClient Anonymous()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        client.DefaultRequestHeaders.Authorization = null;
        return client;
    }

    /// <summary>A browser session, obtained the way `./orkka open` gets one.</summary>
    private async Task<string> SessionCookieAsync()
    {
        var cli = _factory.CreateClient();
        var code = await (await cli.PostAsync("/local/launch-codes", null)).Content.ReadFromJsonAsync<CodeBody>();
        var response = await Anonymous().GetAsync($"/launch?code={Uri.EscapeDataString(code!.Code)}");
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var setCookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("orkka_s=", StringComparison.Ordinal));
        return setCookie.Split(';')[0];
    }

    private sealed record CodeBody(string Code);

    [Fact]
    public async Task A_page_without_a_session_is_the_signed_out_page()
    {
        var response = await Anonymous().GetAsync("/en/dashboard");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("./orkka open").And.NotContain("orkka-spa");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Fact]
    public async Task A_hashed_asset_is_public_and_cached_forever()
    {
        var response = await Anonymous().GetAsync("/assets/index-abc123.js");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/javascript");
        response.Headers.CacheControl!.ToString().Should().Contain("immutable").And.Contain("max-age=31536000");
        response.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff");
    }

    [Fact]
    public async Task Excalidraw_fonts_are_cached_forever_too()
    {
        var response = await Anonymous().GetAsync("/excalidraw/fonts/Virgil.woff2");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.ToString().Should().Contain("immutable");
    }

    [Fact]
    public async Task Fonts_and_icons_have_their_content_types()
    {
        var font = await Anonymous().GetAsync("/assets/inter-latin.woff2");
        font.Content.Headers.ContentType!.MediaType.Should().Be("font/woff2");

        var icon = await Anonymous().GetAsync("/favicon.svg");
        icon.StatusCode.Should().Be(HttpStatusCode.OK);
        icon.Content.Headers.ContentType!.MediaType.Should().Be("image/svg+xml");
        icon.Headers.CacheControl!.NoCache.Should().BeTrue();
    }

    [Fact]
    public async Task The_root_redirects_to_the_default_locale()
    {
        var response = await Anonymous().GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be("/en");
    }

    [Fact]
    public async Task The_locale_cookie_wins_and_the_query_is_kept()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/p/123/tasks?tab=chat");
        request.Headers.Add("Cookie", "NEXT_LOCALE=pt");
        request.Headers.AcceptLanguage.ParseAdd("de-DE");

        var response = await Anonymous().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be("/pt/p/123/tasks?tab=chat");
    }

    [Fact]
    public async Task Accept_language_picks_a_shipped_locale()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/dashboard");
        request.Headers.AcceptLanguage.ParseAdd("ja;q=1, de-DE;q=0.8, en;q=0.5");

        var response = await Anonymous().SendAsync(request);

        response.Headers.Location!.OriginalString.Should().Be("/de/dashboard");
    }

    [Fact]
    public async Task A_deep_link_with_a_session_is_the_app_with_its_security_headers()
    {
        var cookie = await SessionCookieAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, "/en/p/01JABCDEF/tasks/12");
        request.Headers.Add("Cookie", cookie);

        var response = await Anonymous().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("orkka-spa");
        response.Headers.CacheControl!.NoCache.Should().BeTrue();
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        csp.Should().Contain("default-src 'self'").And.Contain("script-src 'self'").And.Contain("frame-ancestors 'none'")
            .And.Contain($"ws://127.0.0.1:{OrkkaApiFactory.Port}").And.Contain("base-uri 'none'");
        csp.Should().NotContain("unsafe-eval");
        response.Headers.GetValues("Referrer-Policy").Should().Contain("no-referrer");
        response.Headers.GetValues("X-Frame-Options").Should().Contain("DENY");
    }

    [Fact]
    public async Task Index_html_itself_is_a_page_and_gated()
    {
        var response = await Anonymous().GetAsync("/index.html");

        response.StatusCode.Should().NotBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("orkka-spa");
    }

    [Fact]
    public async Task An_api_404_is_not_swallowed_by_the_fallback()
    {
        var cookie = await SessionCookieAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/definitely-not-an-endpoint");
        request.Headers.Add("Cookie", cookie);

        var response = await Anonymous().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("orkka-spa");
    }

    [Fact]
    public async Task A_missing_asset_is_a_plain_404_never_the_app()
    {
        var cookie = await SessionCookieAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, "/assets/gone-123.js");
        request.Headers.Add("Cookie", cookie);

        var response = await Anonymous().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("orkka-spa");
    }

    [Fact]
    public async Task Path_traversal_never_leaves_the_ui_folder()
    {
        var response = await Anonymous().GetAsync("/assets/..%2F..%2Forkka.db");

        response.StatusCode.Should().NotBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_missing_build_is_a_503_that_says_how_to_build_it()
    {
        await using var unbuilt = new OrkkaApiFactory();
        var client = unbuilt.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = null;

        var response = await client.GetAsync("/en/dashboard");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync()).Should().Contain("./orkka start").And.Contain("npm run build");
        response.Headers.Contains("Content-Security-Policy").Should().BeTrue();

        // The host's own endpoints still answer.
        (await client.GetAsync("/health")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
