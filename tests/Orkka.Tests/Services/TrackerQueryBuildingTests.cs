namespace Orkka.Tests.Services;

using FluentAssertions;
using Orkka.LocalRunner.Contracts;
using Orkka.LocalRunner.Trackers;

/// <summary>
/// The JQL/WIQL the runner builds from what a user typed into the Ticket source settings, the
/// site URLs it normalizes, and the connection ids it mints. User text goes into a query
/// language here, so the escaping is what keeps a search box from rewriting the query.
/// </summary>
public sealed class TrackerQueryBuildingTests
{
    [Theory]
    [InlineData("plain", "\"plain\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"back\slash", "\"back\\\\slash\"")]
    public void JqlLiterals_EscapeQuotesAndBackslashes(string input, string expected)
        => JiraQueries.Literal(input).Should().Be(expected);

    // ── Intake and tracking (P6) ────────────────────────────────────────────

    [Fact]
    public void JiraIntake_AllMyTickets_IsTheProjectTheAssigneeAndTheStatuses()
        => JiraQueries.IntakeJql("SHOP", ["To Do", " Ready \"now\" ", "to do"], null, null, null).Should()
            .Be("project = \"SHOP\" AND assignee = currentUser() AND status in (\"To Do\", \"Ready \\\"now\\\"\")");

    [Fact]
    public void JiraIntake_NoStatuses_IsTheToDoCategory_AndEpicsAddTheirParentClause()
        => JiraQueries.IntakeJql("SHOP", [], ["shop-1"], ["SHOP-5"], "labels = api").Should()
            .Be("project = \"SHOP\" AND assignee = currentUser() AND statusCategory = \"To Do\" AND parent in (\"SHOP-1\", \"SHOP-5\") AND (labels = api)");

    [Fact]
    public void JiraTracking_IsTheKeysWhateverTheirStatus()
        => JiraQueries.TrackingJql(["shop-7", "SHOP-8"]).Should().Be("key in (\"SHOP-7\", \"SHOP-8\")");

    [Fact]
    public void AdoIntake_IsAssignedToMe_InTheIntakeStates_WithoutContainers()
        => AdoQueries.IntakeWiql(["New", "Approved", "new", "It's"], ["Feature", "Epic"], null, "[System.Tags] CONTAINS 'api'").Should()
            .Be("SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project AND [System.AssignedTo] = @Me"
                + " AND [System.State] IN ('New', 'Approved', 'It''s') AND [System.WorkItemType] NOT IN ('Epic', 'Feature')"
                + " AND ([System.Tags] CONTAINS 'api')");

    [Fact]
    public void AdoIntake_UnderEpics_IsLimitedToTheirDescendants_AndNeedsAState()
    {
        AdoQueries.IntakeWiql(["New"], [], [3, 4], null).Should().EndWith("AND [System.Id] IN (3, 4)");
        var act = () => AdoQueries.IntakeWiql([" "], [], null, null);
        act.Should().Throw<TrackerException>();
    }

    [Fact]
    public void JiraEpics_WithoutSearch_AreTheRecentEpics()
        => JiraQueries.EpicsJql("ACME", null).Should()
            .Be("project = \"ACME\" AND issuetype = Epic ORDER BY updated DESC");

    [Fact]
    public void JiraEpics_TextSearch_IsAPrefixSummaryMatch_WithLuceneOperatorsRemoved()
        => JiraQueries.EpicsJql("ACME", "  login \"page\" (v2)* ").Should()
            .Be("project = \"ACME\" AND issuetype = Epic AND summary ~ \"login page v2*\" ORDER BY updated DESC");

    [Theory]
    [InlineData("acme-12", "ACME-12")]
    [InlineData("OTHER-7", "OTHER-7")]
    [InlineData("12", "ACME-12")]
    public void JiraEpics_KeySearch_MatchesTheKeyExactly(string search, string key)
        => JiraQueries.EpicsJql("ACME", search).Should()
            .Be($"project = \"ACME\" AND issuetype = Epic AND key = \"{key}\" ORDER BY updated DESC");

    [Fact]
    public void JiraPreview_IsOpenAssignedChildrenOfTheEpics_PlusTheExtraClause()
        => JiraQueries.PreviewJql(["ACME-1", "acme-2"], null, " labels = orkka ").Should()
            .Be("parent in (\"ACME-1\", \"ACME-2\") AND assignee = currentUser() AND statusCategory != Done AND (labels = orkka)");

    [Fact]
    public void JiraPreview_WithDescendants_IncludesTheChildrenAsParents()
        => JiraQueries.PreviewJql(["ACME-1"], ["ACME-5", "ACME-1"], null).Should()
            .Be("parent in (\"ACME-1\", \"ACME-5\") AND assignee = currentUser() AND statusCategory != Done");

