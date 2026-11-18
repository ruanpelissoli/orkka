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
/// Tasks born from an accepted plan run on the accepting user's runner, like every other
/// creation path: the orchestrator resolves the runner from the task's own RunnerUserId, so a
/// task without one would sit waiting for a machine nobody owns.
/// </summary>
[Collection("Integration")]
public sealed class MilestonePlanRunnerOwnerTests(OrkkaApiFactory factory)
{
    [Fact]
    public async Task AcceptedTasks_RunOnTheAcceptingUsersRunner()
    {
        using var client = factory.CreateClient();

        var projectResp = await client.PostAsJsonAsync("/api/projects",
            new CreateProjectRequest("Runner Owner Plan Project", "desc"));
        var project = await projectResp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);

        var repoResp = await client.PostAsJsonAsync($"/api/projects/{project!.Id}/repos",
            new AddProjectRepoRequest("ro-plan-repo", "https://github.com/test/ro-plan.git", "main", null, null));
        var repo = await repoResp.Content.ReadFromJsonAsync<ProjectRepoResponse>(TestJsonOptions.Default);

        var accept = await client.PostAsJsonAsync($"/api/projects/{project.Id}/milestones/accept",
            new AcceptMilestonePlanRequest("Runner work", "Runs on my machine.",
                [new PlannedTaskResponse("Do it here", "## What\nLocally.", "High", "Feature")],
                repo!.Id));

        accept.IsSuccessStatusCode.Should().BeTrue();
        var milestone = await accept.Content.ReadFromJsonAsync<MilestoneResponse>(TestJsonOptions.Default);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var task = await db.Tasks.AsNoTracking()
            .IgnoreQueryFilters()
            .FirstAsync(t => t.MilestoneId == milestone!.Id);

        task.RunnerUserId.Should().Be(TestCurrentUser.TestUserId);
    }
}
