namespace Orkka.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Orkka.IntegrationTests.Fixtures;
using Orkka.IntegrationTests.Helpers;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Agents.Features.Agents;
using Orkka.Modules.Projects.Features.ProjectRepos;
using Orkka.Modules.Projects.Features.Projects;
using Orkka.Modules.Runners.Domain;
using Orkka.Modules.Runners.Infrastructure;
using Orkka.Modules.Tasks.Features.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The per-task model override picked on the create form. The endpoint validates against the
/// admin-managed model catalog (seeded from the shipped set on first boot) so a typo never
/// reaches the orchestrator — where an unknown id would be routed by prefix and fail mid-run —
/// while locally-pulled Ollama tags, which the catalog can't enumerate, stay selectable —
/// selectable, and run like every task on the user's own runner, which is the only place an
/// Ollama tag can run.
/// </summary>
[Collection("Integration")]
public sealed class TaskModelOverrideTests(OrkkaApiFactory factory) : IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    // The Ollama cases seed runner rows for the suite's shared user; clean on both sides so
    // the "no runner" case and every other class see the same starting state.
    public Task InitializeAsync() => ClearRunnerRowsAsync();

    public Task DisposeAsync() => ClearRunnerRowsAsync();

    private async Task ClearRunnerRowsAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RunnersDbContext>();
        var orgId = TestCurrentUser.TestOrgId;
        var userId = TestCurrentUser.TestUserId;
        await db.RunnerProviderSnapshots.IgnoreQueryFilters()
            .Where(x => x.OrgId == orgId && x.UserId == userId).ExecuteDeleteAsync();
        await db.RunnerPresences.IgnoreQueryFilters()
            .Where(x => x.OrgId == orgId && x.UserId == userId).ExecuteDeleteAsync();
        await db.RunnerTokens.IgnoreQueryFilters()
            .Where(x => x.OrgId == orgId && x.UserId == userId).ExecuteDeleteAsync();
    }

    /// <summary>An attached runner whose last Hello reported the given Ollama tags pulled.</summary>
    private async Task SeedRunnerWithOllamaAsync(params string[] pulledTags)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RunnersDbContext>();
        db.RunnerTokens.Add(new RunnerToken
        {
            Id = Ulid.NewUlid(),
            OrgId = TestCurrentUser.TestOrgId,
            UserId = TestCurrentUser.TestUserId,
            Name = "laptop",
            TokenHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
        });
        db.RunnerProviderSnapshots.Add(new RunnerProviderSnapshot
        {
            Id = Ulid.NewUlid(),
            OrgId = TestCurrentUser.TestOrgId,
            UserId = TestCurrentUser.TestUserId,
            MachineName = "ollama-box",
            ProvidersJson = JsonSerializer.Serialize(new RunnerProviderStatus[]
            {
                new(RunnerProviders.Ollama, CliFound: true, Authenticated: true, Models: pulledTags),
            }),
            CapturedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        });
        await db.SaveChangesAsync();
    }

    private async Task<(Ulid ProjectId, Ulid RepoId)> SeedProjectAndRepo()
    {
        var projectResp = await _client.PostAsJsonAsync("/api/projects",
            new CreateProjectRequest("Model Override Project", "desc"));
        var project = await projectResp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);

        var repoResp = await _client.PostAsJsonAsync($"/api/projects/{project!.Id}/repos",
            new AddProjectRepoRequest("app-repo", "https://github.com/test/app.git", "main", null, null));
        var repo = await repoResp.Content.ReadFromJsonAsync<ProjectRepoResponse>(TestJsonOptions.Default);

        return (project.Id, repo!.Id);
    }

    // The default pod is seeded asynchronously on the project-created event — poll
    // briefly so tests that need a seeded agent don't race the handler.
    private async Task<List<AgentResponse>> WaitForSeededAgents(Ulid projectId)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var agents = await _client.GetFromJsonAsync<List<AgentResponse>>(
                $"/api/projects/{projectId}/agents", TestJsonOptions.Default);
            if (agents is { Count: > 0 }) return agents;
            await Task.Delay(100);
        }
        throw new TimeoutException($"Agents were never seeded for project {projectId}.");
    }

    [Fact]
    public async Task CreateTask_WithCatalogModel_PersistsOverride()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        var dev = (await WaitForSeededAgents(projectId)).First(a => a.Role == "FullStackDev");

        var resp = await _client.PostAsJsonAsync($"/api/projects/{projectId}/tasks",
            new CreateTaskRequest(repoId, "Refactor the scheduler", "Big one",
                AssignedAgentId: dev.Id, Model: "claude-opus-5"));

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var task = await resp.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default);
        task!.Model.Should().Be("claude-opus-5");
        task.AssignedAgentId.Should().Be(dev.Id);
    }

    [Fact]
    public async Task CreateTask_WithoutModel_LeavesOverrideNull()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();

        var resp = await _client.PostAsJsonAsync($"/api/projects/{projectId}/tasks",
            new CreateTaskRequest(repoId, "Fix typo", "Small one"));

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        (await resp.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default))!
            .Model.Should().BeNull();
    }

    [Fact]
    public async Task CreateTask_WithPulledOllamaTag_IsAccepted_WhenTheRunnerHasIt()
    {
        await SeedRunnerWithOllamaAsync("ollama/qwen3-coder:30b");
        var (projectId, repoId) = await SeedProjectAndRepo();

        // Not a catalog anchor — the runner reports whatever the user actually pulled, and
        // that report is what makes the id both selectable and routable.
        var resp = await _client.PostAsJsonAsync($"/api/projects/{projectId}/tasks",
            new CreateTaskRequest(repoId, "Local run", "Desc", Model: "ollama/qwen3-coder:30b"));

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var task = (await resp.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default))!;
        task.Model.Should().Be("ollama/qwen3-coder:30b");
        task.RunnerUserId.Should().Be(TestCurrentUser.TestUserId);
    }

    [Fact]
    public async Task CreateTask_WithOllamaTag_ButNoRunnerYet_IsCreatedForTheirRunner()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();

        // Nothing is known about a runner that never connected: the task is created for the
        // creator's runner and waits for it, rather than being refused.
        var resp = await _client.PostAsJsonAsync($"/api/projects/{projectId}/tasks",
            new CreateTaskRequest(repoId, "Local run", "Desc", Model: "ollama/qwen3-coder:30b"));

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        (await resp.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default))!
            .RunnerUserId.Should().Be(TestCurrentUser.TestUserId);
    }

    [Fact]
    public async Task CreateTask_WithUnknownModel_ReturnsBadRequest()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();

        var resp = await _client.PostAsJsonAsync($"/api/projects/{projectId}/tasks",
            new CreateTaskRequest(repoId, "Task", "Desc", Model: "claude-opus-99"));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("not a selectable model");
    }

    [Fact]
    public async Task CreateTask_WithRetiredModel_ReturnsBadRequest()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();

        // Seeded disabled: still priced so historical usage bills correctly, but not offered.
        var resp = await _client.PostAsJsonAsync($"/api/projects/{projectId}/tasks",
            new CreateTaskRequest(repoId, "Task", "Desc", Model: "claude-sonnet-4-5"));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("not a selectable model");
    }
}
