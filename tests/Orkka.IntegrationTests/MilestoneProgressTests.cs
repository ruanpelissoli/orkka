namespace Orkka.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using Orkka.IntegrationTests.Fixtures;
using Orkka.IntegrationTests.Helpers;
using Orkka.Modules.Projects.Features.ProjectRepos;
using Orkka.Modules.Projects.Features.Projects;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Features.Milestones;
using Orkka.Modules.Tasks.Features.Tasks;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.Modules.Tasks.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>
/// Milestone status is derived from the tasks under it, never accumulated — these cover the
/// transitions and the rollup that the roadmap renders.
/// </summary>
[Collection("Integration")]
public sealed class MilestoneProgressTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<(Ulid ProjectId, Ulid RepoId)> SeedProjectAndRepo(string name)
    {
        var projectResp = await _client.PostAsJsonAsync("/api/projects",
            new CreateProjectRequest(name, "desc"));
        var project = await projectResp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);

        var repoResp = await _client.PostAsJsonAsync($"/api/projects/{project!.Id}/repos",
            new AddProjectRepoRequest("prog-repo", "https://github.com/test/prog.git", "main", null, null));
        var repo = await repoResp.Content.ReadFromJsonAsync<ProjectRepoResponse>(TestJsonOptions.Default);

        return (project.Id, repo!.Id);
    }

    private async Task<MilestoneResponse> CreateMilestone(Ulid projectId, string title)
    {
        var response = await _client.PostAsJsonAsync($"/api/projects/{projectId}/milestones",
            new CreateMilestoneRequest(title, "An outcome."));
        return (await response.Content.ReadFromJsonAsync<MilestoneResponse>(TestJsonOptions.Default))!;
    }

    private async Task<TaskResponse> CreateTask(Ulid projectId, Ulid repoId, Ulid milestoneId, string title)
    {
        var response = await _client.PostAsJsonAsync($"/api/projects/{projectId}/tasks",
            new CreateTaskRequest(repoId, title, "Description", "Medium", MilestoneId: milestoneId));
        return (await response.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default))!;
    }

    private async Task<MilestoneResponse> GetMilestone(Ulid projectId, Ulid milestoneId)
    {
        var list = await _client.GetFromJsonAsync<MilestoneListResponse>(
            $"/api/projects/{projectId}/milestones", TestJsonOptions.Default);
        return list!.Milestones.Single(m => m.Id == milestoneId);
    }

    [Fact]
    public async Task EmptyMilestone_StaysPlanned_AndDoesNotAutoComplete()
    {
        var (projectId, _) = await SeedProjectAndRepo("Empty Milestone Project");
        var milestone = await CreateMilestone(projectId, "Nothing planned yet");

        using var scope = factory.Services.CreateScope();
        var updater = scope.ServiceProvider.GetRequiredService<MilestoneProgressUpdater>();
        await updater.RefreshAsync(TestCurrentUser.TestOrgId, projectId, milestone.Id, default);

        // "0 of 0 done" is not an achievement, and auto-completing would fire the grooming
        // prompt for a milestone nobody has planned.
        (await GetMilestone(projectId, milestone.Id)).Status.Should().Be("Planned");
    }

    [Fact]
    public async Task TaskStillDrafting_LeavesMilestonePlanned()
    {
        var (projectId, repoId) = await SeedProjectAndRepo("Drafting Milestone Project");
        var milestone = await CreateMilestone(projectId, "Not started");
        await CreateTask(projectId, repoId, milestone.Id, "Still a draft");

        using var scope = factory.Services.CreateScope();
        var updater = scope.ServiceProvider.GetRequiredService<MilestoneProgressUpdater>();
        await updater.RefreshAsync(TestCurrentUser.TestOrgId, projectId, milestone.Id, default);

        (await GetMilestone(projectId, milestone.Id)).Status.Should().Be("Planned");
    }

    [Fact]
    public async Task SubmittingATask_MovesTheMilestoneToActive()
    {
        var (projectId, repoId) = await SeedProjectAndRepo("Active Milestone Project");
        var milestone = await CreateMilestone(projectId, "Work begins");
        var task = await CreateTask(projectId, repoId, milestone.Id, "First real task");

        var submit = await _client.PostAsync($"/api/tasks/{task.Id}/submit", null);
        submit.StatusCode.Should().Be(HttpStatusCode.OK);

        // Leaving Drafting is what starts a milestone, and no integration event covers it —
        // the submit endpoint drives the transition directly.
        (await GetMilestone(projectId, milestone.Id)).Status.Should().Be("Active");
    }

    [Fact]
    public async Task AllTasksDone_CompletesTheMilestone()
    {
        var (projectId, repoId) = await SeedProjectAndRepo("Completing Milestone Project");
        var milestone = await CreateMilestone(projectId, "Finishes");
        var task = await CreateTask(projectId, repoId, milestone.Id, "The only task");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var entity = await db.Tasks.FirstAsync(t => t.Id == task.Id);
        entity.CurrentLane = Lane.Done;
        entity.Status = TaskStatus.Completed;
        await db.SaveChangesAsync();

        var updater = scope.ServiceProvider.GetRequiredService<MilestoneProgressUpdater>();
        await updater.RefreshAsync(TestCurrentUser.TestOrgId, projectId, milestone.Id, default);

        var refreshed = await GetMilestone(projectId, milestone.Id);
        refreshed.Status.Should().Be("Completed");
        refreshed.CompletedAt.Should().NotBeNull();
        refreshed.Counts.Done.Should().Be(1);
    }

    [Fact]
    public async Task CancelledMilestone_IsNeverResurrectedByALateMerge()
    {
        var (projectId, repoId) = await SeedProjectAndRepo("Cancelled Milestone Project");
        var milestone = await CreateMilestone(projectId, "Abandoned");
        var task = await CreateTask(projectId, repoId, milestone.Id, "Straggler");

        await _client.PatchAsJsonAsync($"/api/projects/{projectId}/milestones/{milestone.Id}",
            new UpdateMilestoneRequest(null, null, "Cancelled", null));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var entity = await db.Tasks.FirstAsync(t => t.Id == task.Id);
        entity.CurrentLane = Lane.Done;
        entity.Status = TaskStatus.Completed;
        await db.SaveChangesAsync();

        var updater = scope.ServiceProvider.GetRequiredService<MilestoneProgressUpdater>();
        await updater.RefreshAsync(TestCurrentUser.TestOrgId, projectId, milestone.Id, default);

        // Cancelled is the one status a human sets deliberately; the derivation must not
        // overwrite it.
        (await GetMilestone(projectId, milestone.Id)).Status.Should().Be("Cancelled");
    }

    [Fact]
    public async Task ArchivedTasks_StillCountInTheRollup()
    {
        // Archiving hides a card from the board; it does not un-finish the work. The two-week
        // auto-archive sweep eventually archives every Done task, so excluding archived tasks
        // here would empty each shipped milestone's rollup and the roadmap would report it as
        // never started. Lane/status breakdowns are covered in MilestoneArchivedCountsTests.
        var (projectId, repoId) = await SeedProjectAndRepo("Archived Rollup Project");
        var milestone = await CreateMilestone(projectId, "Counts");
        var keep = await CreateTask(projectId, repoId, milestone.Id, "Counts toward total");
        var drop = await CreateTask(projectId, repoId, milestone.Id, "Archived but still counted");

        var archive = await _client.PostAsync($"/api/tasks/{drop.Id}/archive", null);
        archive.IsSuccessStatusCode.Should().BeTrue();

        var refreshed = await GetMilestone(projectId, milestone.Id);
        refreshed.Counts.Total.Should().Be(2);
        keep.Id.Should().NotBe(drop.Id);
    }

    [Fact]
    public async Task DraftingTasks_CountAsNotStarted_NotInFlight()
    {
        var (projectId, repoId) = await SeedProjectAndRepo("Draft Counting Project");
        var milestone = await CreateMilestone(projectId, "Freshly planned");
        await CreateTask(projectId, repoId, milestone.Id, "Draft one");
        var submitted = await CreateTask(projectId, repoId, milestone.Id, "Draft two");

        var beforeSubmit = await GetMilestone(projectId, milestone.Id);
        // Six untouched drafts reading as "in flight" contradicted the same card's Planned
        // badge — the two are derived from the same rows and must agree.
        beforeSubmit.Counts.NotStarted.Should().Be(2);
        beforeSubmit.Counts.InFlight.Should().Be(0);
        beforeSubmit.Status.Should().Be("Planned");

        (await _client.PostAsync($"/api/tasks/{submitted.Id}/submit", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var afterSubmit = await GetMilestone(projectId, milestone.Id);
        afterSubmit.Counts.NotStarted.Should().Be(1);
        afterSubmit.Counts.InFlight.Should().Be(1);
        afterSubmit.Status.Should().Be("Active");

        // The four buckets always partition the total.
        (afterSubmit.Counts.Done + afterSubmit.Counts.InFlight
            + afterSubmit.Counts.NeedsAttention + afterSubmit.Counts.NotStarted)
            .Should().Be(afterSubmit.Counts.Total);
    }
}
