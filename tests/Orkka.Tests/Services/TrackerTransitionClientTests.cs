namespace Orkka.Tests.Services;

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Orkka.LocalRunner.Trackers;

public sealed class JiraTransitionTests
{
    private const string Token = "ATATT3xFfGF0-secret-jira-token";
    private static readonly TrackerCredential Credential = new("https://acme.atlassian.net", "dana@acme.com", Token);

    private const string Transitions = """
        {"transitions":[
          {"id":"11","name":"Start work","to":{"name":"In Progress"}},
          {"id":"21","name":"Send to review","to":{"name":"Code Review"}},
          {"id":"31","name":"Done","to":{"name":"Closed"}}]}
        """;

    private static (JiraCloudClient Client, FakeTrackerHandler Handler) Create(string currentStatus)
    {
        var handler = new FakeTrackerHandler()
            .On(HttpMethod.Get, "/rest/api/3/issue/SHOP-12?fields=status", $$"""{"key":"SHOP-12","fields":{"status":{"name":"{{currentStatus}}"} } }""")
            .On(HttpMethod.Get, "/rest/api/3/issue/SHOP-12/transitions", Transitions)
            .On(HttpMethod.Post, "/rest/api/3/issue/SHOP-12/transitions", _ => (HttpStatusCode.NoContent, "", "application/json"));
        return (new JiraCloudClient(new HttpClient(handler)), handler);
    }

    [Fact]
    public async Task MovesThroughTheTransitionWhoseDestinationIsTheTarget()
    {
        var (client, handler) = Create("To Do");

        var moved = await client.TransitionAsync(Credential, "SHOP-12", "10012", "in progress", CancellationToken.None);

        moved.Should().BeTrue();
        var post = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        JsonDocument.Parse(post.Body!).RootElement.GetProperty("transition").GetProperty("id").GetString().Should().Be("11");
    }

    [Fact]
    public async Task FallsBackToTheTransitionsOwnName()
    {
        var (client, handler) = Create("To Do");

        await client.TransitionAsync(Credential, "SHOP-12", "10012", "Send to review", CancellationToken.None);

        JsonDocument.Parse(handler.Requests.Single(r => r.Method == HttpMethod.Post).Body!).RootElement
            .GetProperty("transition").GetProperty("id").GetString().Should().Be("21");
    }

    [Fact]
    public async Task AlreadyInTheStatus_WritesNothing()
    {
        var (client, handler) = Create("In Progress");

        var moved = await client.TransitionAsync(Credential, "SHOP-12", "10012", "IN PROGRESS", CancellationToken.None);

        moved.Should().BeFalse();
        handler.Requests.Should().ContainSingle("only the status was read");
    }

    [Fact]
    public async Task NoMatchingTransition_NamesTheOnesThatExist()
    {
        var (client, handler) = Create("To Do");

        var act = () => client.TransitionAsync(Credential, "SHOP-12", "10012", "Merged", CancellationToken.None);

        (await act.Should().ThrowAsync<TrackerException>()).Which.Message.Should().Be(
            "No transition to 'Merged' from 'To Do' in this workflow — available: In Progress, Code Review, Closed.");
        handler.Requests.Should().NotContain(r => r.Method == HttpMethod.Post);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "Your Jira account cannot move this ticket")]
    [InlineData(HttpStatusCode.NotFound, "Ticket not found")]
    public async Task ForbiddenAndNotFound_SayWhatTheyMean(HttpStatusCode status, string expected)
    {
        var (client, handler) = Create("To Do");
        handler.On(HttpMethod.Post, "/rest/api/3/issue/SHOP-12/transitions",
            _ => (status, """{"errorMessages":["nope"]}""", "application/json"));

        var act = () => client.TransitionAsync(Credential, "SHOP-12", "10012", "In Progress", CancellationToken.None);

        (await act.Should().ThrowAsync<TrackerException>()).Which.Message.Should().Contain(expected).And.NotContain(Token);
    }
}

public sealed class AdoTransitionTests
{
    private const string Pat = "ado-pat-0123456789abcdefghijklmnop";
    private static readonly TrackerCredential Credential = new("https://dev.azure.com/contoso", "", Pat);

    private static (AzureDevOpsClient Client, FakeTrackerHandler Handler) Create(string currentState)
    {
        var handler = new FakeTrackerHandler()
            .On(HttpMethod.Get, "/_apis/wit/workitems/42?fields=System.State", $$"""{"id":42,"fields":{"System.State":"{{currentState}}"} }""")
            .On(HttpMethod.Patch, "/_apis/wit/workitems/42?api-version=7.1", """{"id":42,"fields":{"System.State":"Active"}}""");
        return (new AzureDevOpsClient(new HttpClient(new ContentTypeRecorder(handler))), handler);
    }

    [Fact]
    public async Task SetsTheStateWithAJsonPatch()
    {
        var (client, handler) = Create("New");

        var moved = await client.TransitionAsync(Credential, "42", "42", "Active", CancellationToken.None);

        moved.Should().BeTrue();
        var patch = handler.Requests.Single(r => r.Method == HttpMethod.Patch);
        patch.Url.Should().Be("https://dev.azure.com/contoso/_apis/wit/workitems/42?api-version=7.1");
        var op = JsonDocument.Parse(patch.Body!).RootElement.EnumerateArray().Single();
        op.GetProperty("op").GetString().Should().Be("add");
        op.GetProperty("path").GetString().Should().Be("/fields/System.State");
        op.GetProperty("value").GetString().Should().Be("Active");
        ContentTypeRecorder.Last.Should().Be("application/json-patch+json");
    }

    [Fact]
    public async Task AlreadyInTheState_WritesNothing()
    {
        var (client, handler) = Create("active");

        (await client.TransitionAsync(Credential, "42", "42", "Active", CancellationToken.None)).Should().BeFalse();

        handler.Requests.Should().NotContain(r => r.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task AnInvalidState_ReturnsAzureDevOpsOwnMessage()
    {
        var (client, handler) = Create("New");
        handler.On(HttpMethod.Patch, "/_apis/wit/workitems/42", """
            {"message":"TF401320: Rule Error for field State. Error code: Required, HasValues, LimitedToValues, AllowsOldValue, InvalidEmpty."}
            """, HttpStatusCode.BadRequest);

        var act = () => client.TransitionAsync(Credential, "42", "42", "Shipped", CancellationToken.None);

        (await act.Should().ThrowAsync<TrackerException>()).Which.Message.Should()
            .Contain("refused the state 'Shipped'").And.Contain("TF401320").And.NotContain(Pat);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "cannot move this work item")]
    [InlineData(HttpStatusCode.NotFound, "Work item not found")]
    public async Task ForbiddenAndNotFound_SayWhatTheyMean(HttpStatusCode status, string expected)
    {
        var (client, handler) = Create("New");
        handler.On(HttpMethod.Get, "/_apis/wit/workitems/42", "{}", status);

        var act = () => client.TransitionAsync(Credential, "42", "42", "Active", CancellationToken.None);

        (await act.Should().ThrowAsync<TrackerException>()).Which.Message.Should().Contain(expected);
    }

    /// <summary>The shared fake records bodies, not content types; this sits in front of it.</summary>
    private sealed class ContentTypeRecorder(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public static string? Last { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Patch)
                Last = request.Content?.Headers.ContentType?.MediaType;
            return base.SendAsync(request, ct);
        }
    }
}
