namespace Orkka.Tests.Services;

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Orkka.LocalRunner.Contracts;
using Orkka.LocalRunner.Trackers;

/// <summary>
/// A ticket source limited to one board: an Azure DevOps team (its area paths) or a Jira board
/// (its filter). The board narrows intake only, is resolved live on every poll, and a board that
/// is gone fails the poll instead of widening it to the whole project.
/// </summary>
public sealed class TrackerBoardQueryTests
{
    // ── Azure DevOps: team field values → WIQL ──────────────────────────────

    [Fact]
    public void AdoBoard_IncludeChildren_IsUnder_OtherwiseEquals_OrEd()
        => AdoQueries.TeamFieldCondition("System.AreaPath",
                [(@"Shop\Web", true), (@"Shop\Shared", false), (@"Shop\Api", true)]).Should()
            .Be(@"([System.AreaPath] UNDER 'Shop\Web' OR [System.AreaPath] = 'Shop\Shared' OR [System.AreaPath] UNDER 'Shop\Api')");

    [Fact]
    public void AdoBoard_SingleQuotes_AreDoubled()
        => AdoQueries.TeamFieldCondition("System.AreaPath", [(@"Shop\Dana's team", true)]).Should()
            .Be(@"([System.AreaPath] UNDER 'Shop\Dana''s team')");

    [Fact]
    public void AdoBoard_DuplicatesAndBlanks_AreDropped_AndTheDefaultStandsInForNone()
    {
        AdoQueries.TeamFieldCondition(null, [("Shop", false), (" shop ", false), ("", true)]).Should()
            .Be("([System.AreaPath] = 'Shop')", "the field defaults to the area path");
        AdoQueries.TeamFieldCondition("System.AreaPath", [], @"Shop\Web").Should().Be(@"([System.AreaPath] UNDER 'Shop\Web')");
        AdoQueries.TeamFieldCondition("System.AreaPath", []).Should().BeNull();
    }

    [Fact]
    public void AdoBoard_ReadsTheTeamFieldFromTheResponse()
    {
        using var doc = JsonDocument.Parse("""
            {"field":{"referenceName":"Custom.Team","url":"x"},"defaultValue":"Payments",
             "values":[{"value":"Payments","includeChildren":false},{"value":"Pay'ments\\EU","includeChildren":true}]}
            """);

        AdoQueries.TeamFieldCondition(doc.RootElement).Should()
            .Be(@"([Custom.Team] = 'Payments' OR [Custom.Team] UNDER 'Pay''ments\EU')");
    }

    [Fact]
    public void AdoBoard_AFieldNameThatIsNotAReferenceName_IsRefused()
    {
        var act = () => AdoQueries.TeamFieldCondition("System.AreaPath] = 'x' OR [System.Id", [("Shop", true)]);

        act.Should().Throw<TrackerException>().WithMessage("*unexpected team field*");
    }

    [Fact]
    public void AdoIntake_TheBoardComesBeforeTheExtraClause()
        => AdoQueries.IntakeWiql(["New"], [], null, "[System.Tags] CONTAINS 'api'", "([System.AreaPath] UNDER 'Shop')").Should()
            .Be("SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project AND [System.AssignedTo] = @Me"
                + " AND [System.State] IN ('New') AND ([System.AreaPath] UNDER 'Shop') AND ([System.Tags] CONTAINS 'api')");

    // ── Jira: board filter → JQL ────────────────────────────────────────────

    [Theory]
    [InlineData("project = SHOP AND component = Web ORDER BY Rank ASC", "(project = SHOP AND component = Web)")]
    [InlineData("project = SHOP order   by rank", "(project = SHOP)")]
    [InlineData("project = SHOP\nORDER BY Rank ASC, created DESC", "(project = SHOP)")]
    [InlineData("summary ~ \"order by\" AND project = SHOP ORDER BY Rank", "(summary ~ \"order by\" AND project = SHOP)")]
    [InlineData("summary ~ 'sort order by date' ORDER BY Rank", "(summary ~ 'sort order by date')")]
    [InlineData("labels = reorder AND project = SHOP", "(labels = reorder AND project = SHOP)")]
    [InlineData("summary ~ \"say \\\"order by\\\" twice\" ORDER BY key", "(summary ~ \"say \\\"order by\\\" twice\")")]
    public void JiraBoard_FilterJql_LosesItsOrderBy_AndIsParenthesized(string filter, string expected)
        => JiraQueries.BoardFilterClause(filter).Should().Be(expected);

