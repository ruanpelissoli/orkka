namespace Orkka.Tests.Services;

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Orkka.LocalRunner.Contracts;
using Orkka.LocalRunner.Trackers;

/// <summary>Answers tracker HTTP calls from canned responses and records what was asked.</summary>
internal sealed class FakeTrackerHandler : HttpMessageHandler
{
    private readonly List<(HttpMethod Method, string PathPart, Func<string, (HttpStatusCode, string, string)> Answer)> _routes = [];

    public List<(HttpMethod Method, string Url, string? Body, string? Authorization)> Requests { get; } = [];

    public FakeTrackerHandler On(HttpMethod method, string pathPart, string json, HttpStatusCode status = HttpStatusCode.OK)
        => On(method, pathPart, _ => (status, json, "application/json"));

    public FakeTrackerHandler On(HttpMethod method, string pathPart, Func<string, (HttpStatusCode, string, string)> answer)
    {
        // Later registrations win, so a test can override one route of a shared setup.
        _routes.Insert(0, (method, pathPart, answer));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        var url = request.RequestUri!.AbsoluteUri;
        Requests.Add((request.Method, url, body, request.Headers.Authorization?.ToString()));
        var route = _routes.FirstOrDefault(r => r.Method == request.Method && url.Contains(r.PathPart, StringComparison.Ordinal));
        if (route.Answer is null)
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        var (status, text, mediaType) = route.Answer(body ?? "");
        return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, mediaType) };
    }

    public static string JqlOf(string body) => JsonDocument.Parse(body).RootElement.GetProperty("jql").GetString()!;
    public static string WiqlOf(string body) => JsonDocument.Parse(body).RootElement.GetProperty("query").GetString()!;
}

public sealed class JiraCloudClientTests
{
    private const string Token = "ATATT3xFfGF0-secret-jira-token";
    private static readonly TrackerCredential Credential = new("https://acme.atlassian.net", "dana@acme.com", Token);

    private static (JiraCloudClient Client, FakeTrackerHandler Handler) Create(Action<FakeTrackerHandler> setup)
    {
        var handler = new FakeTrackerHandler();
        setup(handler);
        return (new JiraCloudClient(new HttpClient(handler)), handler);
    }

