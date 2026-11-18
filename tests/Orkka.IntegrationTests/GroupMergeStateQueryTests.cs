namespace Orkka.IntegrationTests;

using System.Net.Http.Json;
using Orkka.IntegrationTests.Fixtures;
using Orkka.IntegrationTests.Helpers;
using Orkka.Modules.Projects.Features.ProjectRepos;
using Orkka.Modules.Projects.Features.Projects;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Features.Tasks;
using Orkka.Modules.Tasks.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The merge sweep's first question on every poll cycle is "which cross-repo groups do these
/// merge candidates belong to". Against the real database, asking it as <c>ids.Contains(t.GroupId)</c>
/// threw at translation — EF could not build a Ulid[] parameter against a nullable Ulid
/// column — the moment the first grouped task reached the sweep (prod, 2026-09-15). The sweep
/// runs before queue pickup, so that one throw starved every project's Ready lane. This runs
/// the query with a real group so the translation is proven, not assumed.
/// </summary>
[Collection("Integration")]
public sealed class GroupMergeStateQueryTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task GroupMergeStates_WithARealGroup_TranslatesAndAnswers()
    {
        var projectResp = await _client.PostAsJsonAsync("/api/projects",
            new CreateProjectRequest("Group Merge State Project", "desc"));
        var project = await projectResp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);

        var repoResp = await _client.PostAsJsonAsync($"/api/projects/{project!.Id}/repos",
            new AddProjectRepoRequest("api", "https://github.com/test/api.git", "main", null, null));
        var repo = await repoResp.Content.ReadFromJsonAsync<ProjectRepoResponse>(TestJsonOptions.Default);

        var taskIds = new List<Ulid>();
        foreach (var title in new[] { "API half", "UI half" })
        {
            var taskResp = await _client.PostAsJsonAsync($"/api/projects/{project.Id}/tasks",
                new CreateTaskRequest(repo!.Id, title, "desc", "Medium"));
            var task = await taskResp.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default);
            taskIds.Add(task!.Id);
        }

        var groupId = Ulid.NewUlid();
        using (var seed = factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<TasksDbContext>();
            var tasks = await db.Tasks.Where(t => taskIds.Contains(t.Id)).ToListAsync();
            db.TaskGroups.Add(new TaskGroup
            {
                Id = groupId, OrgId = tasks[0].OrgId, ProjectId = tasks[0].ProjectId,
                Title = "Cross-repo feature", MergePolicy = TaskGroupMergePolicy.Gated,
            });
            foreach (var t in tasks) t.GroupId = groupId;
            await db.SaveChangesAsync();
        }

        using var scope = factory.Services.CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();

        var states = await api.GetGroupMergeStatesAsync([groupId, Ulid.NewUlid()]);

        var state = states.Should().ContainSingle().Subject;
        state.GroupId.Should().Be(groupId);
        state.MergePolicy.Should().Be(TaskGroupMergePolicy.Gated);
        state.Ready.Should().BeFalse("neither member has a pull request yet");
    }
}
