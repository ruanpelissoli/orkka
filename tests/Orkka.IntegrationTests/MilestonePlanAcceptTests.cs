namespace Orkka.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using Orkka.IntegrationTests.Fixtures;
using Orkka.IntegrationTests.Helpers;
using Orkka.Modules.Projects.Features.ProjectRepos;
using Orkka.Modules.Projects.Features.Projects;
using Orkka.Modules.Tasks.Features.Milestones;
using Orkka.Modules.Tasks.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The persistence half of on-demand planning. The propose half needs a live model call — the
/// test host points Anthropic at an invalid host on purpose — so it is exercised by the
/// decomposer's own contract rather than here.
/// </summary>
[Collection("Integration")]
public sealed class MilestonePlanAcceptTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<(Ulid ProjectId, Ulid RepoId)> SeedProjectAndRepo(string name)
    {
        var projectResp = await _client.PostAsJsonAsync("/api/projects",
            new CreateProjectRequest(name, "desc"));
        var project = await projectResp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);

        var repoResp = await _client.PostAsJsonAsync($"/api/projects/{project!.Id}/repos",
            new AddProjectRepoRequest("plan-repo", "https://github.com/test/plan.git", "main", null, null));
        var repo = await repoResp.Content.ReadFromJsonAsync<ProjectRepoResponse>(TestJsonOptions.Default);

        return (project.Id, repo!.Id);
    }

    private static IReadOnlyList<PlannedTaskResponse> TwoTasks() =>
    [
        new("Add the checkout page", "## What\nBuild it.", "High", "Feature"),
        new("Handle payment errors", "## What\nCover failures.", "Medium", "Improvement"),
    ];

    /// <summary>
    /// A gated cross-repo group is one unit of the build order. The planner wrote "every UI
    /// task after every API task" AND paired each UI task with an API task by contract key —
    /// persisted as-is, the first group could never merge (its UI half waited on later
    /// groups, whose halves waited on it) and nothing in the milestone ever started (prod,
    /// 2026-09-15). Edges may stay inside a group or point at an EARLIER group; the rest are
    /// dropped.
    /// </summary>
    [Fact]
    public async Task Accept_DropsBuildOrderEdges_ThatWouldDeadlockGatedGroups()
    {
        var (projectId, apiRepoId) = await SeedProjectAndRepo("Gated Groups Plan Project");
        var uiRepoResp = await _client.PostAsJsonAsync($"/api/projects/{projectId}/repos",
            new AddProjectRepoRequest("ui-repo", "https://github.com/test/plan-ui.git", "main", null, null));
        uiRepoResp.EnsureSuccessStatusCode();

        // The prod plan, key for key: three API tasks, then their UI halves each "after" every API task.
        IReadOnlyList<PlannedTaskResponse> tasks =
        [
            new("Theme API", "d", "High", "Feature", Key: 1, Repo: "plan-repo", ContractKey: 1),
            new("Catalog API", "d", "High", "Feature", Key: 2, Repo: "plan-repo", ContractKey: 2),
            new("Stock API", "d", "High", "Feature", Key: 3, DependsOn: [2], Repo: "plan-repo", ContractKey: 3),
            new("Theme UI", "d", "High", "Feature", Key: 4, DependsOn: [1, 2, 3], Repo: "ui-repo", ContractKey: 1),
            new("Storefront UI", "d", "High", "Feature", Key: 5, DependsOn: [1, 2, 3], Repo: "ui-repo"),
            new("Catalog UI", "d", "High", "Feature", Key: 6, DependsOn: [1, 2, 3], Repo: "ui-repo", ContractKey: 2),
            new("Stock UI", "d", "High", "Feature", Key: 7, DependsOn: [1, 2, 3, 6], Repo: "ui-repo", ContractKey: 3),
        ];
        IReadOnlyList<PlannedContractResponse> contracts =
            [new(1, "GET /theme"), new(2, "GET /products"), new(3, "GET /stock")];

        var response = await _client.PostAsJsonAsync($"/api/projects/{projectId}/milestones/accept",
            new AcceptMilestonePlanRequest("Design & Catalog", null, tasks, apiRepoId, Contracts: contracts));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var milestone = await response.Content.ReadFromJsonAsync<MilestoneResponse>(TestJsonOptions.Default);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var byTitle = await db.Tasks.AsNoTracking()
            .Where(t => t.MilestoneId == milestone!.Id)
            .ToDictionaryAsync(t => t.Title, t => t.Id);
        var edges = (await db.TaskDependencies.AsNoTracking()
                .Where(d => d.Task.MilestoneId == milestone!.Id)
                .Select(d => new { From = d.Task.Title, To = d.DependsOnTask.Title })
                .ToListAsync())
            .Select(e => (e.From, e.To))
            .ToHashSet();

        // Kept: inside a group, or pointing at an earlier group / task.
        edges.Should().Contain([
            ("Stock API", "Catalog API"),
            ("Theme UI", "Theme API"),
            ("Storefront UI", "Theme API"), ("Storefront UI", "Catalog API"), ("Storefront UI", "Stock API"),
            ("Catalog UI", "Theme API"), ("Catalog UI", "Catalog API"),
            ("Stock UI", "Theme API"), ("Stock UI", "Catalog API"), ("Stock UI", "Stock API"), ("Stock UI", "Catalog UI"),
        ]);
        // Dropped: from a group to a LATER group — the edges that deadlocked prod.
        edges.Should().NotContain([
            ("Theme UI", "Catalog API"), ("Theme UI", "Stock API"), ("Catalog UI", "Stock API"),
        ]);

        // And the group members really are gated pairs.
        var groups = await db.Tasks.AsNoTracking()
            .Where(t => t.MilestoneId == milestone!.Id && t.GroupId != null)
            .GroupBy(t => t.GroupId)
            .Select(g => g.Count())
            .ToListAsync();
        groups.Should().Equal(2, 2, 2);
        byTitle.Should().ContainKey("Storefront UI");
    }

    [Fact]
    public async Task Accept_CreatesTheMilestoneAndItsTasksTogether()
    {
        var (projectId, repoId) = await SeedProjectAndRepo("Accept Plan Project");

        var response = await _client.PostAsJsonAsync($"/api/projects/{projectId}/milestones/accept",
            new AcceptMilestonePlanRequest("Checkout", "Users can buy things.", TwoTasks(), repoId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var milestone = await response.Content.ReadFromJsonAsync<MilestoneResponse>(TestJsonOptions.Default);

        milestone!.Title.Should().Be("Checkout");
        milestone.Counts.Total.Should().Be(2);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var tasks = await db.Tasks.AsNoTracking()
            .Where(t => t.MilestoneId == milestone.Id)
            .ToListAsync();

        tasks.Should().HaveCount(2);
        tasks.Should().AllSatisfy(t =>
        {
            // Accepting a plan is not the same as approving the spend to run it — each task
            // still has to be submitted.
            t.CurrentLane.Should().Be(Orkka.Modules.Tasks.Contracts.Lane.Drafting);
            t.RepoId.Should().Be(repoId);
        });
        tasks.Select(t => t.Number).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Accept_IntoAnExistingMilestone_AddsTasksWithoutCreatingAnother()
    {
        var (projectId, repoId) = await SeedProjectAndRepo("Accept Into Existing Project");

        var createResp = await _client.PostAsJsonAsync($"/api/projects/{projectId}/milestones",
            new CreateMilestoneRequest("Existing", "Already planned."));
        var existing = await createResp.Content.ReadFromJsonAsync<MilestoneResponse>(TestJsonOptions.Default);

        var response = await _client.PostAsJsonAsync($"/api/projects/{projectId}/milestones/accept",
            new AcceptMilestonePlanRequest("Ignored title", "Ignored outcome", TwoTasks(),
                repoId, existing!.Id));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var returned = await response.Content.ReadFromJsonAsync<MilestoneResponse>(TestJsonOptions.Default);

        returned!.Id.Should().Be(existing.Id);
        returned.Title.Should().Be("Existing", "planning into a milestone must not rename it");

        var list = await _client.GetFromJsonAsync<MilestoneListResponse>(
            $"/api/projects/{projectId}/milestones", TestJsonOptions.Default);
        list!.Milestones.Should().HaveCount(1);
        list.Milestones[0].Counts.Total.Should().Be(2);
    }

    [Fact]
    public async Task Accept_WithNoTasks_IsRejected()
    {
        var (projectId, repoId) = await SeedProjectAndRepo("Empty Plan Project");

        var response = await _client.PostAsJsonAsync($"/api/projects/{projectId}/milestones/accept",
            new AcceptMilestonePlanRequest("Nothing", null, [], repoId));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Accept_WithARepoFromAnotherProject_IsRejected()
    {
        var (projectA, _) = await SeedProjectAndRepo("Accept Repo Scope A");
        var (_, repoB) = await SeedProjectAndRepo("Accept Repo Scope B");

        var response = await _client.PostAsJsonAsync($"/api/projects/{projectA}/milestones/accept",
            new AcceptMilestonePlanRequest("Cross project", null, TwoTasks(), repoB));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Accept_ForAnUnknownProject_Is404()
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{Ulid.NewUlid()}/milestones/accept",
            new AcceptMilestonePlanRequest("Nowhere", null, TwoTasks()));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
