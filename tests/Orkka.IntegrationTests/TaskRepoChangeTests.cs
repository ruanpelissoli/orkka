namespace Orkka.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using Orkka.IntegrationTests.Fixtures;
using Orkka.IntegrationTests.Helpers;
using Orkka.Modules.Projects.Features.ProjectRepos;
using Orkka.Modules.Projects.Features.Projects;
using Orkka.Modules.Tasks.Features.Tasks;
using Orkka.Modules.Tasks.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Moving a task between repos in a multi-repo project — the recovery path for a task filed
/// against the wrong one.
/// </summary>
[Collection("Integration")]
public sealed class TaskRepoChangeTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();


    private async Task<(Ulid ProjectId, Ulid ApiRepoId, Ulid MobileRepoId)> SeedTwoRepoProjectAsync(string name)
    {
        var projectResp = await _client.PostAsJsonAsync("/api/projects",
            new CreateProjectRequest(name, "desc"));
        var project = await projectResp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);

        var apiResp = await _client.PostAsJsonAsync($"/api/projects/{project!.Id}/repos",
            new AddProjectRepoRequest("tc-api", "https://github.com/t/tc-api.git", "main", null, null));
        var api = await apiResp.Content.ReadFromJsonAsync<ProjectRepoResponse>(TestJsonOptions.Default);

        var mobileResp = await _client.PostAsJsonAsync($"/api/projects/{project.Id}/repos",
            new AddProjectRepoRequest("tc-mobile", "https://github.com/t/tc-mobile.git", "main", null, null));
        var mobile = await mobileResp.Content.ReadFromJsonAsync<ProjectRepoResponse>(TestJsonOptions.Default);

        return (project.Id, api!.Id, mobile!.Id);
    }

    [Fact]
    public async Task PatchingOnlyTheRepo_MovesTheTask()
    {
        var projectResp = await _client.PostAsJsonAsync("/api/projects",
            new CreateProjectRequest("Repo Change Project", "desc"));
        var project = await projectResp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);

        var apiResp = await _client.PostAsJsonAsync($"/api/projects/{project!.Id}/repos",
            new AddProjectRepoRequest("rc-api", "https://github.com/t/rc-api.git", "main", null, null));
        var api = await apiResp.Content.ReadFromJsonAsync<ProjectRepoResponse>(TestJsonOptions.Default);

        var mobileResp = await _client.PostAsJsonAsync($"/api/projects/{project.Id}/repos",
            new AddProjectRepoRequest("rc-mobile", "https://github.com/t/rc-mobile.git", "main", null, null));
        var mobile = await mobileResp.Content.ReadFromJsonAsync<ProjectRepoResponse>(TestJsonOptions.Default);

        var createResp = await _client.PostAsJsonAsync($"/api/projects/{project.Id}/tasks",
            new CreateTaskRequest(mobile!.Id, "Filed against the wrong repo", "desc", "Medium"));
        var task = await createResp.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default);
        task!.RepoId.Should().Be(mobile.Id);

        // Exactly what the picker sends: repoId alone, every other field omitted.
        var patch = await _client.PatchAsync($"/api/tasks/{task.Id}",
            JsonContent.Create(new { repoId = api!.Id.ToString() }));

        patch.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "a partial PATCH carrying only repoId is what the UI picker sends");

        var updated = await patch.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default);
        updated!.RepoId.Should().Be(api.Id);

        var refetched = await _client.GetFromJsonAsync<TaskResponse>(
            $"/api/tasks/{task.Id}", TestJsonOptions.Default);
        refetched!.RepoId.Should().Be(api.Id, "the change must survive a refetch, not just echo back");
    }

    [Fact]
    public async Task ChangingTheRepo_ClearsTheOldRepoBranchAndPr()
    {
        var (projectId, apiRepoId, mobileRepoId) = await SeedTwoRepoProjectAsync("Repo Change Artifacts");

        var createResp = await _client.PostAsJsonAsync($"/api/projects/{projectId}/tasks",
            new CreateTaskRequest(mobileRepoId, "Went to the wrong repo", "desc", "Medium"));
        var task = await createResp.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default);

        // Simulate a run that already branched and opened a PR in the mobile repo.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
            var entity = await db.Tasks.FirstAsync(t => t.Id == task!.Id);
            entity.BranchName = "orkka/TAP-1";
            entity.PrUrl = "https://github.com/o/tapago-mobile/pull/3";
            entity.MergeFailureReason = "something about the mobile repo";
            await db.SaveChangesAsync();
        }

        var patch = await _client.PatchAsync($"/api/tasks/{task!.Id}",
            JsonContent.Create(new { repoId = apiRepoId.ToString() }));
        patch.StatusCode.Should().Be(HttpStatusCode.OK);

        using var verify = factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<TasksDbContext>();
        var moved = await verifyDb.Tasks.AsNoTracking().FirstAsync(t => t.Id == task.Id);

        moved.RepoId.Should().Be(apiRepoId);
        // PR creation is skipped while PrUrl is set, so carrying it over meant the task kept
        // reporting the OLD repo's pull request forever.
        moved.PrUrl.Should().BeNull();
        moved.BranchName.Should().BeNull("the branch exists in the old repo, not the new one");
        moved.MergeFailureReason.Should().BeNull();
    }

    [Fact]
    public async Task ChangingTheRepo_IsRefusedOnceTheWorkIsMerged()
    {
        var (projectId, apiRepoId, mobileRepoId) = await SeedTwoRepoProjectAsync("Repo Change Merged");

        var createResp = await _client.PostAsJsonAsync($"/api/projects/{projectId}/tasks",
            new CreateTaskRequest(mobileRepoId, "Already shipped", "desc", "Medium"));
        var task = await createResp.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
            var entity = await db.Tasks.FirstAsync(t => t.Id == task!.Id);
            entity.IsMerged = true;
            entity.PrUrl = "https://github.com/o/tapago-mobile/pull/4";
            await db.SaveChangesAsync();
        }

        var patch = await _client.PatchAsync($"/api/tasks/{task!.Id}",
            JsonContent.Create(new { repoId = apiRepoId.ToString() }));

        // The branch and PR are the record of where the work landed — moving the task would
        // orphan both.
        patch.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SettingTheSameRepo_LeavesArtifactsAlone()
    {
        var (projectId, _, mobileRepoId) = await SeedTwoRepoProjectAsync("Repo Change Noop");

        var createResp = await _client.PostAsJsonAsync($"/api/projects/{projectId}/tasks",
            new CreateTaskRequest(mobileRepoId, "Unchanged", "desc", "Medium"));
        var task = await createResp.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
            var entity = await db.Tasks.FirstAsync(t => t.Id == task!.Id);
            entity.BranchName = "orkka/keep-me";
            await db.SaveChangesAsync();
        }

        // Re-sending the current repo is a no-op, not a reason to throw away a live branch.
        var patch = await _client.PatchAsync($"/api/tasks/{task!.Id}",
            JsonContent.Create(new { repoId = mobileRepoId.ToString() }));
        patch.StatusCode.Should().Be(HttpStatusCode.OK);

        using var verify = factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<TasksDbContext>();
        var unchanged = await verifyDb.Tasks.AsNoTracking().FirstAsync(t => t.Id == task.Id);
        unchanged.BranchName.Should().Be("orkka/keep-me");
    }
}
