namespace Orkka.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Orkka.IntegrationTests.Fixtures;
using Orkka.IntegrationTests.Helpers;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Projects.Features.ProjectRepos;
using Orkka.Modules.Projects.Features.Projects;
using Orkka.Modules.Runners.Domain;
using Orkka.Modules.Runners.Infrastructure;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Features.Tasks;
using Orkka.Modules.Tasks.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Every task runs on its creator's runner, on their own AI subscription. Nothing is ever
/// billed, and nothing is refused for a provider the runner lacks — the task is created, says
/// in its thread what to sign in to, and waits.
/// </summary>
[Collection("Integration")]
public sealed class RunnerCoverageOnCreateTests(OrkkaApiFactory factory) : IAsyncLifetime
{
    private const string Gpt = "gpt-5.6-sol";
    private const string Claude = "claude-sonnet-5";
    private const string Copilot = "copilot/claude-sonnet-5";
    private const string Machine = "dana-laptop";

    private static readonly RunnerProviderStatus[] ClaudeReadyCodexSignedOut =
    [
        new(RunnerProviders.Claude, CliFound: true, Authenticated: true),
        new(RunnerProviders.Codex, CliFound: true, Authenticated: false, Models: null, Detail: "codex is not logged in"),
    ];

    private readonly HttpClient _client = factory.CreateClient();

    public Task InitializeAsync() => ClearRunnerRowsAsync();

    public Task DisposeAsync() => ClearRunnerRowsAsync();

    private async Task ClearRunnerRowsAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RunnersDbContext>();
        var orgId = TestCurrentUser.TestOrgId;
        var userId = TestCurrentUser.TestUserId;
        await db.RunnerProviderSnapshots.IgnoreQueryFilters()
            .Where(s => s.OrgId == orgId && s.UserId == userId).ExecuteDeleteAsync();
        await db.RunnerTokens.IgnoreQueryFilters()
            .Where(t => t.OrgId == orgId && t.UserId == userId).ExecuteDeleteAsync();
    }

    private async Task SeedRunnerSnapshotAsync(RunnerProviderStatus[] providers)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RunnersDbContext>();
        db.RunnerProviderSnapshots.Add(new RunnerProviderSnapshot
        {
            Id = Ulid.NewUlid(),
            OrgId = TestCurrentUser.TestOrgId,
            UserId = TestCurrentUser.TestUserId,
            MachineName = Machine,
            ProvidersJson = JsonSerializer.Serialize(providers),
            CapturedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        });
        await db.SaveChangesAsync();
    }

    private async Task<(Ulid ProjectId, Ulid RepoId)> SeedProjectAndRepoAsync()
    {
        var projectResp = await _client.PostAsJsonAsync("/api/projects",
            new CreateProjectRequest("Runner Only Project", "desc"));
        var project = await projectResp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);

        var repoResp = await _client.PostAsJsonAsync($"/api/projects/{project!.Id}/repos",
            new AddProjectRepoRequest("ro-repo", "git@github.com:acme/ro.git", "main", null));
        var repo = await repoResp.Content.ReadFromJsonAsync<ProjectRepoResponse>(TestJsonOptions.Default);

        return (project.Id, repo!.Id);
    }

    private async Task<TaskResponse> CreateOkAsync(Ulid projectId, Ulid repoId, string? model)
    {
        var response = await _client.PostAsJsonAsync($"/api/projects/{projectId}/tasks",
            new CreateTaskRequest(repoId, $"Task on {model ?? "default"}", "Description", Model: model));
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default))!;
    }

    private async Task<List<string>> SystemEventsAsync(Ulid taskId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        return await db.TaskMessages.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.TaskId == taskId && m.MessageType == MessageType.SystemEvent)
            .Select(m => m.Content)
            .ToListAsync();
    }

    [Fact]
    public async Task ACoveredModel_RunsOnTheCreatorsRunner()
    {
        await SeedRunnerSnapshotAsync(ClaudeReadyCodexSignedOut);
        var (projectId, repoId) = await SeedProjectAndRepoAsync();

        var task = await CreateOkAsync(projectId, repoId, Claude);

        task.RunnerUserId.Should().Be(TestCurrentUser.TestUserId);
        (await SystemEventsAsync(task.Id)).Should().NotContain(e => e.Contains("isn't signed in"));
    }

    [Fact]
    public async Task AModelTheRunnerHasNotSignedIn_IsStillCreatedForTheRunner_NeverBilled()
    {
        await SeedRunnerSnapshotAsync(ClaudeReadyCodexSignedOut);
        var (projectId, repoId) = await SeedProjectAndRepoAsync();

        var task = await CreateOkAsync(projectId, repoId, Gpt);

        task.RunnerUserId.Should().Be(TestCurrentUser.TestUserId, "there is no cloud and no platform key");
        var events = await SystemEventsAsync(task.Id);
        events.Should().Contain(e => e.Contains(Gpt) && e.Contains("isn't signed in") && e.Contains(Machine));
        events.Should().NotContain(e => e.Contains("Billed"));
    }

    [Fact]
    public async Task ARunnerOnlyModel_IsNeverRefused_WhenTheRunnerLacksIt()
    {
        await SeedRunnerSnapshotAsync(ClaudeReadyCodexSignedOut);
        var (projectId, repoId) = await SeedProjectAndRepoAsync();

        var task = await CreateOkAsync(projectId, repoId, Copilot);

        task.RunnerUserId.Should().Be(TestCurrentUser.TestUserId);
        (await SystemEventsAsync(task.Id)).Should().Contain(e => e.Contains("isn't signed in"));
    }

    [Fact]
    public async Task ACreatorWhoseRunnerNeverConnected_StillGetsTheTask()
    {
        var (projectId, repoId) = await SeedProjectAndRepoAsync();

        var task = await CreateOkAsync(projectId, repoId, Gpt);

        task.RunnerUserId.Should().Be(TestCurrentUser.TestUserId);
        (await SystemEventsAsync(task.Id)).Should().BeEmpty("nothing is known about that runner yet");
    }
}
