namespace Orkka.Tests.Services;

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orkka.LocalRunner.Contracts;
using Orkka.LocalRunner.Trackers;

/// <summary>
/// Adding a tracker connection from the settings page (<c>AddTrackerConnection</c>, P6): the
/// token is tested, stored in the secret store (a file store in a sandbox Orkka home here), and
/// nothing that goes back — result, errors, trackers.json, the request's own ToString — carries it.
/// </summary>
public sealed class TrackerConnectionManagerTests : IDisposable
{
    private const string Token = "ATATT3xFfGF0-typed-into-the-settings-page";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orkka-connect-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private (TrackerConnectionManager Manager, FakeTrackerHandler Handler, FileSecretStore Secrets) Create(Action<FakeTrackerHandler> setup)
    {
        var handler = new FakeTrackerHandler();
        setup(handler);
        var http = new HttpClient(handler);
        var secrets = new FileSecretStore(TrackerPaths.SecretsDir(_root, "default"));
        var service = new TrackerService(new TrackerStore(_root), secrets, new JiraCloudClient(http), new AzureDevOpsClient(http),
            NullLogger<TrackerService>.Instance);
        return (new TrackerConnectionManager(service), handler, secrets);
    }

    [Fact]
    public async Task AWorkingJiraToken_IsStored_AndOnlyMetadataComesBack()
    {
        var (manager, handler, secrets) = Create(h => h.On(HttpMethod.Get, "/rest/api/3/myself",
            """{"accountId":"acc-dana","displayName":"Dana"}"""));

        var result = await manager.AddAsync(new AddTrackerConnectionRequest("jira", "acme.atlassian.net", "dana@acme.com", $"  {Token} "),
            CancellationToken.None);

        result.Ok.Should().BeTrue();
        result.Connection.Should().BeEquivalentTo(new
        {
            Id = "jira-acme", Kind = TrackerKinds.Jira, SiteUrl = "https://acme.atlassian.net", AccountId = "acc-dana",
            DisplayName = "Dana", Healthy = true,
        });
        secrets.Get("jira-acme").Should().Be(Token);
        handler.Requests.Single().Url.Should().Be("https://acme.atlassian.net/rest/api/3/myself");
        JsonSerializer.Serialize(result).Should().NotContain(Token);
        File.ReadAllText(TrackerPaths.TrackersFile(_root)).Should().NotContain(Token).And.Contain("dana@acme.com");

        // Adding the same account again replaces the token, not the connection.
        var again = await manager.AddAsync(new AddTrackerConnectionRequest("jira", "https://acme.atlassian.net", "Dana@acme.com", Token + "-2"),
            CancellationToken.None);
        again.Connection!.Id.Should().Be("jira-acme");
        manager.Service.Store.List().Should().ContainSingle();
        secrets.Get("jira-acme").Should().Be(Token + "-2");
    }

    [Fact]
    public async Task ARejectedToken_StoresNothing_AndTheErrorDoesNotQuoteIt()
    {
        var (manager, _, secrets) = Create(h => h.On(HttpMethod.Get, "/_apis/connectionData",
            _ => (HttpStatusCode.Unauthorized, $"bad token {Token}", "text/plain")));

        var result = await manager.AddAsync(new AddTrackerConnectionRequest("ado", "contoso", null, Token), CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("rejected the personal access token").And.Contain("Nothing was saved").And.NotContain(Token);
        result.Connection.Should().BeNull();
        manager.Service.Store.List().Should().BeEmpty();
        secrets.Get("ado-contoso").Should().BeNull();
    }

    [Theory]
    [InlineData("linear", "x", "a@b.c", "Pick Jira Cloud, Azure DevOps or GitHub")]
    [InlineData("github", "github.acme.corp", null, "GitHub Enterprise Server is not supported")]
    [InlineData("jira", "not a site!", "a@b.c", "Jira Cloud site")]
    [InlineData("jira", "acme.atlassian.net", null, "email")]
    [InlineData("jira", "acme.atlassian.net", "a@b.c", "No token")]
    public async Task BadInput_IsRefused_BeforeAnythingIsAsked(string kind, string site, string? email, string expected)
    {
        var (manager, handler, _) = Create(_ => { });

        var result = await manager.AddAsync(
            new AddTrackerConnectionRequest(kind, site, email, expected == "No token" ? "  " : Token), CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain(expected);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Remove_DeletesTheTokenAndTheEntry()
    {
        var (manager, _, secrets) = Create(h => h.On(HttpMethod.Get, "/rest/api/3/myself", """{"accountId":"a","displayName":"D"}"""));
        await manager.AddAsync(new AddTrackerConnectionRequest("jira", "acme.atlassian.net", "dana@acme.com", Token), CancellationToken.None);

        manager.Remove("jira-acme").Ok.Should().BeTrue();

        secrets.Get("jira-acme").Should().BeNull();
        manager.Service.Store.List().Should().BeEmpty();
        manager.Remove("jira-acme").Ok.Should().BeFalse();
    }

    [Fact]
    public void TheRequest_NeverPrintsItsToken()
    {
        var request = new AddTrackerConnectionRequest("jira", "acme.atlassian.net", "dana@acme.com", Token);

        request.ToString().Should().NotContain(Token).And.Contain("***");
        $"{request}".Should().NotContain(Token);
    }
}