    [Fact]
    public async Task Test_ReadsTheAccount_WithEmailAndTokenAsBasicAuth()
    {
        var (client, handler) = Create(h => h.On(HttpMethod.Get, "/rest/api/3/myself",
            """{"accountId":"5b10ac8d82e05b22cc7d4ef5","displayName":"Dana Silva","emailAddress":"dana@acme.com"}"""));

        var me = await client.TestAsync(Credential, CancellationToken.None);

        me.Should().Be(new TrackerIdentity("5b10ac8d82e05b22cc7d4ef5", "Dana Silva"));
        handler.Requests.Single().Authorization.Should()
            .Be("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"dana@acme.com:{Token}")));
    }

    [Fact]
    public async Task Test_401_SaysTheTokenWasRejected_WithoutTheToken()
    {
        var (client, _) = Create(h => h.On(HttpMethod.Get, "/myself", "Unauthorized", HttpStatusCode.Unauthorized));

        var act = () => client.TestAsync(Credential, CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<TrackerException>()).Which;
        ex.Message.Should().Contain("rejected the email + API token").And.Contain("id.atlassian.com").And.NotContain(Token);
    }

    [Fact]
    public async Task Test_AnHtmlPage_IsNotAJiraSite()
    {
        var (client, _) = Create(h => h.On(HttpMethod.Get, "/myself", _ => (HttpStatusCode.OK, "<html>login</html>", "text/html")));

        var act = () => client.TestAsync(Credential, CancellationToken.None);

        (await act.Should().ThrowAsync<TrackerException>()).Which.Message.Should().Contain("did not answer like a Jira Cloud site");
    }

    [Fact]
    public async Task Scopes_FollowPages_UntilTheLastOne()
    {
        var (client, handler) = Create(h => h
            .On(HttpMethod.Get, "startAt=0", """{"values":[{"key":"ACME","name":"Acme"},{"key":"OPS","name":"Ops"}],"isLast":false}""")
            .On(HttpMethod.Get, "startAt=2", """{"values":[{"key":"WEB","name":"Web"}],"isLast":true}"""));

        var scopes = await client.ScopesAsync(Credential, CancellationToken.None);

        scopes.Should().Equal(new TrackerScope("ACME", "Acme"), new TrackerScope("OPS", "Ops"), new TrackerScope("WEB", "Web"));
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Epics_SearchWithJql_AndLinkToTheIssue()
    {
        var (client, handler) = Create(h => h.On(HttpMethod.Post, "/rest/api/3/search/jql",
            """{"issues":[{"id":"10001","key":"ACME-12","fields":{"summary":"Checkout v2","status":{"name":"In Progress"}}}],"isLast":true}"""));

        var epics = await client.EpicsAsync(Credential, "ACME", "checkout", CancellationToken.None);

        epics.Should().Equal(new TrackerEpic("ACME-12", "Checkout v2", "In Progress", "https://acme.atlassian.net/browse/ACME-12"));
        var body = JsonDocument.Parse(handler.Requests.Single().Body!).RootElement;
        body.GetProperty("jql").GetString().Should().Contain("summary ~ \"checkout*\"");
        body.GetProperty("maxResults").GetInt32().Should().Be(20);
        body.GetProperty("fields").EnumerateArray().Select(f => f.GetString()).Should().Equal("summary", "status");
    }

    [Fact]
    public async Task Epics_AKeyThatDoesNotExist_IsNoMatch_NotAnError()
    {
        var (client, _) = Create(h => h.On(HttpMethod.Post, "/search/jql",
            """{"errorMessages":["An issue with key 'ACME-999' does not exist for field 'key'."],"errors":{}}""", HttpStatusCode.BadRequest));

        (await client.EpicsAsync(Credential, "ACME", "ACME-999", CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Statuses_AreTheUnionAcrossTicketTypes_WithCategories()
    {
        var (client, _) = Create(h => h.On(HttpMethod.Get, "/rest/api/3/project/ACME/statuses", """
            [
              {"name":"Story","statuses":[
                {"name":"To Do","statusCategory":{"key":"new"}},
                {"name":"In Progress","statusCategory":{"key":"indeterminate"}},
                {"name":"Done","statusCategory":{"key":"done"}}]},
              {"name":"Bug","statuses":[
                {"name":"To Do","statusCategory":{"key":"new"}},
                {"name":"In Review","statusCategory":{"key":"indeterminate"}}]},
              {"name":"Epic","statuses":[{"name":"Planning","statusCategory":{"key":"new"}}]}
            ]
            """));

        var statuses = await client.StatusesAsync(Credential, "ACME", CancellationToken.None);

        statuses.Should().Equal(
            new TrackerStatus("To Do", "todo"),
            new TrackerStatus("In Progress", "inprogress"),
            new TrackerStatus("Done", "done"),
            new TrackerStatus("In Review", "inprogress"));
    }

    [Fact]
    public async Task Preview_CountsAndSamples_TheEffectiveQuery()
    {
        var (client, handler) = Create(h => h
            .On(HttpMethod.Post, "/search/approximate-count", """{"count":7}""")
            .On(HttpMethod.Post, "/search/jql", """
                {"issues":[{"key":"ACME-31","fields":{"summary":"Fix cart","status":{"name":"To Do"},"issuetype":{"name":"Bug"}}}],"isLast":true}
                """));

        var preview = await client.PreviewAsync(Credential, "ACME", ["ACME-12"], "labels = orkka", false, null, TrackerScopeModes.Epics, CancellationToken.None);

        preview.MatchCount.Should().Be(7);
        preview.EffectiveQuery.Should().Be(
            "project = \"ACME\" AND assignee = currentUser() AND statusCategory = \"To Do\" AND parent in (\"ACME-12\") AND (labels = orkka)");
        preview.Sample.Should().Equal(new TrackerTicketPreview("ACME-31", "Fix cart", "Bug", "To Do", "https://acme.atlassian.net/browse/ACME-31"));
        FakeTrackerHandler.JqlOf(handler.Requests[0].Body!).Should().Be(preview.EffectiveQuery);
        FakeTrackerHandler.JqlOf(handler.Requests[1].Body!).Should().Be(preview.EffectiveQuery + " ORDER BY updated DESC");
    }

    [Fact]
    public async Task Preview_WithDescendants_FetchesTheEpicsChildrenFirst()
    {
        var (client, handler) = Create(h => h
            .On(HttpMethod.Post, "/search/approximate-count", """{"count":2}""")
            .On(HttpMethod.Post, "/search/jql", body => FakeTrackerHandler.JqlOf(body).StartsWith("parent in (\"ACME-12\")", StringComparison.Ordinal)
                && !FakeTrackerHandler.JqlOf(body).Contains("assignee", StringComparison.Ordinal)
                    ? (HttpStatusCode.OK, """{"issues":[{"key":"ACME-20"},{"key":"ACME-21"}],"isLast":true}""", "application/json")
                    : (HttpStatusCode.OK, """{"issues":[],"isLast":true}""", "application/json")));

        var preview = await client.PreviewAsync(Credential, "ACME", ["ACME-12"], null, true, null, TrackerScopeModes.Epics, CancellationToken.None);

        preview.EffectiveQuery.Should().Be(
            "project = \"ACME\" AND assignee = currentUser() AND statusCategory = \"To Do\" AND parent in (\"ACME-12\", \"ACME-20\", \"ACME-21\")");
        FakeTrackerHandler.JqlOf(handler.Requests[0].Body!).Should().Be("parent in (\"ACME-12\")");
    }

    [Fact]
    public async Task Preview_ABadExtraClause_ReturnsJirasOwnWords()
    {
        var (client, _) = Create(h => h.On(HttpMethod.Post, "/search/approximate-count",
            """{"errorMessages":["Error in the JQL Query: Expecting operator but got 'orkka'."],"errors":{}}""", HttpStatusCode.BadRequest));

        var act = () => client.PreviewAsync(Credential, "ACME", ["ACME-12"], "labels orkka", false, null, TrackerScopeModes.Epics, CancellationToken.None);

        (await act.Should().ThrowAsync<TrackerException>()).Which.Message.Should()
            .Be("Jira rejected the query: Error in the JQL Query: Expecting operator but got 'orkka'.");
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "refused access")]
    [InlineData(HttpStatusCode.NotFound, "could not find")]
    public void OtherErrors_AreDescribed(HttpStatusCode status, string expected)
        => JiraCloudClient.DescribeError(Credential, status, """{"errorMessages":["No project could be found with key 'X'."]}""", "project X")
            .Should().Contain(expected).And.Contain("No project could be found");
}

public sealed class AzureDevOpsClientTests
{
    private const string Pat = "k3x9zq7secretpat0000000000000000000000000000000000";
    private const string Org = "https://dev.azure.com/contoso";
    private static readonly TrackerCredential Credential = new(Org, "", Pat);

    private const string TypesJson = """
        {"value":[
          {"name":"Epic","states":[{"name":"New","category":"Proposed"},{"name":"Done","category":"Completed"}]},
          {"name":"Feature","states":[{"name":"New","category":"Proposed"},{"name":"Closed","category":"Completed"}]},
          {"name":"User Story","states":[{"name":"New","category":"Proposed"},{"name":"Active","category":"InProgress"},
            {"name":"Resolved","category":"Resolved"},{"name":"Closed","category":"Completed"},{"name":"Removed","category":"Removed"}]},
          {"name":"Bug","states":[{"name":"New","category":"Proposed"},{"name":"Active","category":"InProgress"},{"name":"Closed","category":"Completed"}]},
          {"name":"Test Case","states":[{"name":"Design","category":"Proposed"},{"name":"Ready","category":"InProgress"}]}
        ]}
        """;

    private const string CategoriesJson = """
        {"value":[
          {"referenceName":"Microsoft.EpicCategory","workItemTypes":[{"name":"Epic"}]},
          {"referenceName":"Microsoft.FeatureCategory","workItemTypes":[{"name":"Feature"}]},
          {"referenceName":"Microsoft.RequirementCategory","workItemTypes":[{"name":"User Story"}]},
          {"referenceName":"Microsoft.BugCategory","workItemTypes":[{"name":"Bug"}]},
          {"referenceName":"Microsoft.TestCaseCategory","workItemTypes":[{"name":"Test Case"}]}
        ]}
        """;

    private static (AzureDevOpsClient Client, FakeTrackerHandler Handler) Create(Action<FakeTrackerHandler> setup)
    {
        var handler = new FakeTrackerHandler();
        setup(handler);
        return (new AzureDevOpsClient(new HttpClient(handler)), handler);
    }

    [Fact]
    public async Task Test_ReadsTheAuthenticatedUser_WithThePatAsBasicAuth()
    {
        var (client, handler) = Create(h => h.On(HttpMethod.Get, "/_apis/connectionData",
            """{"authenticatedUser":{"id":"9f1c-identity","providerDisplayName":"Dana Silva"}}"""));

        (await client.TestAsync(Credential, CancellationToken.None)).Should().Be(new TrackerIdentity("9f1c-identity", "Dana Silva"));
        handler.Requests.Single().Authorization.Should().Be("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($":{Pat}")));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "application/json")]
    [InlineData(HttpStatusCode.NonAuthoritativeInformation, "text/html")]
    [InlineData(HttpStatusCode.Redirect, "text/html")]
    public async Task Test_ABadPat_IsSaidPlainly_WithoutThePat(HttpStatusCode status, string mediaType)
    {
        var (client, _) = Create(h => h.On(HttpMethod.Get, "/_apis/connectionData", _ => (status, "<html>Sign in</html>", mediaType)));

        var act = () => client.TestAsync(Credential, CancellationToken.None);

        (await act.Should().ThrowAsync<TrackerException>()).Which.Message.Should()
            .Contain("rejected the personal access token").And.Contain("Work Items (Read & write)").And.NotContain(Pat);
    }

    [Fact]
    public async Task Scopes_AreTheProjects()
    {
        var (client, _) = Create(h => h.On(HttpMethod.Get, "/_apis/projects",
            """{"count":2,"value":[{"id":"1","name":"Web"},{"id":"2","name":"Api Platform"}]}"""));

        (await client.ScopesAsync(Credential, CancellationToken.None)).Should()
            .Equal(new TrackerScope("Api Platform", "Api Platform"), new TrackerScope("Web", "Web"));
    }

    [Fact]
    public async Task Epics_RunWiql_ThenReadTitles_InWiqlOrder()
    {
        var (client, handler) = Create(h => h
            .On(HttpMethod.Post, "/Api%20Platform/_apis/wit/wiql", """{"workItems":[{"id":42},{"id":7}]}""")
            .On(HttpMethod.Post, "/_apis/wit/workitemsbatch", """
                {"count":2,"value":[
                  {"id":7,"fields":{"System.Title":"Billing","System.State":"New","System.TeamProject":"Api Platform"}},
                  {"id":42,"fields":{"System.Title":"Checkout v2","System.State":"Active","System.TeamProject":"Api Platform"}}]}
                """));

        var epics = await client.EpicsAsync(Credential, "Api Platform", "check", CancellationToken.None);

        epics.Should().Equal(
            new TrackerEpic("42", "Checkout v2", "Active", $"{Org}/Api%20Platform/_workitems/edit/42"),
            new TrackerEpic("7", "Billing", "New", $"{Org}/Api%20Platform/_workitems/edit/7"));
        handler.Requests[0].Url.Should().Contain("$top=20");
        FakeTrackerHandler.WiqlOf(handler.Requests[0].Body!).Should().Contain("[System.Title] CONTAINS 'check'");
    }

    [Fact]
    public async Task Statuses_AreTheTicketTypesStates_WithoutContainersOrRemoved()
    {
        var (client, _) = Create(h => h
            .On(HttpMethod.Get, "/_apis/wit/workitemtypes?", TypesJson)
            .On(HttpMethod.Get, "/_apis/wit/workitemtypecategories", CategoriesJson));

        var statuses = await client.StatusesAsync(Credential, "Web", CancellationToken.None);

        statuses.Should().Equal(
            new TrackerStatus("New", "todo", "Proposed"),
            new TrackerStatus("Active", "inprogress", "InProgress"),
            new TrackerStatus("Resolved", "inprogress", "Resolved"),
            new TrackerStatus("Closed", "done", "Completed"));
    }

    [Fact]
    public async Task Preview_FindsChildren_ThenFiltersThemFlat()
    {
        var (client, handler) = Create(h => h
            .On(HttpMethod.Get, "/_apis/wit/workitemtypes?", TypesJson)
            .On(HttpMethod.Get, "/_apis/wit/workitemtypecategories", CategoriesJson)
            .On(HttpMethod.Post, "/_apis/wit/wiql", body => FakeTrackerHandler.WiqlOf(body).Contains("WorkItemLinks", StringComparison.Ordinal)
                ? (HttpStatusCode.OK, """
                    {"workItemRelations":[
                      {"rel":null,"source":null,"target":{"id":42}},
                      {"rel":"System.LinkTypes.Hierarchy-Forward","source":{"id":42},"target":{"id":101}},
                      {"rel":"System.LinkTypes.Hierarchy-Forward","source":{"id":42},"target":{"id":102}},
                      {"rel":"System.LinkTypes.Hierarchy-Forward","source":{"id":42},"target":{"id":101}}]}
                    """, "application/json")
                : (HttpStatusCode.OK, """{"workItems":[{"id":102}]}""", "application/json"))
            .On(HttpMethod.Post, "/_apis/wit/workitemsbatch", """
                {"value":[{"id":102,"fields":{"System.Title":"Fix login","System.State":"Active","System.WorkItemType":"Bug","System.TeamProject":"Web"}}]}
                """));

        var preview = await client.PreviewAsync(Credential, "Web", ["42"], "[System.Tags] CONTAINS 'orkka'", false, null, TrackerScopeModes.Epics, CancellationToken.None);

        preview.MatchCount.Should().Be(1);
        preview.Sample.Should().Equal(new TrackerTicketPreview("102", "Fix login", "Bug", "Active", $"{Org}/Web/_workitems/edit/102"));
        var flat = handler.Requests.Where(r => r.Body?.Contains("FROM WorkItems ", StringComparison.Ordinal) == true)
            .Select(r => FakeTrackerHandler.WiqlOf(r.Body!)).Single();
        flat.Should().Contain("[System.Id] IN (101, 102)")
            .And.Contain("[System.AssignedTo] = @Me")
            .And.Contain("[System.State] IN ('New')")
            .And.NotContain("NOT IN ('Closed'")
            .And.Contain("AND ([System.Tags] CONTAINS 'orkka')");
        preview.EffectiveQuery.Should().Contain("MODE (MustContain)").And.Contain(flat);
    }

    [Fact]
    public async Task Preview_WithDescendants_WalksTheTreeRecursively()
    {
        var (client, handler) = Create(h => h
            .On(HttpMethod.Get, "/_apis/wit/workitemtypes?", TypesJson)
            .On(HttpMethod.Get, "/_apis/wit/workitemtypecategories", CategoriesJson)
            .On(HttpMethod.Post, "/_apis/wit/wiql", """{"workItemRelations":[{"rel":null,"source":null,"target":{"id":42}}]}"""));

        var preview = await client.PreviewAsync(Credential, "Web", ["#42"], null, true, null, TrackerScopeModes.Epics, CancellationToken.None);

        preview.MatchCount.Should().Be(0);
        FakeTrackerHandler.WiqlOf(handler.Requests.First(r => r.Method == HttpMethod.Post).Body!).Should().EndWith("MODE (Recursive)");
    }

    [Fact]
    public async Task Preview_ABadExtraClause_ReturnsAzureDevOpsOwnWords()
    {
        var (client, _) = Create(h => h
            .On(HttpMethod.Get, "/_apis/wit/workitemtypes?", TypesJson)
            .On(HttpMethod.Get, "/_apis/wit/workitemtypecategories", CategoriesJson)
            .On(HttpMethod.Post, "/_apis/wit/wiql", body => FakeTrackerHandler.WiqlOf(body).Contains("WorkItemLinks", StringComparison.Ordinal)
                ? (HttpStatusCode.OK, """{"workItemRelations":[{"rel":"x","source":{"id":42},"target":{"id":101}}]}""", "application/json")
                : (HttpStatusCode.BadRequest, """{"message":"TF51005: The query references a field that does not exist. The error is caused by «[System.Tagz]».","typeName":"x"}""", "application/json")));

        var act = () => client.PreviewAsync(Credential, "Web", ["42"], "[System.Tagz] = 'x'", false, null, TrackerScopeModes.Epics, CancellationToken.None);

        (await act.Should().ThrowAsync<TrackerException>()).Which.Message.Should()
            .StartWith("Azure DevOps rejected the query: TF51005");
    }

    [Fact]
    public async Task Preview_NonNumericEpics_AreRefused()
    {
        var (client, _) = Create(_ => { });

        var act = () => client.PreviewAsync(Credential, "Web", ["ACME-1"], null, false, null, TrackerScopeModes.Epics, CancellationToken.None);

        (await act.Should().ThrowAsync<TrackerException>()).Which.Message.Should().Contain("not an Azure DevOps work item id");
    }
}

/// <summary>The store, the file secret store, and the service that ties them to the host's op
/// and the Hello — including the hard rule that a token never leaves the machine.</summary>
public sealed class TrackerStoreAndServiceTests : IDisposable
{
    private const string Token = "ATATT3xFfGF0-never-leaves-this-machine";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orkka-trackers-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private TrackerConnectionEntry JiraEntry(DateTimeOffset? checkedAt = null) => new()
    {
        Id = "jira-acme",
        Kind = TrackerKinds.Jira,
        SiteUrl = "https://acme.atlassian.net",
        Email = "dana@acme.com",
        AccountId = "acc-1",
        DisplayName = "Dana",
        Healthy = true,
        CheckedAt = checkedAt,
        AddedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void Store_RoundTrips_ThroughTheProfileFile()
    {
        var store = new TrackerStore(_root);
        store.Upsert(JiraEntry(DateTimeOffset.UtcNow));
        store.Upsert(JiraEntry() with { Id = "ado-contoso", Kind = TrackerKinds.AzureDevOps, SiteUrl = "https://dev.azure.com/contoso", Email = null });

        var reread = new TrackerStore(_root);
        reread.List().Select(e => e.Id).Should().BeEquivalentTo(["jira-acme", "ado-contoso"]);
        reread.Get("JIRA-ACME")!.Email.Should().Be("dana@acme.com");
        reread.FilePath.Should().Be(Path.Combine(_root, "workspaces", "default", "trackers.json"));
        reread.NextId(TrackerKinds.Jira, "https://acme.atlassian.net").Should().Be("jira-acme-2");
        reread.FindSame(TrackerKinds.Jira, "https://ACME.atlassian.net", "Dana@acme.com")!.Id.Should().Be("jira-acme");

        reread.Remove("jira-acme").Should().BeTrue();
        new TrackerStore(_root).List().Select(e => e.Id).Should().Equal("ado-contoso");
        File.ReadAllText(reread.FilePath).Should().NotContain(Token);
        Directory.GetFiles(Path.GetDirectoryName(reread.FilePath)!, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public void Update_DoesNotResurrectARemovedEntry()
    {
        var store = new TrackerStore(_root);
        store.Upsert(JiraEntry());
        store.Remove("jira-acme");

        store.Update("jira-acme", e => e with { Healthy = false });

        store.List().Should().BeEmpty();
    }

    [Fact]
    public void FileSecretStore_RoundTrips_OwnerOnly()
    {
        var dir = TrackerPaths.SecretsDir(_root, "default");
        var secrets = new FileSecretStore(dir);

        secrets.Get("jira-acme").Should().BeNull();
        secrets.Set("jira-acme", Token);
        secrets.Get("jira-acme").Should().Be(Token);
        if (!OperatingSystem.IsWindows())
            File.GetUnixFileMode(Path.Combine(dir, "jira-acme")).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        secrets.Delete("jira-acme");
        secrets.Get("jira-acme").Should().BeNull();
        secrets.Delete("jira-acme");

        var escape = () => secrets.Set("../evil", "x");
        escape.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void WindowsCredentialManager_RoundTrips()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var store = new WindowsCredentialSecretStore("orkka-test-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            store.Get("jira-acme").Should().BeNull();
            store.Set("jira-acme", Token);
            store.Get("jira-acme").Should().Be(Token);
            store.Set("jira-acme", Token + "-rotated");
            store.Get("jira-acme").Should().Be(Token + "-rotated");
        }
        finally
        {
            store.Delete("jira-acme");
        }
        store.Get("jira-acme").Should().BeNull();
        store.Delete("jira-acme");
    }

    private (TrackerService Service, FakeTrackerHandler Handler, ListLogger Logger, FakeClock Clock) CreateService()
    {
        var handler = new FakeTrackerHandler();
        var http = new HttpClient(handler);
        var logger = new ListLogger();
        var clock = new FakeClock(DateTimeOffset.Parse("2026-10-26T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var secrets = new FileSecretStore(TrackerPaths.SecretsDir(_root));
        var service = new TrackerService(new TrackerStore(_root), secrets, new JiraCloudClient(http), new AzureDevOpsClient(http), logger, clock);
        return (service, handler, logger, clock);
    }

    [Fact]
    public async Task UnknownConnection_PointsAtTrackerList()
    {
        var (service, _, _, _) = CreateService();

        var result = await service.QueryAsync(new TrackerQueryRequest("jira-nope", TrackerQueryKinds.Scopes), CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("./orkka tracker list");
    }

    [Fact]
    public async Task MissingSecret_SaysHowToAddItAgain()
    {
        var (service, _, _, _) = CreateService();
        service.Store.Upsert(JiraEntry(DateTimeOffset.UtcNow));

        var result = await service.QueryAsync(new TrackerQueryRequest("jira-acme", TrackerQueryKinds.Scopes), CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("./orkka tracker add jira --site https://acme.atlassian.net --email dana@acme.com");
    }

    [Fact]
    public async Task Queries_DispatchByKind()
    {
        var (service, handler, _, _) = CreateService();
        service.Store.Upsert(JiraEntry(DateTimeOffset.UtcNow));
        service.Secrets.Set("jira-acme", Token);
        handler.On(HttpMethod.Get, "/rest/api/3/project/search", """{"values":[{"key":"ACME","name":"Acme"}],"isLast":true}""");

        var result = await service.QueryAsync(new TrackerQueryRequest("jira-acme", TrackerQueryKinds.Scopes), CancellationToken.None);

        result.Should().BeEquivalentTo(new TrackerQueryResult(true, Scopes: [new TrackerScope("ACME", "Acme")]));
    }

    [Fact]
    public async Task TheToken_NeverReachesAnError_TheLogs_OrTheHello()
    {
        var (service, handler, logger, _) = CreateService();
        service.Store.Upsert(JiraEntry());
        service.Secrets.Set("jira-acme", Token);
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"dana@acme.com:{Token}"));
        // A tracker (or proxy) that echoes the credentials back in its error text.
        handler.On(HttpMethod.Post, "/search/approximate-count",
            _ => (HttpStatusCode.BadRequest, $$"""{"errorMessages":["bad query from {{Token}} / Basic {{basic}}"]}""", "application/json"));
        handler.On(HttpMethod.Get, "/myself", _ => (HttpStatusCode.InternalServerError, $$"""{"message":"boom {{Token}}"}""", "application/json"));
        handler.On(HttpMethod.Get, "/project/search", _ => throw new HttpRequestException($"socket closed while sending {Token}"));

        var preview = await service.QueryAsync(
            new TrackerQueryRequest("jira-acme", TrackerQueryKinds.Preview, "ACME", EpicKeys: ["ACME-1"]), CancellationToken.None);
        var scopes = await service.QueryAsync(new TrackerQueryRequest("jira-acme", TrackerQueryKinds.Scopes), CancellationToken.None);
        var test = await service.QueryAsync(new TrackerQueryRequest("jira-acme", TrackerQueryKinds.Test), CancellationToken.None);
        var hello = await service.ListConnectionsAsync(CancellationToken.None);

        preview.Ok.Should().BeFalse();
        scopes.Ok.Should().BeFalse();
        test.Ok.Should().BeFalse();
        string[] leaks = [Token, basic];
        foreach (var leak in leaks)
        {
            preview.Error.Should().NotContain(leak).And.Contain("***");
            scopes.Error.Should().NotContain(leak);
            test.Error.Should().NotContain(leak);
            JsonSerializer.Serialize(test).Should().NotContain(leak);
            JsonSerializer.Serialize(hello).Should().NotContain(leak);
            logger.Lines.Should().NotContain(l => l.Contains(leak, StringComparison.Ordinal));
            File.ReadAllText(service.Store.FilePath).Should().NotContain(leak);
        }
        JsonSerializer.Serialize(hello).Should().NotContain("dana@acme.com");
        hello.Single().Healthy.Should().BeFalse();
    }

    [Fact]
    public async Task Hello_RechecksHealth_AtMostEvery15Minutes_AndNeverThrows()
    {
        var (service, handler, _, clock) = CreateService();
        service.Store.Upsert(JiraEntry(clock.GetUtcNow().AddMinutes(-20)));
        service.Secrets.Set("jira-acme", Token);
        handler.On(HttpMethod.Get, "/myself", """{"accountId":"acc-2","displayName":"Dana S."}""");

        var first = await service.ListConnectionsAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(10));
        await service.ListConnectionsAsync(CancellationToken.None);

        handler.Requests.Should().HaveCount(1);
        first.Single().Should().Be(new RunnerTrackerConnection("jira-acme", TrackerKinds.Jira, "https://acme.atlassian.net",
            "acc-2", "Dana S.", true, null, clock.GetUtcNow().AddMinutes(-10)));

        clock.Advance(TimeSpan.FromMinutes(6));
        handler.On(HttpMethod.Get, "/myself", "nope", HttpStatusCode.Unauthorized);
        var later = await service.ListConnectionsAsync(CancellationToken.None);
        later.Single().Healthy.Should().BeFalse();
        later.Single().HealthDetail.Should().Contain("rejected the email + API token");

        File.WriteAllText(service.Store.FilePath, "{ not json");
        (await service.ListConnectionsAsync(CancellationToken.None)).Should().BeEmpty();
    }

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class ListLogger : ILogger<TrackerService>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception) + exception);
    }
}