    [Theory]
    [InlineData("ORDER BY Rank ASC")]
    [InlineData("  ")]
    [InlineData(null)]
    public void JiraBoard_AFilterThatOnlyOrders_AddsNothing(string? filter)
        => JiraQueries.BoardFilterClause(filter).Should().BeNull();

    [Fact]
    public void JiraIntake_TheBoardComesBeforeTheExtraClause()
        => JiraQueries.IntakeJql("SHOP", ["To Do"], null, null, "labels = api", "(component = Web)").Should()
            .Be("project = \"SHOP\" AND assignee = currentUser() AND status in (\"To Do\") AND (component = Web) AND (labels = api)");
}

public sealed class JiraBoardClientTests
{
    private const string Token = "ATATT3xFfGF0-secret-jira-token";
    private static readonly TrackerCredential Credential = new("https://acme.atlassian.net", "dana@acme.com", Token);
    private static readonly TrackerBoardRef WebBoard = new("12", "Web board");

    private static FakeTrackerHandler WithBoard(FakeTrackerHandler handler) => handler
        .On(HttpMethod.Get, "/rest/agile/1.0/board/12/configuration", """{"id":12,"name":"Web board","filter":{"id":"10040"}}""")
        .On(HttpMethod.Get, "/rest/api/3/filter/10040", """{"id":"10040","jql":"project = SHOP AND component = Web ORDER BY Rank ASC"}""");

    [Fact]
    public async Task Boards_PageThroughTheProjectsBoards_ByName()
    {
        var handler = new FakeTrackerHandler()
            .On(HttpMethod.Get, "startAt=0", """{"isLast":false,"values":[{"id":12,"name":"Web board","type":"scrum"},{"id":3,"name":"api","type":"kanban"}]}""")
            .On(HttpMethod.Get, "startAt=2", """{"isLast":true,"values":[{"id":40,"name":"Mobile","type":"kanban"}]}""");
        var client = new JiraCloudClient(new HttpClient(handler));

        var boards = await client.BoardsAsync(Credential, "SHOP", CancellationToken.None);

        boards.Should().Equal(
            new TrackerBoard("3", "api", "kanban"),
            new TrackerBoard("40", "Mobile", "kanban"),
            new TrackerBoard("12", "Web board", "scrum"));
        handler.Requests[0].Url.Should().Be("https://acme.atlassian.net/rest/agile/1.0/board?projectKeyOrId=SHOP&maxResults=50&startAt=0");
    }

    [Fact]
    public async Task Preview_OnABoard_AndsItsFilter_ReadLive()
    {
        var handler = WithBoard(new FakeTrackerHandler()
            .On(HttpMethod.Post, "/search/approximate-count", """{"count":2}""")
            .On(HttpMethod.Post, "/search/jql", """{"issues":[],"isLast":true}"""));
        var client = new JiraCloudClient(new HttpClient(handler));

        var preview = await client.PreviewAsync(Credential, "SHOP", [], null, false, ["To Do"], TrackerScopeModes.All,
            CancellationToken.None, WebBoard);

        preview.EffectiveQuery.Should().Be(
            "project = \"SHOP\" AND assignee = currentUser() AND status in (\"To Do\") AND (project = SHOP AND component = Web)");
    }

    [Fact]
    public async Task Poll_OnABoard_NarrowsIntakeOnly_TrackingReadsKeysOffTheBoardToo()
    {
        var handler = WithBoard(new FakeTrackerHandler()
            .On(HttpMethod.Get, "/rest/api/3/myself", """{"accountId":"acc-dana"}""")
            .On(HttpMethod.Get, "/rest/api/3/field", "[]")
            .On(HttpMethod.Post, "/rest/api/3/search/jql", """{"issues":[],"isLast":true}"""));
        var client = new JiraCloudClient(new HttpClient(handler));

        await client.FetchTicketsAsync(Credential,
            new TrackerPollAssignment("cfg", "jira-acme", TrackerKinds.Jira, "SHOP", [], null, false,
                [new TrackerKnownTicket("SHOP-9", null, null)], TrackerScopeModes.All, ["To Do"], "12", "Web board"),
            CancellationToken.None);

        var jql = handler.Requests.Where(r => r.Url.Contains("/search/jql", StringComparison.Ordinal))
            .Select(r => FakeTrackerHandler.JqlOf(r.Body!)).ToList();
        jql.Should().Equal(
            "project = \"SHOP\" AND assignee = currentUser() AND status in (\"To Do\") AND (project = SHOP AND component = Web) ORDER BY key ASC",
            "key in (\"SHOP-9\")");
    }

