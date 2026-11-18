namespace Orkka.IntegrationTests;

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
/// A plan in a multi-repo project must file each task in the repo it names. Accept used to stamp
/// one repo on every task, so a Go API task landed on the mobile app — and then ran its agent in
/// the wrong worktree.
/// </summary>
[Collection("Integration")]
public sealed class MilestonePlanRepoRoutingTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<(Ulid ProjectId, Ulid ApiRepoId, Ulid MobileRepoId)> SeedMultiRepoProject(string name)
    {
        var projectResp = await _client.PostAsJsonAsync("/api/projects",
            new CreateProjectRequest(name, "desc"));
        var project = await projectResp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);

        var apiResp = await _client.PostAsJsonAsync($"/api/projects/{project!.Id}/repos",
            new AddProjectRepoRequest("tapago-api", "https://github.com/t/api.git", "main", null, null));
        var api = await apiResp.Content.ReadFromJsonAsync<ProjectRepoResponse>(TestJsonOptions.Default);

        var mobileResp = await _client.PostAsJsonAsync($"/api/projects/{project.Id}/repos",
            new AddProjectRepoRequest("tapago-mobile", "https://github.com/t/mobile.git", "main", null, null));
        var mobile = await mobileResp.Content.ReadFromJsonAsync<ProjectRepoResponse>(TestJsonOptions.Default);

        return (project.Id, api!.Id, mobile!.Id);
    }

    [Fact]
    public async Task EachTaskLandsInTheRepoItNames()
    {
        var (projectId, apiRepoId, mobileRepoId) = await SeedMultiRepoProject("Repo Routing Project");

        var accept = await _client.PostAsJsonAsync($"/api/projects/{projectId}/milestones/accept",
            new AcceptMilestonePlanRequest("Bootstrap", "Both sides exist.",
            [
                new PlannedTaskResponse("Bootstrap Go API", "## What\nRouting and DB.",
                    "High", "Feature", 0, null, "tapago-api"),
                new PlannedTaskResponse("Bootstrap Expo app", "## What\nNavigation shell.",
                    "High", "Feature", 1, null, "tapago-mobile"),
            ],
            // The plan-level repo is the API one; the mobile task must NOT inherit it.
            apiRepoId));

        accept.IsSuccessStatusCode.Should().BeTrue();
        var milestone = await accept.Content.ReadFromJsonAsync<MilestoneResponse>(TestJsonOptions.Default);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var tasks = await db.Tasks.AsNoTracking()
            .Where(t => t.MilestoneId == milestone!.Id)
            .ToListAsync();

        tasks.Single(t => t.Title == "Bootstrap Go API").RepoId.Should().Be(apiRepoId);
        tasks.Single(t => t.Title == "Bootstrap Expo app").RepoId.Should().Be(mobileRepoId);

        // Work spanning two repos: labelling the milestone with one of them would be a lie.
        milestone!.RepoId.Should().BeNull();
    }

    [Fact]
    public async Task MissingRepoName_FallsBackToThePlanDefault()
    {
        var (projectId, apiRepoId, _) = await SeedMultiRepoProject("Repo Fallback Project");

        var accept = await _client.PostAsJsonAsync($"/api/projects/{projectId}/milestones/accept",
            new AcceptMilestonePlanRequest("Fallbacks", null,
            [
                new PlannedTaskResponse("Named no repo at all", "## What\nY.",
                    "Medium", "Feature", 0, null, null),
            ],
            apiRepoId));

        accept.IsSuccessStatusCode.Should().BeTrue();
        var milestone = await accept.Content.ReadFromJsonAsync<MilestoneResponse>(TestJsonOptions.Default);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var tasks = await db.Tasks.AsNoTracking()
            .Where(t => t.MilestoneId == milestone!.Id)
            .ToListAsync();

        // No name at all is not a mistake to reject — the plan-level default answers it, and
        // a task must never land with no repo (SubmitTask refuses those, stranding the task).
        tasks.Should().ContainSingle().Which.RepoId.Should().Be(apiRepoId);
        milestone!.RepoId.Should().Be(apiRepoId);
    }

    [Fact]
    public async Task UnknownRepoName_IsRejectedWhileThePlanIsStillEditable()
    {
        var (projectId, _, _) = await SeedMultiRepoProject("Repo Reject Project");

        var accept = await _client.PostAsJsonAsync($"/api/projects/{projectId}/milestones/accept",
            new AcceptMilestonePlanRequest("Hallucinations", null,
            [
                new PlannedTaskResponse("Named a repo that doesn't exist", "## What\nX.",
                    "Medium", "Feature", 0, null, "tapago-hallucinated"),
            ],
            RepoId: null));

        // The silent fallback this replaced filed the task against the project's first repo,
        // and the agent then planned against code that isn't there. Rejecting at accept time
        // is the one moment the user can still fix the plan — the error names the bad repo
        // and the valid ones.
        accept.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        var body = await accept.Content.ReadAsStringAsync();
        body.Should().Contain("tapago-hallucinated");
        body.Should().Contain("tapago-api");
        body.Should().Contain("tapago-mobile");

        // Nothing half-created: no milestone, no tasks.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        (await db.Milestones.AsNoTracking().AnyAsync(m => m.Title == "Hallucinations"))
            .Should().BeFalse();
    }
}
