namespace Orkka.IntegrationTests;

using System.Net.Http.Json;
using Orkka.IntegrationTests.Fixtures;
using Orkka.IntegrationTests.Helpers;
using Orkka.Modules.Projects.Features.ProjectRepos;
using Orkka.Modules.Projects.Features.Projects;
using Orkka.Modules.Projects.Infrastructure;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Exercises the PM-agent chat task creation path (SendMessageEndpoint → ExtractTasksAsync →
/// ITasksApi.CreateChatTasksAsync) which reserves numbers and inserts in one transaction.
/// This path had no coverage: in production it reserved numbers but persisted zero tasks.
/// </summary>
[Collection("Integration")]
public sealed class ChatTaskCreationTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task CreateChatTasks_PersistsAllTasks()
    {
        var projectResp = await _client.PostAsJsonAsync("/api/projects",
            new CreateProjectRequest("Chat Task Project", "desc"));
        var project = await projectResp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);

        var repoResp = await _client.PostAsJsonAsync($"/api/projects/{project!.Id}/repos",
            new AddProjectRepoRequest("chat-repo", "https://github.com/test/chat-repo.git", "main", null, null));
        var repo = await repoResp.Content.ReadFromJsonAsync<ProjectRepoResponse>(TestJsonOptions.Default);

        using var scope = factory.Services.CreateScope();
        var projectsDb = scope.ServiceProvider.GetRequiredService<ProjectsDbContext>();
        var orgId = (await projectsDb.Projects.IgnoreQueryFilters().FirstAsync(p => p.Id == project.Id)).OrgId;

        var specs = Enumerable.Range(1, 8)
            .Select(i => new ChatTaskSpec(
                $"Chat task {i}", $"Description {i}", "High", "Feature",
                repo!.Id, ["pm-generated"], i % 2 == 0 ? ["src/app.ts"] : null))
            .ToList();

        var tasksApi = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var created = await tasksApi.CreateChatTasksAsync(
            orgId, project.Id, Ulid.NewUlid(), "user_test", "en", specs);

        created.Should().HaveCount(8);

        var tasksDb = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var persisted = await tasksDb.Tasks.IgnoreQueryFilters()
            .Where(t => t.ProjectId == project.Id)
            .OrderBy(t => t.Number)
            .ToListAsync();

        persisted.Should().HaveCount(8);
        persisted[0].CurrentLane.ToString().Should().Be("Drafting");
        // SourceMessageId is how the orchestrator knows to skip the redundant PM review
        // (the PM authored these in chat) while still running the dev planning phase —
        // so SkipPlanning must stay false
        persisted.Should().OnlyContain(t => t.SourceMessageId != null);
        persisted.Should().OnlyContain(t => !t.SkipPlanning);
    }
}