    [Fact]
    public async Task Poll_ABoardThatIsGone_Fails_NeverWidens()
    {
        var handler = new FakeTrackerHandler()
            .On(HttpMethod.Get, "/rest/api/3/myself", """{"accountId":"acc-dana"}""")
            .On(HttpMethod.Get, "/rest/agile/1.0/board/12/configuration", """{"errorMessages":["Board does not exist"]}""", HttpStatusCode.NotFound)
            .On(HttpMethod.Post, "/rest/api/3/search/jql", """{"issues":[],"isLast":true}""");
        var client = new JiraCloudClient(new HttpClient(handler));

        var act = () => client.FetchTicketsAsync(Credential,
            new TrackerPollAssignment("cfg", "jira-acme", TrackerKinds.Jira, "SHOP", [], null, false, [],
                TrackerScopeModes.All, null, "12", "Web board"),
            CancellationToken.None);

        (await act.Should().ThrowAsync<TrackerException>()).Which.Message.Should()
            .Be("The board 'Web board' was not found in SHOP — pick another board in the ticket source settings.");
        handler.Requests.Should().NotContain(r => r.Url.Contains("/search/jql", StringComparison.Ordinal), "nothing is read without the board");
    }

    [Fact]
    public async Task Preview_ABoardWhoseFilterIsNotShared_SaysSo()
    {
        var handler = new FakeTrackerHandler()
            .On(HttpMethod.Get, "/rest/agile/1.0/board/12/configuration", """{"filter":{"id":"10040"}}""")
            .On(HttpMethod.Get, "/rest/api/3/filter/10040", "{}", HttpStatusCode.NotFound);
        var client = new JiraCloudClient(new HttpClient(handler));

        var act = () => client.PreviewAsync(Credential, "SHOP", [], null, false, null, TrackerScopeModes.All, CancellationToken.None, WebBoard);

        (await act.Should().ThrowAsync<TrackerException>()).Which.Message.Should().Contain("filter of the board 'Web board' cannot be read");
    }
}

public sealed class AdoBoardClientTests
{
    private const string Pat = "ado-pat-0123456789abcdefghijklmnop";
    private static readonly TrackerCredential Credential = new("https://dev.azure.com/contoso", "", Pat);
    private const string TeamId = "6f1c1f3a-0000-4000-8000-00000000web1";

    private const string Types = """
        {"value":[{"name":"User Story","states":[{"name":"New","category":"Proposed"},{"name":"Closed","category":"Completed"}]}]}
        """;

    private const string Categories = """
        {"value":[{"referenceName":"Microsoft.RequirementCategory","workItemTypes":[{"name":"User Story"}]}]}
        """;

    private const string TeamFields = """
        {"field":{"referenceName":"System.AreaPath"},"defaultValue":"Shop\\Web",
         "values":[{"value":"Shop\\Web","includeChildren":true},{"value":"Shop\\Shared","includeChildren":false}]}
        """;

    private static FakeTrackerHandler Handler() => new FakeTrackerHandler()
        .On(HttpMethod.Get, "/_apis/connectionData", """{"authenticatedUser":{"id":"me-0001","providerDisplayName":"Dana"}}""")
        .On(HttpMethod.Get, "/_apis/wit/workitemtypes?", Types)
        .On(HttpMethod.Get, "/_apis/wit/workitemtypecategories", Categories)
        .On(HttpMethod.Get, $"/Shop/{TeamId}/_apis/work/teamsettings/teamfieldvalues", TeamFields)
        .On(HttpMethod.Post, "/_apis/wit/wiql", """{"workItems":[]}""")
        .On(HttpMethod.Post, "/_apis/wit/workitemsbatch", """{"value":[]}""");

