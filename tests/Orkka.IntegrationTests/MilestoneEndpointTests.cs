namespace Orkka.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using Orkka.IntegrationTests.Fixtures;
using Orkka.IntegrationTests.Helpers;
using Orkka.Modules.Projects.Features.ProjectRepos;
using Orkka.Modules.Projects.Features.Projects;
using Orkka.Modules.Tasks.Features.Milestones;
using Orkka.Modules.Tasks.Features.Tasks;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.SharedKernel.Events;
using Orkka.SharedKernel.Events.Catalog;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

[Collection("Integration")]
public sealed class MilestoneEndpointTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<(Ulid ProjectId, Ulid RepoId)> SeedProjectAndRepo(string name = "Milestone Test Project")
    {
        var projectResp = await _client.PostAsJsonAsync("/api/projects",
            new CreateProjectRequest(name, "desc"));
        var project = await projectResp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);

        var repoResp = await _client.PostAsJsonAsync($"/api/projects/{project!.Id}/repos",
            new AddProjectRepoRequest("ms-repo", "https://github.com/test/ms.git", "main", null, null));
        var repo = await repoResp.Content.ReadFromJsonAsync<ProjectRepoResponse>(TestJsonOptions.Default);

        return (project.Id, repo!.Id);
    }

    private async Task<MilestoneResponse> CreateMilestone(
        Ulid projectId, string title = "Payments", string? outcome = "Users can pay.")
    {
        var response = await _client.PostAsJsonAsync($"/api/projects/{projectId}/milestones",
            new CreateMilestoneRequest(title, outcome));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<MilestoneResponse>(TestJsonOptions.Default))!;
    }

    private async Task<TaskResponse> CreateTask(
        Ulid projectId, Ulid repoId, string title, Ulid? milestoneId = null)
    {
        var request = new CreateTaskRequest(repoId, title, "Description", "Medium",
            MilestoneId: milestoneId);
        var response = await _client.PostAsJsonAsync($"/api/projects/{projectId}/tasks", request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default))!;
    }

    [Fact]
    public async Task Create_ThenList_ReturnsMilestoneWithEmptyCounts()
    {
        var (projectId, _) = await SeedProjectAndRepo();
        var created = await CreateMilestone(projectId, "Billing", "Money moves.");

        created.Title.Should().Be("Billing");
        created.Outcome.Should().Be("Money moves.");
        created.Status.Should().Be("Planned");

        var list = await _client.GetFromJsonAsync<MilestoneListResponse>(
            $"/api/projects/{projectId}/milestones", TestJsonOptions.Default);

        list!.Milestones.Should().ContainSingle(m => m.Id == created.Id);
        list.Milestones[0].Counts.Total.Should().Be(0);
    }

    [Fact]
    public async Task List_TasksWithoutMilestone_LandInBacklogNotAMilestone()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        var milestone = await CreateMilestone(projectId);

        await CreateTask(projectId, repoId, "In milestone", milestone.Id);
        await CreateTask(projectId, repoId, "Unplanned work");

        var list = await _client.GetFromJsonAsync<MilestoneListResponse>(
            $"/api/projects/{projectId}/milestones", TestJsonOptions.Default);

        list!.Milestones.Single(m => m.Id == milestone.Id).Counts.Total.Should().Be(1);
        // The backlog is not a milestone row — it is whatever hasn't been planned into one.
        list.Backlog.Total.Should().Be(1);
    }

    [Fact]
    public async Task Delete_DetachesTasks_NeverDeletesThem()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        var milestone = await CreateMilestone(projectId);
        var task = await CreateTask(projectId, repoId, "Survives deletion", milestone.Id);

        var response = await _client.DeleteAsync($"/api/projects/{projectId}/milestones/{milestone.Id}");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var detail = await _client.GetFromJsonAsync<TaskResponse>(
            $"/api/tasks/{task.Id}", TestJsonOptions.Default);

        detail.Should().NotBeNull("deleting a planning artifact must never destroy the work under it");
        detail!.MilestoneId.Should().BeNull();

        var list = await _client.GetFromJsonAsync<MilestoneListResponse>(
            $"/api/projects/{projectId}/milestones", TestJsonOptions.Default);
        list!.Milestones.Should().NotContain(m => m.Id == milestone.Id);
        list.Backlog.Total.Should().Be(1);
    }

    [Fact]
    public async Task Patch_UpdatesTitleOutcomeAndPosition()
    {
        var (projectId, _) = await SeedProjectAndRepo();
        var milestone = await CreateMilestone(projectId);

        var response = await _client.PatchAsJsonAsync(
            $"/api/projects/{projectId}/milestones/{milestone.Id}",
            new UpdateMilestoneRequest("Renamed", "New outcome", null, 7));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await response.Content.ReadFromJsonAsync<MilestoneResponse>(TestJsonOptions.Default);

        updated!.Title.Should().Be("Renamed");
        updated.Outcome.Should().Be("New outcome");
        updated.Position.Should().Be(7);
    }

    [Fact]
    public async Task Patch_RejectsUnknownStatus()
    {
        var (projectId, _) = await SeedProjectAndRepo();
        var milestone = await CreateMilestone(projectId);

        var response = await _client.PatchAsJsonAsync(
            $"/api/projects/{projectId}/milestones/{milestone.Id}",
            new UpdateMilestoneRequest(null, null, "Shipped", null));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AssigningTaskToMilestoneFromAnotherProject_IsRejected()
    {
        var (projectA, repoA) = await SeedProjectAndRepo("Milestone Project A");
        var (projectB, _) = await SeedProjectAndRepo("Milestone Project B");

        var foreignMilestone = await CreateMilestone(projectB, "Foreign");
        var task = await CreateTask(projectA, repoA, "Task in A");

        var response = await _client.PatchAsJsonAsync($"/api/tasks/{task.Id}",
            new UpdateTaskRequest(null, null, null, null, null, null,
                MilestoneId: foreignMilestone.Id.ToString()));

        // Otherwise a sibling project's milestone silently absorbs the task and corrupts
        // both roadmaps.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task MilestoneCanSpanRepos_InAMultiRepoProject()
    {
        var (projectId, repoOne) = await SeedProjectAndRepo("Multi Repo Milestone Project");

        var secondRepoResp = await _client.PostAsJsonAsync($"/api/projects/{projectId}/repos",
            new AddProjectRepoRequest("ms-repo-two", "https://github.com/test/ms2.git", "main", null, null));
        var repoTwo = await secondRepoResp.Content.ReadFromJsonAsync<ProjectRepoResponse>(TestJsonOptions.Default);

        var milestone = await CreateMilestone(projectId, "Cross-repo work");
        await CreateTask(projectId, repoOne, "Frontend side", milestone.Id);
        await CreateTask(projectId, repoTwo!.Id, "Backend side", milestone.Id);

        var list = await _client.GetFromJsonAsync<MilestoneListResponse>(
            $"/api/projects/{projectId}/milestones", TestJsonOptions.Default);

        // Milestone carries no RepoId by design; the tasks carry their own. Asserted so it
        // isn't narrowed by accident later.
        list!.Milestones.Single(m => m.Id == milestone.Id).Counts.Total.Should().Be(2);
    }

    [Fact]
    public async Task Get_ForUnknownProject_Is404()
    {
        var response = await _client.GetAsync($"/api/projects/{Ulid.NewUlid()}/milestones");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Patch_ForMilestoneInAnotherProject_Is404()
    {
        var (projectA, _) = await SeedProjectAndRepo("Scoped Milestone A");
        var (projectB, _) = await SeedProjectAndRepo("Scoped Milestone B");
        var milestone = await CreateMilestone(projectB, "Owned by B");

        var response = await _client.PatchAsJsonAsync(
            $"/api/projects/{projectA}/milestones/{milestone.Id}",
            new UpdateMilestoneRequest("Hijacked", null, null, null));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ProjectDeletedHandler_CleansUpItsMilestones()
    {
        var (projectId, _) = await SeedProjectAndRepo("Milestone Cleanup Project");
        var milestone = await CreateMilestone(projectId);

        // The handler is invoked directly rather than through DELETE /api/projects: the event
        // bus is a channel drained by a background dispatcher, so asserting right after the
        // response would be a race, not a test.
        using var scope = factory.Services.CreateScope();
        // Several modules clean up after ProjectDeleted, so resolving the interface would give
        // whichever registered last. This test is about the Tasks one.
        var handler = scope.ServiceProvider
            .GetServices<IIntegrationEventHandler<ProjectDeleted>>()
            .OfType<Orkka.Modules.Tasks.Infrastructure.EventHandlers.TaskDataCleanupOnProjectDeleted>()
            .Single();

        await handler.HandleAsync(new ProjectDeleted(projectId, TestCurrentUser.TestOrgId), default);

        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var remaining = await db.Milestones.IgnoreQueryFilters()
            .AnyAsync(m => m.Id == milestone.Id);

        remaining.Should().BeFalse();
    }
}
