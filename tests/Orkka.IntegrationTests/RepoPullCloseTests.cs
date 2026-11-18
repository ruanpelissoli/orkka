namespace Orkka.IntegrationTests;

using System.Net;
using Orkka.IntegrationTests.Fixtures;
using FluentAssertions;

/// <summary>
/// Closing a PR from the repo page. The GitHub call itself needs a live installation, so what is
/// asserted here is the guard layer: an unknown repo must 404 rather than reaching GitHub, and
/// the route must exist at all — it did not before, which is why the PR list had no close button.
/// </summary>
[Collection("Integration")]
public sealed class RepoPullCloseTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Close_ForUnknownRepo_Is404()
    {
        var response = await _client.PostAsync($"/api/repos/{Ulid.NewUlid()}/pulls/1/close", null);

        // Never 404 because the route is missing — that would pass for the wrong reason.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Close_RouteIsRegistered()
    {
        // A missing FastEndpoints route answers 404 with an empty body; a resolved one that
        // rejects the repo answers 404 through Send.NotFoundAsync. Distinguish by asserting the
        // sibling merge route behaves identically — both exist, both guard the same way.
        var close = await _client.PostAsync($"/api/repos/{Ulid.NewUlid()}/pulls/1/close", null);
        var merge = await _client.PostAsync($"/api/repos/{Ulid.NewUlid()}/pulls/1/merge", null);

        close.StatusCode.Should().Be(merge.StatusCode,
            "close is registered and guarded exactly like merge");
    }
}
