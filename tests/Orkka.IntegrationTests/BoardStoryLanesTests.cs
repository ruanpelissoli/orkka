namespace Orkka.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orkka.IntegrationTests.Fixtures;
using Orkka.IntegrationTests.Helpers;
using Orkka.Modules.Projects.Features.Projects;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Features.Tasks;
using Orkka.Modules.Tasks.Infrastructure;

/// <summary>
/// The board's Stories (the "Group by: Story" swimlane headers) and tracker dependency links,
/// on the real SQLite database: the board lists every story with a card on it, titled without
/// its key, with its member and done counts; a story blocked by another waits from its first
/// member on the blocker's last.
/// </summary>
[Collection("Integration")]
public sealed class BoardStoryLanesTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<ProjectResponse> SeedProjectAsync()
    {
        var response = await _client.PostAsJsonAsync("/api/projects", new CreateProjectRequest("Story Shop", "desc"));
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default))!;
    }

    private static TrackerStorySpec Story(Ulid orgId, Ulid projectId, string key, params string[] subs)
        => new(orgId, projectId, TestCurrentUser.TestUserId, key, $"https://acme.atlassian.net/browse/{key}",
            $"[{key}] Story {key}", "The story.", $"feature/{key}",
            [.. subs.Select(s => new StorySubTaskSpec(s, $"https://acme.atlassian.net/browse/{s}",
                $"jira:acme.atlassian.net:{Ulid.NewUlid()}:{s}", $"[{s}] Do {s}", "Body", TaskType.Feature, Priority.Medium, [], []))]);

    private static async Task InWorkspace(Func<Ulid, Task> body)
    {
        var orgId = Ulid.NewUlid();
        TestCurrentUser.CurrentOrgId = orgId;
        try
        {
            await body(orgId);
        }
        finally
        {
            TestCurrentUser.Reset();
        }
    }

    [Fact]
    public async Task TheBoardListsItsStories_AndTrackerLinksBecomeDependencies() => await InWorkspace(async orgId =>
    {
        var project = await SeedProjectAsync();
        using var scope = factory.Services.CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var first = await api.CreateTrackerStoryAsync(Story(orgId, project.Id, "SHOP-10", "SHOP-11", "SHOP-12"));
        var second = await api.CreateTrackerStoryAsync(Story(orgId, project.Id, "SHOP-20", "SHOP-21"));

        await api.ReconcileTrackerDependenciesAsync(project.Id,
            [new TrackerBlockEdge(new TrackerBlockEnd(null, second.GroupId), new TrackerBlockEnd(null, first.GroupId), "SHOP-10")],
            new Dictionary<Ulid, IReadOnlyList<string>> { [first.Members[0].TaskId] = ["OPS-9"] });

        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var edge = await db.TaskDependencies.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(d => d.Source == DependencySource.Tracker && d.TaskId == second.Members[0].TaskId);
        edge.DependsOnTaskId.Should().Be(first.Members[^1].TaskId);

        var response = await _client.GetAsync($"/api/board?projectId={project.Id}");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var board = (await response.Content.ReadFromJsonAsync<TaskBoardResponse>(TestJsonOptions.Default))!;

        board.Stories.Should().NotBeNull();
        var stories = board.Stories!.ToDictionary(s => s.Key!);
        stories.Keys.Should().BeEquivalentTo(["SHOP-10", "SHOP-20"]);
        stories["SHOP-10"].Title.Should().Be("Story SHOP-10", "the swimlane shows the key on its own");
        stories["SHOP-10"].MemberCount.Should().Be(2);
        stories["SHOP-10"].DoneCount.Should().Be(0);
        stories["SHOP-10"].BranchName.Should().Be("feature/SHOP-10");
        stories["SHOP-10"].PullRequests.Should().BeEmpty();

        var cards = board.Lanes.SelectMany(l => l.Tasks).ToList();
        cards.Single(c => c.Id == first.Members[0].TaskId).ExternalBlockedBy.Should().Be("OPS-9");
        cards.Single(c => c.Id == second.Members[0].TaskId).HasUnmetDependencies.Should().BeTrue("it waits for SHOP-10 to merge");
    });
}