    [Theory]
    [InlineData("new", "todo")]
    [InlineData("indeterminate", "inprogress")]
    [InlineData("done", "done")]
    public void JiraStatusCategories_MapOntoTheThree(string key, string expected)
        => JiraQueries.StatusCategory(key).Should().Be(expected);

    [Fact]
    public void WiqlLiterals_DoubleSingleQuotes()
        => AdoQueries.Literal("it's").Should().Be("'it''s'");

    [Fact]
    public void AdoEpics_TitleSearch_IsEscaped()
        => AdoQueries.EpicsWiql("Bob's epic").Should().Be(
            "SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project AND [System.WorkItemType] = 'Epic' "
            + "AND [System.Title] CONTAINS 'Bob''s epic' ORDER BY [System.ChangedDate] DESC");

    [Theory]
    [InlineData("42")]
    [InlineData("#42")]
    public void AdoEpics_IdSearch_MatchesTheIdExactly(string search)
        => AdoQueries.EpicsWiql(search).Should().Contain("AND [System.Id] = 42 ORDER BY");

    [Theory]
    [InlineData(false, "MODE (MustContain)")]
    [InlineData(true, "MODE (Recursive)")]
    public void AdoDescendants_UseTheHierarchyLink_InTheRightMode(bool recursive, string mode)
        => AdoQueries.DescendantsWiql([1, 2], recursive).Should().Be(
            "SELECT [System.Id] FROM WorkItemLinks WHERE [Source].[System.Id] IN (1, 2) "
            + $"AND [System.Links.LinkType] = 'System.LinkTypes.Hierarchy-Forward' {mode}");

    [Fact]
    public void AdoPreview_FiltersAssignedToMe_NotDone_AndTheExtraClause()
        => AdoQueries.PreviewWiql([5, 6], ["Done", "Removed", "done"], "[System.Tags] CONTAINS 'orkka'").Should().Be(
            "SELECT [System.Id] FROM WorkItems WHERE [System.Id] IN (5, 6) AND [System.AssignedTo] = @Me "
            + "AND [System.State] NOT IN ('Done', 'Removed') AND ([System.Tags] CONTAINS 'orkka')");

    [Theory]
    [InlineData("Proposed", "todo")]
    [InlineData("InProgress", "inprogress")]
    [InlineData("Resolved", "inprogress")]
    [InlineData("Completed", "done")]
    [InlineData("Removed", null)]
    public void AdoStateCategories_MapOntoTheThree(string category, string? expected)
        => AdoQueries.StateCategory(category).Should().Be(expected);

    [Theory]
    [InlineData("acme", "https://acme.atlassian.net")]
    [InlineData("acme.atlassian.net", "https://acme.atlassian.net")]
    [InlineData("https://ACME.atlassian.net/jira/software/projects/X/boards/1", "https://acme.atlassian.net")]
    [InlineData("https://jira.acme.com/", "https://jira.acme.com")]
    [InlineData("", null)]
    [InlineData("ftp://acme.atlassian.net", null)]
    public void JiraSites_Normalize(string input, string? expected)
        => TrackerSites.NormalizeJiraSite(input).Should().Be(expected);

    [Theory]
    [InlineData("contoso", "https://dev.azure.com/contoso")]
    [InlineData("https://dev.azure.com/contoso", "https://dev.azure.com/contoso")]
    [InlineData("https://dev.azure.com/contoso/Big%20Project/_boards", "https://dev.azure.com/contoso")]
    [InlineData("dev.azure.com/contoso/", "https://dev.azure.com/contoso")]
    [InlineData("https://contoso.visualstudio.com", "https://dev.azure.com/contoso")]
    [InlineData("https://contoso.visualstudio.com/DefaultCollection", "https://dev.azure.com/contoso")]
    [InlineData("https://tfs.contoso.local/tfs", null)]
    [InlineData("https://dev.azure.com/", null)]
    public void AdoOrganizations_Normalize(string input, string? expected)
        => TrackerSites.NormalizeAdoOrg(input).Should().Be(expected);

    [Fact]
    public void Ids_AreKindAndSite_SuffixedWhenTaken()
    {
        TrackerSites.UniqueId(TrackerKinds.Jira, "https://acme.atlassian.net", []).Should().Be("jira-acme");
        TrackerSites.UniqueId(TrackerKinds.Jira, "https://acme.atlassian.net", ["jira-acme"]).Should().Be("jira-acme-2");
        TrackerSites.UniqueId(TrackerKinds.Jira, "https://acme.atlassian.net", ["JIRA-ACME", "jira-acme-2"]).Should().Be("jira-acme-3");
        TrackerSites.UniqueId(TrackerKinds.AzureDevOps, "https://dev.azure.com/Contoso_Ltd", []).Should().Be("ado-contoso-ltd");
        TrackerSites.UniqueId(TrackerKinds.Jira, "https://jira.acme.com", []).Should().Be("jira-jira-acme-com");
    }
}