    [Fact]
    public async Task Boards_AreTheProjectsTeams_ByName()
    {
        var handler = new FakeTrackerHandler().On(HttpMethod.Get, "/_apis/projects/Shop/teams", """
            {"value":[{"id":"t-2","name":"Web"},{"id":"t-1","name":"api team"},{"id":"t-3"}],"count":3}
            """);
        var client = new AzureDevOpsClient(new HttpClient(handler));

        var boards = await client.BoardsAsync(Credential, "Shop", CancellationToken.None);

        boards.Should().Equal(new TrackerBoard("t-1", "api team"), new TrackerBoard("t-2", "Web"));
        handler.Requests.Single().Url.Should().Be("https://dev.azure.com/contoso/_apis/projects/Shop/teams?$top=500&api-version=7.1");
    }

    [Fact]
    public async Task Preview_OnATeamsBoard_IsLimitedToItsAreaPaths()
    {
        var handler = Handler();
        var client = new AzureDevOpsClient(new HttpClient(handler));

        var preview = await client.PreviewAsync(Credential, "Shop", [], null, false, null, TrackerScopeModes.All,
            CancellationToken.None, new TrackerBoardRef(TeamId, "Web"));

        preview.EffectiveQuery.Should().Contain(
            @" AND ([System.AreaPath] UNDER 'Shop\Web' OR [System.AreaPath] = 'Shop\Shared') ORDER BY [System.ChangedDate] DESC");
    }

    [Fact]
    public async Task Poll_OnATeamsBoard_NarrowsIntakeOnly_TrackedIdsAreStillRead()
    {
        var handler = Handler();
        var client = new AzureDevOpsClient(new HttpClient(handler));

        await client.FetchTicketsAsync(Credential,
            new TrackerPollAssignment("cfg", "ado-contoso", TrackerKinds.AzureDevOps, "Shop", [], "[System.Tags] CONTAINS 'api'", false,
                [new TrackerKnownTicket("77", null, null)], TrackerScopeModes.All, ["New"], TeamId, "Web"),
            CancellationToken.None);

        var wiql = FakeTrackerHandler.WiqlOf(handler.Requests.Single(r => r.Url.Contains("/wiql", StringComparison.Ordinal)).Body!);
        wiql.Should().EndWith(
            @" AND ([System.AreaPath] UNDER 'Shop\Web' OR [System.AreaPath] = 'Shop\Shared') AND ([System.Tags] CONTAINS 'api') ORDER BY [System.Id]");
        var tracking = JsonDocument.Parse(handler.Requests.Single(r => r.Url.Contains("workitemsbatch", StringComparison.Ordinal)).Body!).RootElement;
        tracking.GetProperty("ids").EnumerateArray().Select(i => i.GetInt32()).Should().Equal([77], "a tracked item is read by id, on the board or not");
    }

    [Fact]
    public async Task Poll_ATeamThatIsGone_Fails_NeverWidens()
    {
        var handler = Handler().On(HttpMethod.Get, "/teamsettings/teamfieldvalues",
            """{"message":"TF400898: The team does not exist."}""", HttpStatusCode.NotFound);
        var client = new AzureDevOpsClient(new HttpClient(handler));

        var act = () => client.FetchTicketsAsync(Credential,
            new TrackerPollAssignment("cfg", "ado-contoso", TrackerKinds.AzureDevOps, "Shop", [], null, false, [],
                TrackerScopeModes.All, null, TeamId, "Web"),
            CancellationToken.None);

        (await act.Should().ThrowAsync<TrackerException>()).Which.Message.Should()
            .Be("The board 'Web' was not found in Shop — pick another board in the ticket source settings.");
        handler.Requests.Should().NotContain(r => r.Url.Contains("/wiql", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Poll_ATeamWithNoAreaPaths_Fails()
    {
        var handler = Handler().On(HttpMethod.Get, "/teamsettings/teamfieldvalues", """{"field":{"referenceName":"System.AreaPath"},"values":[]}""");
        var client = new AzureDevOpsClient(new HttpClient(handler));

        var act = () => client.FetchTicketsAsync(Credential,
            new TrackerPollAssignment("cfg", "ado-contoso", TrackerKinds.AzureDevOps, "Shop", [], null, false, [],
                TrackerScopeModes.All, null, TeamId, "Web"),
            CancellationToken.None);

        (await act.Should().ThrowAsync<TrackerException>()).Which.Message.Should().Contain("has no area paths");
    }
}
