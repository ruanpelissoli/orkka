namespace Orkka.Tests.Services;

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orkka.LocalRunner;
using Orkka.LocalRunner.Contracts;
using Orkka.LocalRunner.Trackers;

/// <summary>
/// A fake GitHub GraphQL endpoint: answers by matching text in the query, records every request
/// (query, variables, Authorization header), and can attach response headers.
/// </summary>
internal sealed class FakeGitHubHandler : HttpMessageHandler
{
    private readonly List<(string QueryPart, Func<JsonElement, Answer> Respond)> _routes = [];

    public sealed record Answer(HttpStatusCode Status, string Body, Dictionary<string, string>? Headers = null);

    public List<(string Url, string Query, JsonElement Variables, string? Authorization)> Requests { get; } = [];

    public FakeGitHubHandler On(string queryPart, string json, Dictionary<string, string>? headers = null)
        => On(queryPart, _ => new Answer(HttpStatusCode.OK, json, headers));

    public FakeGitHubHandler On(string queryPart, Func<JsonElement, Answer> respond)
    {
        _routes.Insert(0, (queryPart, respond));
        return this;
    }

    public IEnumerable<string> Queries => Requests.Select(r => r.Query);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = await request.Content!.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var query = doc.RootElement.GetProperty("query").GetString()!;
        var variables = doc.RootElement.TryGetProperty("variables", out var v) ? v.Clone() : default;
        Requests.Add((request.RequestUri!.AbsoluteUri, query, variables, request.Headers.Authorization?.ToString()));
        var route = _routes.FirstOrDefault(r => query.Contains(r.QueryPart, StringComparison.Ordinal));
        if (route.Respond is null)
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"errors":[{"message":"no route"}]}""") };
        var answer = route.Respond(variables);
        var response = new HttpResponseMessage(answer.Status) { Content = new StringContent(answer.Body, Encoding.UTF8, "application/json") };
        foreach (var (name, value) in answer.Headers ?? [])
            response.Headers.TryAddWithoutValidation(name, value);
        return response;
    }
}

public sealed class GitHubProjectsHelperTests
{
    [Theory]
    [InlineData("acme/shop#12", "acme", "shop", 12)]
    [InlineData("a-b/my.repo_x#1", "a-b", "my.repo_x", 1)]
    public void Keys_RoundTrip(string key, string owner, string repo, int number)
    {
        GitHubProjects.TryParseKey(key, out var o, out var r, out var n).Should().BeTrue();
        (o, r, n).Should().Be((owner, repo, number));
        GitHubProjects.Key($"{owner}/{repo}", number).Should().Be(key);
    }

    [Theory]
    [InlineData("SHOP-12")]
    [InlineData("acme/shop")]
    [InlineData("acme/shop#x")]
    [InlineData("acme/sh\"op#1")]
    [InlineData("ac me/shop#1")]
    public void Keys_ThatAreNotGitHubIssues_DoNotParse(string key)
        => GitHubProjects.TryParseKey(key, out _, out _, out _).Should().BeFalse();

    [Theory]
    [InlineData(null, false, "todo")]
    [InlineData("No Status", false, "todo")]
    [InlineData("Todo", false, "todo")]
    [InlineData("Ready", false, "todo")]
    [InlineData("Backlog", false, "todo")]
    [InlineData("In Progress", false, "inprogress")]
    [InlineData("In Review", false, "inprogress")]
    [InlineData("QA", false, "inprogress")]
    [InlineData("Done", false, "done")]
    [InlineData("Shipped", false, "done")]
    [InlineData("In Progress", true, "done")]
    public void Category_FollowsTheNameAndTheIssueState(string? status, bool closed, string expected)
        => GitHubProjects.Category(status, closed).Should().Be(expected);

    [Fact]
    public void Category_AnIntakeStatus_IsTodo_WhateverItsName()
        => GitHubProjects.Category("Sprint candidates", closed: false, ["Sprint candidates"]).Should().Be("todo");

    [Fact]
    public void IntakeStatus_IgnoresCaseAndSpaces_AndWithNoneSet_TakesToDoLikeOnes()
    {
        GitHubProjects.IsIntakeStatus("Todo", ["To Do"]).Should().BeTrue("a GitHub 'Todo' is Jira's 'To Do'");
        GitHubProjects.IsIntakeStatus("In Progress", ["Todo"]).Should().BeFalse();
        GitHubProjects.IsIntakeStatus("Ready", []).Should().BeTrue();
        GitHubProjects.IsIntakeStatus("In Progress", []).Should().BeFalse();
        GitHubProjects.IsIntakeStatus(null, []).Should().BeFalse("an item with no status is nobody's pickup yet");
    }

    [Fact]
    public void RepoFilter_EmptyMeansEveryRepository()
    {
        GitHubProjects.InRepoFilter("acme/shop", []).Should().BeTrue();
        GitHubProjects.InRepoFilter("acme/shop", ["ACME/Shop"]).Should().BeTrue();
        GitHubProjects.InRepoFilter("acme/api", ["acme/shop"]).Should().BeFalse();
    }

    [Theory]
    [InlineData(null, "https://github.com")]
    [InlineData("", "https://github.com")]
    [InlineData("github.com", "https://github.com")]
    [InlineData("https://github.com/orgs/acme/projects/5", "https://github.com")]
    [InlineData("api.github.com", "https://github.com")]
    [InlineData("acme.ghe.com", "https://acme.ghe.com")]
    [InlineData("https://api.acme.ghe.com/graphql", "https://acme.ghe.com")]
    public void Hosts_AreGitHubDotComOrGheDotCom(string? input, string expected)
        => TrackerSites.NormalizeGitHubHost(input).Should().Be(expected);

    [Theory]
    [InlineData("github.acme.corp")]
    [InlineData("https://ghe.internal/api/graphql")]
    [InlineData("gitlab.com")]
    public void Hosts_OfGitHubEnterpriseServer_AreRefused(string input)
        => TrackerSites.NormalizeGitHubHost(input).Should().BeNull();

    [Fact]
    public void ApiBase_And_ConnectionIds()
    {
        TrackerSites.GitHubApiBase("https://github.com").Should().Be("https://api.github.com");
        TrackerSites.GitHubApiBase("https://acme.ghe.com").Should().Be("https://api.acme.ghe.com");
        TrackerSites.GitHubCliHost("https://acme.ghe.com").Should().Be("acme.ghe.com");
        TrackerSites.UniqueId(TrackerKinds.GitHub, "https://github.com", []).Should().Be("github");
        TrackerSites.UniqueId(TrackerKinds.GitHub, "https://github.com", ["github"]).Should().Be("github-2");
        TrackerSites.UniqueId(TrackerKinds.GitHub, "https://acme.ghe.com", []).Should().Be("github-acme");
    }

    [Fact]
    public void Markdown_IsKeptAsWritten_WithoutTemplateComments()
    {
        var raw = new RawTicket("acme/shop#1", "I_1", "https://github.com/acme/shop/issues/1", null, "Fix it", null, "Todo", "todo",
            null, "<!-- describe the bug -->\r\nThe *cart* breaks when `qty < 0`.\r\n\r\n\r\n\r\n- [ ] add a test", null, null,
            [], [], null) { Markdown = true, Repository = "https://github.com/acme/shop" };

        var ticket = TicketNormalizer.Normalize(raw, [], [], null,
            [new RawComment("c1", "dana", DateTimeOffset.UnixEpoch, "Looks like `a < b`") { Markdown = true }]);

        ticket.Body!.Description.Should().Be("The *cart* breaks when `qty < 0`.\n\n- [ ] add a test");
        ticket.Body.Repository.Should().Be("https://github.com/acme/shop");
        ticket.NewComments.Single().Body.Should().Be("Looks like `a < b`");
    }
}

public sealed class GitHubProjectsClientTests
{
    private const string Token = "ghp_secretGitHubToken0123456789abcdef";
    private const string ProjectId = "PVT_kwDOshop";
    private static readonly TrackerCredential TokenCredential = new("https://github.com", "", Token, bearer: true);
    private static readonly TrackerCredential GhCredential = new("https://acme.ghe.com", "", Token, bearer: true) { GhCliHost = "acme.ghe.com" };
    private static readonly TrackerBoardRef Board = new(ProjectId, "Shop roadmap");

    private const string ProjectJson = """
        {"data":{"node":{"id":"PVT_kwDOshop","title":"Shop roadmap","closed":false,"fields":{"nodes":[
          {"id":"F_title"},
          {"id":"F_status","name":"Status","options":[{"id":"o_todo","name":"Todo"},{"id":"o_prog","name":"In Progress"},{"id":"o_rev","name":"In Review"},{"id":"o_done","name":"Done"}]},
          {"id":"F_prio","name":"Priority","options":[{"id":"p1","name":"P1"}]}]}}}}
        """;

    private static (GitHubProjectsClient Client, FakeGitHubHandler Handler) Create(Action<FakeGitHubHandler> setup, TimeProvider? time = null)
    {
        var handler = new FakeGitHubHandler();
        setup(handler);
        return (new GitHubProjectsClient(new HttpClient(handler), time), handler);
    }

    private static string Issue(int number, string repo = "acme/shop", string[]? assignees = null, string state = "OPEN",
        string? subIssues = null, string? parent = null, string labels = "", string? type = null)
        => $$"""
            {"id":"I_{{repo.Replace('/', '_')}}_{{number}}","number":{{number}},"title":"Issue {{number}}","url":"https://github.com/{{repo}}/issues/{{number}}",
             "body":"Body of {{number}}","state":"{{state}}","updatedAt":"2026-11-01T10:00:00Z",
             "repository":{"nameWithOwner":"{{repo}}","url":"https://github.com/{{repo}}"},
             "assignees":{"nodes":[{{string.Join(",", (assignees ?? ["dana"]).Select(a => $"{{\"login\":\"{a}\"}}"))}}]},
             "labels":{"nodes":[{{labels}}]},
             "issueType":{{(type is null ? "null" : $"{{\"name\":\"{type}\"}}")}},
             "parent":{{parent ?? "null"}},
             "subIssues":{"nodes":[{{subIssues ?? ""}}]} }
            """;

    private static string Item(string id, string type, string? status, string? content)
        => $$"""{"id":"{{id}}","type":"{{type}}","isArchived":false,"fieldValueByName":{{(status is null ? "null" : $"{{\"name\":\"{status}\"}}")}},"content":{{content ?? "{}"}} }""";

    private static string ItemsPage(string items, bool hasNext = false, string? cursor = null)
        => $$"""{"data":{"viewer":{"login":"dana"},"node":{"items":{"pageInfo":{"hasNextPage":{{(hasNext ? "true" : "false")}},"endCursor":{{(cursor is null ? "null" : $"\"{cursor}\"")}} },"nodes":[{{items}}]} } } }""";

    private static TrackerPollAssignment Assignment(TrackerKnownTicket[]? known = null, string[]? repoFilter = null, string[]? intake = null)
        => new("cfg-1", "github", TrackerKinds.GitHub, "acme", [], null, false, known ?? [],
            IntakeStatuses: intake ?? ["Todo"], BoardId: ProjectId, BoardName: "Shop roadmap", RepoFilter: repoFilter);

    [Fact]
    public async Task Test_SendsTheTokenAsBearer_ToTheGraphQLEndpoint_AndNamesTheAccount()
    {
        var (client, handler) = Create(h => h.On("viewer { login name }", """{"data":{"viewer":{"login":"dana","name":"Dana Silva"}}}""",
            new() { ["X-OAuth-Scopes"] = "repo, read:org, project" }));

        var identity = await client.TestAsync(TokenCredential, CancellationToken.None);

        identity.Should().Be(new TrackerIdentity("dana", "Dana Silva (dana)"));
        handler.Requests.Single().Url.Should().Be("https://api.github.com/graphql");
        handler.Requests.Single().Authorization.Should().Be($"Bearer {Token}");
    }

    [Fact]
    public async Task Test_OnGheDotCom_UsesItsApiHost()
    {
        var (client, handler) = Create(h => h.On("viewer", """{"data":{"viewer":{"login":"dana_acme","name":null}}}"""));

        (await client.TestAsync(GhCredential, CancellationToken.None)).DisplayName.Should().Be("dana_acme");
        handler.Requests.Single().Url.Should().Be("https://api.acme.ghe.com/graphql");
    }

    [Fact]
    public async Task Test_AGhLoginWithoutTheProjectScope_SaysTheExactRefreshCommand()
    {
        var (client, _) = Create(h => h.On("viewer", """{"data":{"viewer":{"login":"dana","name":"Dana"}}}""",
            new() { ["X-OAuth-Scopes"] = "gist, read:org, repo" }));

        var act = () => client.TestAsync(GhCredential, CancellationToken.None);

        (await act.Should().ThrowAsync<TrackerException>()).Which.Message
            .Should().Contain("gh auth refresh -h acme.ghe.com -s project");
    }

    [Fact]
    public async Task Test_ATokenWithoutTheProjectScope_SaysWhichScopesToGive()
    {
        var (client, _) = Create(h => h.On("viewer", """{"data":{"viewer":{"login":"dana","name":"Dana"}}}""",
            new() { ["X-OAuth-Scopes"] = "repo" }));

        var act = () => client.TestAsync(TokenCredential, CancellationToken.None);

        (await act.Should().ThrowAsync<TrackerException>()).Which.Message
            .Should().Contain("lacks the 'project' scope").And.NotContain("gh auth");
    }

    [Fact]
    public async Task AFineGrainedToken_HasNoScopeHeader_AndIsNotRefusedForIt()
    {
        var (client, _) = Create(h => h.On("viewer", """{"data":{"viewer":{"login":"dana","name":"Dana"}}}"""));

        (await client.TestAsync(TokenCredential, CancellationToken.None)).AccountId.Should().Be("dana");
    }

    [Fact]
    public async Task SamlEnforcement_TellsATokenUserToAuthorizeIt_AndAGhUserToSignInAgain()
    {
        const string sso = """{"data":{"node":null},"errors":[{"type":"FORBIDDEN","message":"Resource protected by organization SAML enforcement. You must grant your Personal Access token access to this organization."}]}""";
        var (client, _) = Create(h => h.On("ProjectV2SingleSelectField", sso));

        var withToken = () => client.StatusesAsync(TokenCredential, "acme", CancellationToken.None, Board);
        (await withToken.Should().ThrowAsync<TrackerException>()).Which.Message
            .Should().Contain("SAML single sign-on").And.Contain("Configure SSO");

        var withGh = () => client.StatusesAsync(GhCredential, "acme", CancellationToken.None, Board);
        (await withGh.Should().ThrowAsync<TrackerException>()).Which.Message
            .Should().Contain("gh auth login -h acme.ghe.com");
    }

    [Fact]
    public async Task SsoHeaderOnA403_IsTheSsoMessage()
    {
        var (client, _) = Create(h => h.On("viewer", _ => new FakeGitHubHandler.Answer(HttpStatusCode.Forbidden,
            """{"message":"Forbidden"}""", new() { ["X-GitHub-SSO"] = "required; url=https://github.com/orgs/acme/sso?authorization_request=abc" })));

        var act = () => client.TestAsync(TokenCredential, CancellationToken.None);

        (await act.Should().ThrowAsync<TrackerException>()).Which.Message
            .Should().Contain("SAML single sign-on").And.NotContain("authorization_request");
    }

    [Fact]
    public async Task ASecondaryRateLimit_WithAShortRetryAfter_IsWaitedOutOnce()
    {
        var calls = 0;
        var clock = new MutableClock(DateTimeOffset.UnixEpoch);
        var (client, handler) = Create(h => h.On("viewer", _ => ++calls == 1
            ? new FakeGitHubHandler.Answer(HttpStatusCode.Forbidden,
                """{"message":"You have exceeded a secondary rate limit."}""", new() { ["Retry-After"] = "0" })
            : new FakeGitHubHandler.Answer(HttpStatusCode.OK, """{"data":{"viewer":{"login":"dana","name":"Dana"}}}""")), clock);

        (await client.TestAsync(TokenCredential, CancellationToken.None)).AccountId.Should().Be("dana");
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task ALongRateLimit_FailsTheCall_WithTheWait()
    {
        var (client, handler) = Create(h => h.On("viewer", _ => new FakeGitHubHandler.Answer(HttpStatusCode.Forbidden,
            """{"message":"You have exceeded a secondary rate limit."}""", new() { ["Retry-After"] = "120" })));

        var act = () => client.TestAsync(TokenCredential, CancellationToken.None);

        (await act.Should().ThrowAsync<TrackerException>()).Which.Message
            .Should().Contain("rate limiting").And.Contain("2 minutes");
        handler.Requests.Should().ContainSingle("a long wait is not slept through inside a poll");
    }

    [Fact]
    public async Task Scopes_AreTheViewerThenTheirOrganizations_AcrossPages()
    {
        var (client, handler) = Create(h => h.On("organizations", vars => new FakeGitHubHandler.Answer(HttpStatusCode.OK,
            vars.GetProperty("after").ValueKind == JsonValueKind.Null
                ? """{"data":{"viewer":{"login":"dana","name":"Dana","organizations":{"pageInfo":{"hasNextPage":true,"endCursor":"c1"},"nodes":[{"login":"acme","name":"Acme Inc"}]}}}}"""
                : """{"data":{"viewer":{"login":"dana","name":"Dana","organizations":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[{"login":"globex","name":""}]}}}}""")));

        var scopes = await client.ScopesAsync(TokenCredential, CancellationToken.None);

        scopes.Select(s => s.Key).Should().Equal("dana", "acme", "globex");
        scopes[0].Name.Should().Be("Dana (you)");
        scopes[2].Name.Should().Be("globex");
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Boards_AreTheOwnersOpenProjects_ByTitle()
    {
        var (client, _) = Create(h => h.On("projectsV2", """
            {"data":{"repositoryOwner":{"projectsV2":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[
              {"id":"PVT_b","number":7,"title":"Web","closed":false,"url":"https://github.com/orgs/acme/projects/7"},
              {"id":"PVT_old","number":2,"title":"Archive","closed":true,"url":"u"},
              {"id":"PVT_a","number":5,"title":"API","closed":false,"url":"https://github.com/orgs/acme/projects/5"}]}}}}
            """));

        var boards = await client.BoardsAsync(TokenCredential, "acme", CancellationToken.None);

        boards.Should().Equal(
            new TrackerBoard("PVT_a", "API", null, 5, "https://github.com/orgs/acme/projects/5"),
            new TrackerBoard("PVT_b", "Web", null, 7, "https://github.com/orgs/acme/projects/7"));
    }

    [Fact]
    public async Task Boards_OfAnUnknownOwner_SayWho()
    {
        var (client, _) = Create(h => h.On("projectsV2", """{"data":{"repositoryOwner":null}}"""));

        var act = () => client.BoardsAsync(TokenCredential, "nobody", CancellationToken.None);

        (await act.Should().ThrowAsync<TrackerException>()).Which.Message.Should().Contain("'nobody'");
    }

    [Fact]
    public async Task Statuses_AreTheStatusFieldsOptions_AndFieldsListEverySingleSelect()
    {
        var (client, handler) = Create(h => h.On("ProjectV2SingleSelectField", ProjectJson));

        var statuses = await client.StatusesAsync(TokenCredential, "acme", CancellationToken.None, Board);
        statuses.Should().Equal(new TrackerStatus("Todo", "todo"), new TrackerStatus("In Progress", "inprogress"),
            new TrackerStatus("In Review", "inprogress"), new TrackerStatus("Done", "done"));

        (await client.StatusesAsync(TokenCredential, "acme", CancellationToken.None, Board, "priority"))
            .Select(s => s.Name).Should().Equal("P1");

        var fields = await client.FieldsAsync(TokenCredential, "acme", Board, CancellationToken.None);
        fields.Select(f => f.Name).Should().Equal("Status", "Priority");
        handler.Requests[0].Variables.GetProperty("id").GetString().Should().Be(ProjectId);
    }

    [Fact]
    public async Task AMissingStatusField_SaysWhichFieldsThereAre()
    {
        var (client, _) = Create(h => h.On("ProjectV2SingleSelectField", ProjectJson));

        var act = () => client.StatusesAsync(TokenCredential, "acme", CancellationToken.None, Board, "Stage");

        (await act.Should().ThrowAsync<TrackerException>()).Which.Message
            .Should().Contain("no single-select field 'Stage'").And.Contain("Status, Priority");
    }

    [Fact]
    public async Task AProjectThatIsGone_FailsLikeAMissingBoard()
    {
        var (client, _) = Create(h => h.On("ProjectV2SingleSelectField",
            """{"data":{"node":null},"errors":[{"type":"NOT_FOUND","message":"Could not resolve to a node with the global id of 'PVT_kwDOshop'"}]}"""));

        var act = () => client.FetchTicketsAsync(TokenCredential, Assignment(), CancellationToken.None);

        (await act.Should().ThrowAsync<TrackerException>()).Which.Message
            .Should().Be("The board 'Shop roadmap' was not found in acme — pick another board in the ticket source settings.");
    }

    [Fact]
    public async Task Intake_IsMyOpenIssuesInAPickupStatus_DraftsAndPullRequestsSkipped_AcrossPages()
    {
        var page1 = ItemsPage(string.Join(",",
            Item("PVTI_1", "ISSUE", "Todo", Issue(1, labels: """{"name":"backend"}""")),
            Item("PVTI_2", "ISSUE", "In Progress", Issue(2)),
            Item("PVTI_3", "ISSUE", "Todo", Issue(3, assignees: ["sam"])),
            Item("PVTI_4", "DRAFT_ISSUE", "Todo", """{"title":"a draft"}""")),
            hasNext: true, cursor: "p1");
        var page2 = ItemsPage(string.Join(",",
            Item("PVTI_5", "PULL_REQUEST", "Todo", """{"title":"a PR"}"""),
            Item("PVTI_6", "ISSUE", "Todo", Issue(6, repo: "acme/api")),
            Item("PVTI_7", "ISSUE", "Todo", Issue(7, state: "CLOSED")),
            Item("PVTI_8", "ISSUE", "todo", Issue(8, labels: """{"name":"bug"}"""))));
        var (client, handler) = Create(h => h
            .On("ProjectV2SingleSelectField", ProjectJson)
            .On("items(first:", vars => new FakeGitHubHandler.Answer(HttpStatusCode.OK,
                vars.GetProperty("after").ValueKind == JsonValueKind.Null ? page1 : page2)));

        var tickets = await client.FetchTicketsAsync(TokenCredential, Assignment(repoFilter: ["acme/shop"]), CancellationToken.None);

        tickets.Select(t => t.Key).Should().BeEquivalentTo(["acme/shop#1", "acme/shop#8"],
            "#2 is under way, #3 is a teammate's, #6 is outside the repository filter, #7 is closed; drafts and PRs are never tickets");
        var one = tickets.Single(t => t.Key == "acme/shop#1");
        one.Id.Should().Be("I_acme_shop_1");
        one.InIntake.Should().BeTrue();
        one.AssignedToMe.Should().BeTrue();
        one.Status.Should().Be("Todo");
        one.StatusCategory.Should().Be("todo");
        one.Markdown.Should().BeTrue();
        one.DescriptionHtml.Should().Be("Body of 1");
        one.Labels.Should().Equal("backend");
        one.Repository.Should().Be("https://github.com/acme/shop");
        one.Url.Should().Be("https://github.com/acme/shop/issues/1");
        tickets.Single(t => t.Key == "acme/shop#8").Type.Should().Be("Bug", "a bug label stands in for an issue type");
        handler.Requests.Count(r => r.Query.Contains("items(first:")).Should().Be(2);
        handler.Requests.Last().Variables.GetProperty("field").GetString().Should().Be("Status");
    }

    [Fact]
    public async Task TrackedKeys_AreReadByKey_WhateverTheirStatus_AndAGoneOneIsJustAbsent()
    {
        var (client, handler) = Create(h => h
            .On("ProjectV2SingleSelectField", ProjectJson)
            .On("items(first:", ItemsPage(""))
            .On("i0: repository", """
                {"data":{"viewer":{"login":"dana"},
                  "i0":{"issue":{"id":"I_9","number":9,"title":"Moved on","url":"https://github.com/acme/shop/issues/9","body":"b","state":"OPEN",
                    "updatedAt":"2026-11-02T10:00:00Z","repository":{"nameWithOwner":"acme/shop","url":"https://github.com/acme/shop"},
                    "assignees":{"nodes":[{"login":"sam"}]},"labels":{"nodes":[]},"issueType":null,"parent":null,"subIssues":{"nodes":[]},
                    "projectItems":{"nodes":[{"id":"PVTI_other","project":{"id":"PVT_other"},"fieldValueByName":{"name":"Done"}},
                                             {"id":"PVTI_9","project":{"id":"PVT_kwDOshop"},"fieldValueByName":{"name":"In Review"}}]}}},
                  "i1":null},
                 "errors":[{"type":"NOT_FOUND","path":["i1"],"message":"Could not resolve to a Repository with the name 'acme/gone'."}]}
                """));

        var tickets = await client.FetchTicketsAsync(TokenCredential,
            Assignment(known: [new("acme/shop#9", "h", null), new("acme/gone#1", "h", null), new("not-a-key", null, null)]),
            CancellationToken.None);

        var nine = tickets.Should().ContainSingle().Subject;
        nine.Key.Should().Be("acme/shop#9");
        nine.Status.Should().Be("In Review", "the status is this project's, not another project's");
        nine.StatusCategory.Should().Be("inprogress");
        nine.AssignedToMe.Should().BeFalse();
        nine.InIntake.Should().BeFalse();
        var batch = handler.Requests.Single(r => r.Query.Contains("i0: repository")).Query;
        batch.Should().Contain("repository(owner: \"acme\", name: \"shop\") { issue(number: 9)")
            .And.Contain("repository(owner: \"acme\", name: \"gone\") { issue(number: 1)")
            .And.NotContain("not-a-key");
    }

    [Fact]
    public async Task AnIssueWithSubIssues_ReportsThemInOrder_AndReadsEachOne()
    {
        var subs = """{"number":21,"repository":{"nameWithOwner":"acme/shop"}},{"number":5,"repository":{"nameWithOwner":"acme/api"}}""";
        var (client, _) = Create(h => h
            .On("ProjectV2SingleSelectField", ProjectJson)
            .On("items(first:", ItemsPage(Item("PVTI_20", "ISSUE", "Todo", Issue(20, subIssues: subs))))
            .On("i0: repository", "{\"data\":{\"viewer\":{\"login\":\"dana\"},"
                + "\"i0\":{\"issue\":" + Issue(21, parent: """{"number":20,"repository":{"nameWithOwner":"acme/shop"}}""")[..^1]
                + ",\"projectItems\":{\"nodes\":[]}}},"
                + "\"i1\":{\"issue\":" + Issue(5, repo: "acme/api", assignees: ["sam"])[..^1]
                + ""","projectItems":{"nodes":[{"id":"PVTI_5","project":{"id":"PVT_kwDOshop"},"fieldValueByName":{"name":"In Progress"}}]}}}}}"""));

        var tickets = await client.FetchTicketsAsync(TokenCredential, Assignment(), CancellationToken.None);

        var story = tickets.Single(t => t.Key == "acme/shop#20");
        story.ChildKeys.Should().Equal("acme/shop#21", "acme/api#5");
        var mine = tickets.Single(t => t.Key == "acme/shop#21");
        mine.ParentKey.Should().Be("acme/shop#20");
        mine.ParentType.Should().Be("Issue");
        mine.AssignedToMe.Should().BeTrue();
        mine.Status.Should().BeNull("it is not on the project");
        mine.StatusCategory.Should().Be("todo", "an open issue with no status has not been started");
        var teammates = tickets.Single(t => t.Key == "acme/api#5");
        teammates.AssignedToMe.Should().BeFalse();
        teammates.StatusCategory.Should().Be("inprogress");
    }

    [Fact]
    public async Task AProjectBeyondThePagingCap_FailsTheWholePoll()
    {
        var (client, handler) = Create(h => h
            .On("ProjectV2SingleSelectField", ProjectJson)
            .On("items(first:", ItemsPage(Item("PVTI_1", "ISSUE", "Done", Issue(1)), hasNext: true, cursor: "more")));

        var act = () => client.FetchTicketsAsync(TokenCredential, Assignment(), CancellationToken.None);

        (await act.Should().ThrowAsync<TrackerException>()).Which.Message.Should().Contain("more than 5,000 items");
        handler.Requests.Count(r => r.Query.Contains("items(first:")).Should().Be(GitHubProjectsClient.MaxItemPages);
    }

    [Fact]
    public async Task Preview_CountsIntake_AndDescribesIt()
    {
        var (client, _) = Create(h => h
            .On("ProjectV2SingleSelectField", ProjectJson)
            .On("items(first:", ItemsPage(string.Join(",",
                Item("PVTI_1", "ISSUE", "Todo", Issue(1)), Item("PVTI_2", "ISSUE", "Todo", Issue(2, repo: "acme/api"))))));

        var preview = await client.PreviewAsync(TokenCredential, "acme", [], null, false, ["Todo"], null, CancellationToken.None,
            Board, null, ["acme/api"]);

        preview.MatchCount.Should().Be(1);
        preview.Sample.Single().Key.Should().Be("acme/api#2");
        preview.EffectiveQuery.Should().Be("project:\"Shop roadmap\" is:issue is:open assignee:@me status:\"Todo\" repo:acme/api");
    }

    [Fact]
    public async Task Repositories_AreTheOnesWithIssuesOnTheProject()
    {
        var (client, _) = Create(h => h
            .On("ProjectV2SingleSelectField", ProjectJson)
            .On("items(first:", ItemsPage(string.Join(",",
                Item("a", "ISSUE", null, Issue(1, repo: "acme/web")), Item("b", "ISSUE", "Done", Issue(2, repo: "acme/api")),
                Item("c", "ISSUE", "Todo", Issue(3, repo: "acme/web")), Item("d", "DRAFT_ISSUE", null, "{}")))));

        (await client.RepositoriesAsync(TokenCredential, "acme", Board, CancellationToken.None))
            .Should().Equal("acme/api", "acme/web");
    }

    [Fact]
    public async Task Comments_AreTheNewOnes_OldestFirst_AsMarkdown()
    {
        var (client, _) = Create(h => h.On("comments(last:", """
            {"data":{"node":{"comments":{"nodes":[
              {"id":"IC_1","author":{"login":"sam"},"createdAt":"2026-11-01T09:00:00Z","body":"old"},
              {"id":"IC_2","author":null,"createdAt":"2026-11-03T09:00:00Z","body":"**new**"},
              {"id":"IC_3","author":{"login":"dana"},"createdAt":"2026-11-02T09:00:00Z","body":"middle"}]}}}}
            """));
        var ticket = new RawTicket("acme/shop#1", "I_1", "u", null, "t", null, null, null, null, null, null, null, [], [], null);

        var comments = await client.FetchCommentsAsync(TokenCredential, ticket,
            DateTimeOffset.Parse("2026-11-01T12:00:00Z"), 30, CancellationToken.None);

        comments.Select(c => c.Id).Should().Equal("IC_3", "IC_2");
        comments.Should().OnlyContain(c => c.Markdown);
        comments.Last().Author.Should().Be("ghost");
    }

    private static TrackerTransition Move(string target)
        => new("t-1", "github", "acme/shop#1", "I_1", target, "Developing", ProjectId, null);

    [Fact]
    public async Task WriteBack_SetsTheStatusOption_OnTheProjectsItem()
    {
        var (client, handler) = Create(h => h
            .On("ProjectV2SingleSelectField", ProjectJson)
            .On("projectItems(first: 50", """{"data":{"node":{"projectItems":{"nodes":[{"id":"PVTI_1","project":{"id":"PVT_kwDOshop"},"fieldValueByName":{"optionId":"o_todo"}}]}}}}""")
            .On("updateProjectV2ItemFieldValue", """{"data":{"updateProjectV2ItemFieldValue":{"projectV2Item":{"id":"PVTI_1"}}}}"""));

        (await client.TransitionAsync(TokenCredential, Move("in progress"), CancellationToken.None)).Should().BeTrue();

        var update = handler.Requests.Single(r => r.Query.Contains("updateProjectV2ItemFieldValue")).Variables;
        update.GetProperty("project").GetString().Should().Be(ProjectId);
        update.GetProperty("item").GetString().Should().Be("PVTI_1");
        update.GetProperty("field").GetString().Should().Be("F_status");
        update.GetProperty("option").GetString().Should().Be("o_prog");
        handler.Queries.Should().NotContain(q => q.Contains("addProjectV2ItemById"));
    }

    [Fact]
    public async Task WriteBack_AlreadyThere_WritesNothing()
    {
        var (client, handler) = Create(h => h
            .On("ProjectV2SingleSelectField", ProjectJson)
            .On("projectItems(first: 50", """{"data":{"node":{"projectItems":{"nodes":[{"id":"PVTI_1","project":{"id":"PVT_kwDOshop"},"fieldValueByName":{"optionId":"o_prog"}}]}}}}"""));

        (await client.TransitionAsync(TokenCredential, Move("In Progress"), CancellationToken.None)).Should().BeFalse();
        handler.Queries.Should().NotContain(q => q.Contains("mutation"));
    }

    [Fact]
    public async Task WriteBack_ASubIssueNotOnTheProject_IsAddedFirst()
    {
        var (client, handler) = Create(h => h
            .On("ProjectV2SingleSelectField", ProjectJson)
            .On("projectItems(first: 50", """{"data":{"node":{"projectItems":{"nodes":[{"id":"PVTI_x","project":{"id":"PVT_other"},"fieldValueByName":null}]}}}}""")
            .On("addProjectV2ItemById", """{"data":{"addProjectV2ItemById":{"item":{"id":"PVTI_new"}}}}""")
            .On("updateProjectV2ItemFieldValue", """{"data":{"updateProjectV2ItemFieldValue":{"projectV2Item":{"id":"PVTI_new"}}}}"""));

        (await client.TransitionAsync(TokenCredential, Move("Done"), CancellationToken.None)).Should().BeTrue();

        handler.Requests.Single(r => r.Query.Contains("addProjectV2ItemById")).Variables.GetProperty("content").GetString().Should().Be("I_1");
        handler.Requests.Single(r => r.Query.Contains("updateProjectV2ItemFieldValue")).Variables.GetProperty("item").GetString().Should().Be("PVTI_new");
    }

    [Fact]
    public async Task WriteBack_ToAnOptionThatIsNotThere_ListsTheOptions()
    {
        var (client, _) = Create(h => h.On("ProjectV2SingleSelectField", ProjectJson));

        var act = () => client.TransitionAsync(TokenCredential, Move("Shipped"), CancellationToken.None);

        (await act.Should().ThrowAsync<TrackerException>()).Which.Message
            .Should().Be("The project's Status field has no option 'Shipped' — available: Todo, In Progress, In Review, Done.");
    }

    [Fact]
    public async Task WriteBack_WithoutAProject_IsRefused_NotGuessed()
    {
        var (client, handler) = Create(_ => { });

        var act = () => ((ITrackerStatusWriter)client).TransitionAsync(TokenCredential,
            new TrackerTransition("t", "github", "acme/shop#1", "I_1", "Done", "Merged"), CancellationToken.None);

        await act.Should().ThrowAsync<TrackerException>();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ErrorsThatQuoteTheToken_AreScrubbed_BeforeTheyLeaveThePoller()
    {
        var root = Path.Combine(Path.GetTempPath(), "orkka-gh-" + Guid.NewGuid().ToString("N"));
        try
        {
            var handler = new FakeGitHubHandler().On("ProjectV2SingleSelectField",
                $$"""{"errors":[{"type":"SOMETHING","message":"bad credentials {{Token}}"}]}""");
            var store = new TrackerStore(root);
            store.Upsert(new TrackerConnectionEntry { Id = "github", Kind = TrackerKinds.GitHub, SiteUrl = "https://github.com" });
            var secrets = new FileSecretStore(TrackerPaths.SecretsDir(root));
            secrets.Set("github", Token);
            var logger = new ListLogger<TrackerTicketPoller>();
            var client = new GitHubProjectsClient(new HttpClient(handler));
            var poller = new TrackerTicketPoller(store, secrets, _ => client,
                new TrackerAttachmentCache(TrackerPaths.TicketsDir(root), new HttpClient(new FakeTrackerHandler()),
                    NullLogger<TrackerAttachmentCache>.Instance), logger);

            var outcome = await poller.PollAsync(Assignment(), "instance-1", CancellationToken.None);

            outcome.Report.Ok.Should().BeFalse();
            outcome.Report.Error.Should().Contain("bad credentials ***").And.NotContain(Token);
            outcome.Summary.Should().NotContain(Token);
            logger.Lines.Should().NotContain(l => l.Contains(Token));
            handler.Requests.Single().Authorization.Should().Be($"Bearer {Token}");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void TheCredential_NeverPrintsItsToken()
    {
        GhCredential.ToString().Should().NotContain(Token);
        GhCredential.Scrub($"Authorization: Bearer {Token}").Should().Be("Authorization: Bearer ***");
    }
}

internal sealed class ListLogger<T> : ILogger<T>
{
    public List<string> Lines { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Lines.Add(formatter(state, exception) + (exception is null ? "" : " " + exception));
}

/// <summary>The gh-login path: the token comes from `gh auth token` at call time and is kept nowhere.</summary>
public sealed class GitHubGhLoginTests : IDisposable
{
    private const string GhToken = "gho_tokenFromTheGitHubCli0123456789";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orkka-ghlogin-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task GhAuthToken_IsAskedForTheHost_AndItsTokenNeverPrints()
    {
        string[]? asked = null;
        var result = await GhCliCredentials.TokenAsync("acme.ghe.com", CancellationToken.None,
            (args, _) => { asked = args; return Task.FromResult(new GhCliCredentials.GhRun(0, $"{GhToken}\n")); });

        asked.Should().Equal("auth", "token", "--hostname", "acme.ghe.com");
        result.Ok.Should().BeTrue();
        result.ToString().Should().NotContain(GhToken);
    }

    [Theory]
    [InlineData(-1, null, "not installed")]
    [InlineData(1, "", "gh auth login -h github.com")]
    [InlineData(0, "no oauth token found for github.com", "gh auth login -h github.com")]
    [InlineData(-2, null, "did not answer")]
    public async Task GhAuthToken_Failures_SayWhatToRun(int exit, string? stdout, string expected)
    {
        var result = await GhCliCredentials.TokenAsync("github.com", CancellationToken.None,
            (_, _) => Task.FromResult(new GhCliCredentials.GhRun(exit, stdout)));

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain(expected);
    }

    private (TrackerConnectionManager Manager, FakeGitHubHandler Handler, FileSecretStore Secrets, List<string> GhHosts) Create(
        Action<FakeGitHubHandler> setup, bool ghSignedIn = true)
    {
        var handler = new FakeGitHubHandler();
        setup(handler);
        var http = new HttpClient(handler);
        var secrets = new FileSecretStore(TrackerPaths.SecretsDir(_root));
        var hosts = new List<string>();
        var resolver = new TrackerCredentialResolver(secrets, (host, _) =>
        {
            hosts.Add(host);
            return Task.FromResult(ghSignedIn
                ? new GhCliCredentials.TokenResult(GhToken, null)
                : new GhCliCredentials.TokenResult(null, $"The GitHub CLI is not signed in to {host} — run `gh auth login -h {host}`."));
        });
        var service = new TrackerService(new TrackerStore(_root), secrets, new JiraCloudClient(http), new AzureDevOpsClient(http),
            NullLogger<TrackerService>.Instance, github: new GitHubProjectsClient(http), credentials: resolver);
        return (new TrackerConnectionManager(service), handler, secrets, hosts);
    }

    private const string Viewer = """{"data":{"viewer":{"login":"dana","name":"Dana"}}}""";

    [Fact]
    public async Task AddingWithTheGhLogin_StoresNoToken_AndUsesGhAtEveryCall()
    {
        var (manager, handler, secrets, hosts) = Create(h => h.On("viewer", Viewer));

        var result = await manager.AddAsync(new AddTrackerConnectionRequest("github", "", null, "", TrackerAuthModes.GhCli),
            CancellationToken.None);

        result.Ok.Should().BeTrue(result.Error);
        result.Connection!.Id.Should().Be("github");
        result.Connection.AuthMode.Should().Be(TrackerAuthModes.GhCli);
        result.Connection.AccountId.Should().Be("dana");
        secrets.Get("github").Should().BeNull("the gh login is never copied into Orkka's store");
        hosts.Should().Equal("github.com");
        handler.Requests.Single().Authorization.Should().Be($"Bearer {GhToken}");
        File.ReadAllText(TrackerPaths.TrackersFile(_root)).Should().Contain("\"auth\": \"gh\"").And.NotContain(GhToken);

        // The health check and every query ask gh again.
        var test = await manager.Service.QueryAsync(new TrackerQueryRequest("github", TrackerQueryKinds.Test), CancellationToken.None);
        test.Ok.Should().BeTrue();
        hosts.Should().HaveCount(2);
    }

    [Fact]
    public async Task AddingWithTheGhLogin_WhenGhIsNotSignedIn_SavesNothing_AndSaysWhatToRun()
    {
        var (manager, handler, _, _) = Create(h => h.On("viewer", Viewer), ghSignedIn: false);

        var result = await manager.AddAsync(new AddTrackerConnectionRequest("github", "acme.ghe.com", null, ""), CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("gh auth login -h acme.ghe.com").And.EndWith("Nothing was saved.");
        handler.Requests.Should().BeEmpty();
        manager.Service.Store.List().Should().BeEmpty();
    }

    [Fact]
    public async Task AddingWithAToken_StoresIt_AndSwitchingToGh_DeletesIt()
    {
        const string pat = "github_pat_11ABCDEFG_typedIntoSettings";
        var (manager, handler, secrets, _) = Create(h => h.On("viewer", Viewer));

        var withToken = await manager.AddAsync(new AddTrackerConnectionRequest("github", "github.com", null, pat, TrackerAuthModes.Token),
            CancellationToken.None);
        withToken.Ok.Should().BeTrue(withToken.Error);
        withToken.Connection!.AuthMode.Should().Be(TrackerAuthModes.Token);
        secrets.Get("github").Should().Be(pat);
        handler.Requests.Single().Authorization.Should().Be($"Bearer {pat}");
        withToken.ToString().Should().NotContain(pat);

        var switched = await manager.AddAsync(new AddTrackerConnectionRequest("github", "github.com", null, "", TrackerAuthModes.GhCli),
            CancellationToken.None);
        switched.Connection!.Id.Should().Be("github", "the same host is the same connection");
        secrets.Get("github").Should().BeNull();
        manager.Service.Store.List().Should().ContainSingle();
    }

    [Fact]
    public async Task ATokenTheTrackerRefuses_IsNotStored_AndNotEchoed()
    {
        const string pat = "ghp_refusedToken0123456789";
        var (manager, _, secrets, _) = Create(h => h.On("viewer", _ => new FakeGitHubHandler.Answer(HttpStatusCode.Unauthorized,
            $$"""{"message":"Bad credentials for {{pat}}"}""")));

        var result = await manager.AddAsync(new AddTrackerConnectionRequest("github", "github.com", null, pat, TrackerAuthModes.Token),
            CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("rejected the token").And.NotContain(pat);
        secrets.Get("github").Should().BeNull();
    }

    [Fact]
    public async Task GitHubEnterpriseServer_IsRefused_BeforeAnythingRuns()
    {
        var (manager, handler, _, hosts) = Create(_ => { });

        var result = await manager.AddAsync(new AddTrackerConnectionRequest("github", "github.acme.corp", null, ""), CancellationToken.None);

        result.Error.Should().Be(TrackerConnectionManager.GitHubServerNotSupported);
        handler.Requests.Should().BeEmpty();
        hosts.Should().BeEmpty();
    }

    [Fact]
    public async Task RemovingAGhLoginConnection_LeavesGhAlone_AndDropsTheEntry()
    {
        var (manager, _, _, _) = Create(h => h.On("viewer", Viewer));
        await manager.AddAsync(new AddTrackerConnectionRequest("github", "", null, ""), CancellationToken.None);

        manager.Remove("github").Ok.Should().BeTrue();
        manager.Service.Store.List().Should().BeEmpty();
    }

    [Fact]
    public async Task WriteBack_UsesTheGhLogin_AndTheProjectFromTheTransition()
    {
        var (manager, handler, secrets, hosts) = Create(h => h
            .On("viewer", Viewer)
            .On("ProjectV2SingleSelectField", """{"data":{"node":{"id":"PVT_1","title":"Roadmap","fields":{"nodes":[{"id":"F_s","name":"Stage","options":[{"id":"o_d","name":"Done"}]}]}}}}""")
            .On("projectItems(first: 50", """{"data":{"node":{"projectItems":{"nodes":[{"id":"PVTI_1","project":{"id":"PVT_1"},"fieldValueByName":null}]}}}}""")
            .On("updateProjectV2ItemFieldValue", """{"data":{"updateProjectV2ItemFieldValue":{"projectV2Item":{"id":"PVTI_1"}}}}"""));
        await manager.AddAsync(new AddTrackerConnectionRequest("github", "", null, ""), CancellationToken.None);
        var writeBack = new TrackerWriteBack(manager.Service.Store, secrets, manager.Service.WriterFor,
            (_, _) => Task.FromResult(Orkka.LocalRunner.Contracts.RunnerOpResult.Ok("open")),
            NullLogger<TrackerWriteBack>.Instance, manager.Service.Credentials);

        var report = await writeBack.ApplyAsync(new TrackerSyncResponse([],
            [new TrackerTransition("tr-1", "github", "acme/shop#1", "I_1", "Done", "Merged", "PVT_1", "Stage")]),
            "instance", CancellationToken.None);

        report!.Transitions.Single().Should().Be(new TrackerTransitionAck("tr-1", true));
        handler.Requests.Last().Variables.GetProperty("option").GetString().Should().Be("o_d");
        handler.Requests.Last().Authorization.Should().Be($"Bearer {GhToken}");
        hosts.Should().HaveCount(2, "one gh call to add, one to write back");
    }
}
