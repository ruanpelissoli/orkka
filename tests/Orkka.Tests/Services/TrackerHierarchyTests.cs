namespace Orkka.Tests.Services;

using System.Text.Json;
using FluentAssertions;
using Orkka.LocalRunner.Contracts;
using Orkka.LocalRunner.Trackers;
using Orkka.Modules.Trackers.PublicApi;

/// <summary>
/// How each tracker's hierarchy maps onto Orkka's levels (<see cref="TicketLevel"/>) and how its
/// dependency links are read — and the host's rule that only a story-level ticket with open
/// sub-tasks becomes a Story, never an epic or a sub-task.
/// </summary>
public sealed class TrackerHierarchyTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    // ── Jira ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("""{"name":"Epic","hierarchyLevel":1}""", TicketLevel.Above)]
    [InlineData("""{"name":"Initiative","hierarchyLevel":2}""", TicketLevel.Above)]
    [InlineData("""{"name":"Story","hierarchyLevel":0}""", TicketLevel.Story)]
    [InlineData("""{"name":"Bug","hierarchyLevel":0}""", TicketLevel.Story)]
    [InlineData("""{"name":"Sub-task","hierarchyLevel":-1,"subtask":true}""", TicketLevel.SubTask)]
    [InlineData("""{"name":"Sub-task","subtask":true}""", TicketLevel.SubTask)]
    [InlineData("""{"name":"Epic"}""", TicketLevel.Above)]
    [InlineData("""{"name":"Story"}""", TicketLevel.Story)]
    public void Jira_LevelComesFromTheIssueTypesHierarchyLevel(string issueType, TicketLevel expected)
        => JiraCloudClient.LevelOf(Parse(issueType)).Should().Be(expected);

    [Fact]
    public void Jira_BlockedBy_IsTheInwardSideOfABlocksLink()
    {
        var fields = Parse("""
            {"issuelinks":[
              {"type":{"name":"Blocks","inward":"is blocked by","outward":"blocks"},"inwardIssue":{"key":"shop-9"}},
              {"type":{"name":"Blocks","inward":"is blocked by","outward":"blocks"},"outwardIssue":{"key":"SHOP-30"}},
              {"type":{"name":"Relates","inward":"relates to","outward":"relates to"},"inwardIssue":{"key":"SHOP-40"}}
            ]}
            """);

        JiraCloudClient.BlockedBy(fields).Should().Equal(["SHOP-9"],
            "SHOP-30 is blocked BY this issue, and a relates-to link waits for nothing");
    }

    // ── Azure DevOps ────────────────────────────────────────────────────

    private static readonly IReadOnlyDictionary<string, List<string>> AgileCategories = new Dictionary<string, List<string>>
    {
        ["Microsoft.EpicCategory"] = ["Epic"],
        ["Microsoft.FeatureCategory"] = ["Feature"],
        ["Microsoft.RequirementCategory"] = ["User Story"],
        ["Microsoft.TaskCategory"] = ["Task"],
        ["Microsoft.BugCategory"] = ["Bug"],
        ["Microsoft.HiddenCategory"] = ["Code Review Request"],
    };

    private static readonly TrackerCredential Ado = new("https://dev.azure.com/contoso", "", "pat");

    private static RawTicket? WorkItem(int id, string type, int? parent = null, string? parentType = null, int[]? predecessors = null)
    {
        var relations = new List<string>();
        if (parent is { } p)
            relations.Add($$"""{"rel":"System.LinkTypes.Hierarchy-Reverse","url":"https://dev.azure.com/contoso/_apis/wit/workItems/{{p}}"}""");
        foreach (var pred in predecessors ?? [])
            relations.Add($$"""{"rel":"System.LinkTypes.Dependency-Reverse","url":"https://dev.azure.com/contoso/_apis/wit/workItems/{{pred}}"}""");
        var item = Parse($$"""
            {"id":{{id}},"fields":{"System.WorkItemType":"{{type}}","System.Title":"T","System.State":"New","System.TeamProject":"Shop"},
             "relations":[{{string.Join(",", relations)}}]}
            """);
        return AzureDevOpsClient.ParseWorkItem(Ado, "Shop", item, [], [], [], info: _ => new AzureDevOpsClient.WorkItemInfo(parentType, null),
            levels: AzureDevOpsClient.LevelsOf(AgileCategories));
    }

    [Fact]
    public void Ado_LevelComesFromTheTypesCategory_WhateverTheProcess()
    {
        WorkItem(1, "Feature")!.Level.Should().Be(TicketLevel.Above);
        WorkItem(2, "Epic")!.Level.Should().Be(TicketLevel.Above);
        var story = WorkItem(3, "User Story", parent: 1, parentType: "Feature")!;
        story.Level.Should().Be(TicketLevel.Story);
        story.ParentLevel.Should().Be(TicketLevel.Above);
        var task = WorkItem(4, "Task", parent: 3, parentType: "User Story")!;
        task.Level.Should().Be(TicketLevel.SubTask);
        task.ParentLevel.Should().Be(TicketLevel.Story);
    }

    [Fact]
    public void Ado_ABug_IsASubTaskUnderAStory_AndAStoryOnItsOwn()
    {
        WorkItem(5, "Bug", parent: 3, parentType: "User Story")!.Level.Should().Be(TicketLevel.SubTask);
        WorkItem(6, "Bug")!.Level.Should().Be(TicketLevel.Story);
        WorkItem(7, "Bug", parent: 1, parentType: "Feature")!.Level.Should().Be(TicketLevel.Story);
        var underBug = WorkItem(8, "Task", parent: 6, parentType: "Bug")!;
        underBug.ParentLevel.Should().Be(TicketLevel.Story, "a bug with tasks under it is that story");
    }

    [Fact]
    public void Ado_Predecessors_AreWhatTheItemIsBlockedBy()
        => WorkItem(9, "User Story", predecessors: [41, 42, 41])!.BlockedByKeys.Should().Equal(["41", "42"]);

    // ── GitHub ──────────────────────────────────────────────────────────

    [Theory]
    // type, hasParent, parentType, children, childrenHaveChildren → level, parentLevel
    [InlineData(null, false, null, 0, false, TicketLevel.Story, TicketLevel.Unknown)]
    [InlineData(null, false, null, 3, false, TicketLevel.Story, TicketLevel.Unknown)]
    [InlineData(null, false, null, 3, true, TicketLevel.Above, TicketLevel.Unknown)]
    [InlineData("Epic", false, null, 0, false, TicketLevel.Above, TicketLevel.Unknown)]
    [InlineData(null, true, null, 0, false, TicketLevel.SubTask, TicketLevel.Story)]
    [InlineData("Task", true, "Feature", 0, false, TicketLevel.Story, TicketLevel.Above)]
    [InlineData(null, true, "Epic", 2, false, TicketLevel.Story, TicketLevel.Above)]
    public void GitHub_LevelIsByDepth_WithTheIssueTypeAsAHint(string? type, bool hasParent, string? parentType,
        int children, bool childrenHaveChildren, TicketLevel level, TicketLevel parentLevel)
        => GitHubProjects.LevelOf(type, hasParent, parentType, children, childrenHaveChildren)
            .Should().Be((level, parentLevel));

    [Fact]
    public void GitHub_ParseIssue_ReadsSubIssueDepthAndBlockedBy()
    {
        var node = Parse("""
            {"id":"I_1","number":7,"title":"Checkout","url":"https://github.com/acme/shop/issues/7","state":"OPEN",
             "repository":{"nameWithOwner":"acme/shop","url":"https://github.com/acme/shop"},
             "assignees":{"nodes":[{"login":"dana"}]},"labels":{"nodes":[]},
             "parent":{"number":1,"repository":{"nameWithOwner":"acme/shop"},"issueType":{"name":"Feature"}},
             "subIssues":{"nodes":[
               {"number":8,"repository":{"nameWithOwner":"acme/shop"},"subIssuesSummary":{"total":0}},
               {"number":9,"repository":{"nameWithOwner":"acme/shop"},"subIssuesSummary":{"total":0}}]},
             "blockedBy":{"nodes":[{"number":3,"repository":{"nameWithOwner":"acme/api"}}]}}
            """);

        var issue = GitHubProjectsClient.ParseIssue(node)!;

        issue.Level.Should().Be(TicketLevel.Story);
        issue.ParentLevel.Should().Be(TicketLevel.Above);
        issue.ParentType.Should().Be("Feature");
        issue.ChildKeys.Should().Equal(["acme/shop#8", "acme/shop#9"]);
        issue.BlockedByKeys.Should().Equal(["acme/api#3"]);
    }

    [Fact]
    public void GitHub_AnIssueWhoseSubIssuesHaveSubIssues_IsAboveAStory_AndNeverPickedUp()
    {
        var node = Parse("""
            {"id":"I_1","number":1,"title":"Payments","url":"u","state":"OPEN",
             "repository":{"nameWithOwner":"acme/shop","url":"https://github.com/acme/shop"},
             "assignees":{"nodes":[{"login":"dana"}]},"labels":{"nodes":[]},
             "subIssues":{"nodes":[{"number":7,"repository":{"nameWithOwner":"acme/shop"},"subIssuesSummary":{"total":2}}]}}
            """);
        var issue = GitHubProjectsClient.ParseIssue(node)!;

        issue.Level.Should().Be(TicketLevel.Above);
        new GitHubProjectsClient.Item("PVTI_1", "Todo", issue, "dana").InIntake(["Todo"], null)
            .Should().BeFalse("Orkka works the stories under it, not the container");
    }

    // ── Host: what becomes a Story ──────────────────────────────────────

    private static TrackerTicket Ticket(string key, TicketLevel level, string[]? children = null, bool inIntake = true,
        string? parent = null, string? parentType = null, TicketLevel parentLevel = TicketLevel.Unknown)
        => new(key, key, $"https://acme.atlassian.net/browse/{key}", null, "hash",
            new TrackerTicketBody($"Title {key}", "Story", "To Do", "todo", null, "Body", null, null, [], [], null),
            [], "To Do", "todo", AssignedToMe: true, InIntake: inIntake, ParentKey: parent, ParentType: parentType,
            ChildKeys: children ?? [])
        {
            Level = level,
            ParentLevel = parentLevel,
        };

    [Fact]
    public void OnlyAStoryLevelTicketWithSubTasks_IsAStory()
    {
        TrackersApi.IsStoryCandidate(Ticket("SHOP-10", TicketLevel.Story, ["SHOP-11"])).Should().BeTrue();
        TrackersApi.IsStoryCandidate(Ticket("SHOP-10", TicketLevel.Unknown, ["SHOP-11"])).Should().BeTrue("an older runner sends no level");
        TrackersApi.IsStoryCandidate(Ticket("SHOP-10", TicketLevel.Story)).Should().BeFalse("no sub-tasks: a task of its own");
        TrackersApi.IsStoryCandidate(Ticket("SHOP-11", TicketLevel.SubTask, ["SHOP-12"])).Should().BeFalse("a sub-task is worked as a task");
        TrackersApi.IsStoryCandidate(Ticket("SHOP-1", TicketLevel.Above, ["SHOP-10"])).Should().BeFalse("an epic is never a story");
        TrackersApi.IsNewTaskCandidate(Ticket("SHOP-1", TicketLevel.Above)).Should().BeFalse("nor a task");
    }

    [Fact]
    public void PlanIntake_TurnsAStoryIntoAGroup_AndLeavesTheEpicAboveIt()
    {
        var tickets = new[]
        {
            Ticket("SHOP-1", TicketLevel.Above, ["SHOP-10"]),
            Ticket("SHOP-10", TicketLevel.Story, ["SHOP-11", "SHOP-12"], parent: "SHOP-1", parentType: "Epic", parentLevel: TicketLevel.Above),
            Ticket("SHOP-11", TicketLevel.SubTask, parent: "SHOP-10", parentType: "Story", parentLevel: TicketLevel.Story),
            Ticket("SHOP-12", TicketLevel.SubTask, parent: "SHOP-10", parentType: "Story", parentLevel: TicketLevel.Story),
        };

        var plan = TrackersApi.PlanIntake(tickets, [], ["To Do"], storyMode: true);

        plan.Stories.Should().ContainSingle().Which.Story.Key.Should().Be("SHOP-10");
        plan.Stories[0].Members.Select(m => m.Key).Should().Equal("SHOP-11", "SHOP-12");
        plan.Tasks.Should().BeEmpty();
        plan.Ignored.Select(t => t.Key).Should().Equal("SHOP-1");
    }

    [Fact]
    public void ParentContext_FollowsTheParentsLevel()
    {
        TrackersApi.WantsParentContext(Ticket("SHOP-11", TicketLevel.SubTask, parent: "SHOP-10", parentType: "Story",
            parentLevel: TicketLevel.Story)).Should().BeTrue();
        TrackersApi.WantsParentContext(Ticket("SHOP-10", TicketLevel.Story, parent: "SHOP-1", parentType: "Theme",
            parentLevel: TicketLevel.Above)).Should().BeFalse("a container above a story is not story context, whatever its name");
        TrackersApi.WantsParentContext(Ticket("SHOP-10", TicketLevel.Story, parent: "SHOP-1", parentType: "Epic"))
            .Should().BeFalse("an older runner: the name decides");
    }
}
