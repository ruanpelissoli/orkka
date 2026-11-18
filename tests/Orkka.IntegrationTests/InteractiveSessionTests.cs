namespace Orkka.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orkka.IntegrationTests.Fixtures;
using Orkka.IntegrationTests.Helpers;
using Orkka.Modules.Projects.Features.ProjectRepos;
using Orkka.Modules.Projects.Features.Projects;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Features.InteractiveSessions;
using Orkka.Modules.Tasks.Features.Tasks;
using Orkka.Modules.Tasks.Infrastructure;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>
/// Interactive development sessions (.claude/docs/INTERACTIVE.md) end to end: opening a session,
/// the watermark that decides which human messages a turn still owes a reply to, and finishing.
///
/// The watermark and session-close paths are ExecuteUpdate statements, which the in-memory
/// provider cannot run — they only have meaning against the real database, which is also the only
/// place their atomicity is real.
/// </summary>
[Collection("Integration")]
public sealed class InteractiveSessionTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private IServiceScope CreateScope() => factory.Services.CreateScope();

    private async Task<(Ulid ProjectId, Ulid RepoId)> SeedProjectAsync(string name)
    {
        // Assert the status before deserializing. A refusal here (402 plan_limit_projects, 403)
        // answers with a JSON error object, which happily deserializes into ProjectResponse with
        // a default Id — so the seed appeared to work and blew up one line later on a 404'd repo
        // URL, reporting "input does not contain any JSON tokens" and naming the wrong call.
        var projectResp = await _client.PostAsJsonAsync("/api/projects",
            new CreateProjectRequest(name, "desc"));
        projectResp.StatusCode.Should().Be(HttpStatusCode.Created,
            "seeding a project must not be silently refused: {0}",
            await projectResp.Content.ReadAsStringAsync());
        var project = await projectResp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);

        var repoResp = await _client.PostAsJsonAsync($"/api/projects/{project!.Id}/repos",
            new AddProjectRepoRequest(
                "session-repo", "https://github.com/test/session.git", "main", null, null));
        repoResp.StatusCode.Should().Be(HttpStatusCode.Created,
            "seeding a repo must not be silently refused: {0}",
            await repoResp.Content.ReadAsStringAsync());
        var repo = await repoResp.Content.ReadFromJsonAsync<ProjectRepoResponse>(TestJsonOptions.Default);

        return (project.Id, repo!.Id);
    }

    private async Task<TaskResponse> OpenSessionAsync(string projectName, string? title = null)
    {
        var (projectId, repoId) = await SeedProjectAsync(projectName);

        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/interactive-sessions",
            new StartInteractiveSessionRequest(repoId, title));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default))!;
    }

    /// <summary>Seeds a human message at a controlled instant. CreatedAt is applied after the
    /// insert because the DbContext stamps it on Added — set up front, every message would share
    /// the same "now" and no ordering assertion would mean anything.</summary>
    private async Task SeedUserMessageAsync(Ulid taskId, string content, DateTimeOffset createdAt)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();

        var task = await db.Tasks.FirstAsync(t => t.Id == taskId);
        var message = new TaskMessage
        {
            OrgId = task.OrgId,
            TaskId = taskId,
            Role = "user",
            Content = content,
            MessageType = MessageType.Answer,
        };
        db.TaskMessages.Add(message);
        await db.SaveChangesAsync();

        message.CreatedAt = createdAt;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task StartSession_CreatesLiveInteractiveTaskInDeveloping()
    {
        var task = await OpenSessionAsync("Interactive Start", "Fix the login redirect");

        task.Title.Should().Be("Fix the login redirect");
        task.IsInteractive.Should().BeTrue();
        task.InteractiveSessionStartedAt.Should().NotBeNull();
        task.CurrentLane.Should().Be(Lane.Developing.ToString());
        task.Status.Should().Be(TaskStatus.Active.ToString());
        // There is no plan and no PM refinement in a session — the human is the refinement.
        task.SkipPlanning.Should().BeTrue();
        task.StartedAt.Should().NotBeNull("work begins the moment a session opens");
    }

    [Fact]
    public async Task StartSession_WithoutTitle_GetsAPlaceholder()
    {
        var task = await OpenSessionAsync("Interactive Untitled");

        task.Title.Should().NotBeNullOrWhiteSpace(
            "a session must be openable before the human has named the bug");
    }

    [Fact]
    public async Task StartSession_IsNeverPickedUpByTheAutonomousQueue()
    {
        var task = await OpenSessionAsync("Interactive Not Queued");

        using var scope = CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();

        var queued = await api.ListQueuedTasksAsync();
        queued.Select(q => q.Id).Should().NotContain(task.Id);

        // It is, however, waiting for a worker to drive it.
        var sessions = await api.ListInteractiveSessionsAwaitingPickupAsync();
        sessions.Select(s => s.Id).Should().Contain(task.Id);
    }

    [Fact]
    public async Task Watermark_NeverMovesBackwards()
    {
        var task = await OpenSessionAsync("Interactive Watermark");

        using var scope = CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();

        var later = DateTimeOffset.UtcNow;
        await api.SetInteractiveAnsweredThroughAsync(task.Id, later);
        await api.SetInteractiveAnsweredThroughAsync(task.Id, later.AddMinutes(-5));

        var snapshot = await api.GetTaskSnapshotAsync(task.Id);
        snapshot!.InteractiveAnsweredThroughAt.Should().BeCloseTo(later, TimeSpan.FromMilliseconds(10),
            "a retried or out-of-order turn must not un-answer messages a later turn handled");
    }

    [Fact]
    public async Task Watermark_KeepsAFollowUpTypedWhileTheAgentWasWorking()
    {
        // The reason the watermark is message-based rather than "when did the last run finish":
        // this follow-up is OLDER than the turn that completes after it, so a completion-time
        // watermark would count it as answered and the human would never get a reply.
        var task = await OpenSessionAsync("Interactive Follow Up");
        var asked = DateTimeOffset.UtcNow.AddMinutes(-10);

        await SeedUserMessageAsync(task.Id, "do the thing", asked);
        await SeedUserMessageAsync(task.Id, "actually also this", asked.AddSeconds(30));

        using var scope = CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();

        // The turn consumed only the first message, so it stamps THAT message's timestamp.
        await api.SetInteractiveAnsweredThroughAsync(task.Id, asked);

        var snapshot = await api.GetTaskSnapshotAsync(task.Id);
        var pending = await api.ListUnansweredInteractiveMessagesAsync(
            task.Id, snapshot!.InteractiveAnsweredThroughAt);

        pending.Select(p => p.Content).Should().ContainSingle()
            .Which.Should().Be("actually also this");
    }

    [Fact]
    public async Task CloseSession_ClearsMarkersAndDropsOutOfPickup()
    {
        var task = await OpenSessionAsync("Interactive Close");

        using var scope = CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();

        await api.CloseInteractiveSessionAsync(task.Id);

        var snapshot = await api.GetTaskSnapshotAsync(task.Id);
        snapshot!.InteractiveSessionStartedAt.Should().BeNull();
        snapshot.InteractiveFinishRequestedAt.Should().BeNull();
        snapshot.HasAgentWorking.Should().BeFalse();

        var sessions = await api.ListInteractiveSessionsAwaitingPickupAsync();
        sessions.Select(s => s.Id).Should().NotContain(task.Id,
            "an ended session must never be re-claimed by a worker");
    }

    [Fact]
    public async Task Finish_FlagsTheSessionForItsWorker_AndIsIdempotent()
    {
        var task = await OpenSessionAsync("Interactive Finish");

        var first = await _client.PostAsync($"/api/tasks/{task.Id}/interactive-session/finish", null);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstBody = await first.Content
            .ReadFromJsonAsync<FinishInteractiveSessionResponse>(TestJsonOptions.Default);

        var second = await _client.PostAsync($"/api/tasks/{task.Id}/interactive-session/finish", null);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var secondBody = await second.Content
            .ReadFromJsonAsync<FinishInteractiveSessionResponse>(TestJsonOptions.Default);

        // Close, not equal: the first response carries the in-memory value at full .NET tick
        // precision while the second is read back from the database (UTC ticks, so it may differ only
        // in offset representation). A tolerance far below the round-trip time still discriminates — a second
        // write would land tens of milliseconds later.
        secondBody!.FinishRequestedAt.Should().BeCloseTo(
            firstBody!.FinishRequestedAt, TimeSpan.FromMilliseconds(1),
            "pressing Finish twice must not look like a second session ending");

        // The session stays live until its worker has actually committed and pushed — the API
        // process holds no worktree and cannot do that itself.
        using var scope = CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var snapshot = await api.GetTaskSnapshotAsync(task.Id);
        snapshot!.InteractiveFinishRequestedAt.Should().NotBeNull();
        snapshot.InteractiveSessionStartedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Finish_OnAnAlreadyEndedSession_Fails()
    {
        var task = await OpenSessionAsync("Interactive Finish Twice");

        using (var scope = CreateScope())
        {
            var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();
            await api.CloseInteractiveSessionAsync(task.Id);
        }

        var response = await _client.PostAsync($"/api/tasks/{task.Id}/interactive-session/finish", null);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Finish_OnAnOrdinaryTask_IsNotFound()
    {
        var (projectId, repoId) = await SeedProjectAsync("Interactive Wrong Task");
        var taskResp = await _client.PostAsJsonAsync($"/api/projects/{projectId}/tasks",
            new CreateTaskRequest(repoId, "An ordinary task", "desc", "Medium"));
        var task = await taskResp.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default);

        var response = await _client.PostAsync($"/api/tasks/{task!.Id}/interactive-session/finish", null);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Auto-titling ─────────────────────────────────────────────────────
    // The guard lives in the UPDATE's WHERE clause, which is the point — a user renaming a
    // session while its first turn is titling it must keep their words. Only the real database runs
    // ExecuteUpdate, so this is the only place the guard means anything.

    [Fact]
    public async Task GeneratedTitle_ReplacesThePlaceholder()
    {
        var task = await OpenSessionAsync("Interactive Autotitle");
        task.Title.Should().Be(InteractiveSessionDefaults.UntitledSession);

        using var scope = CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();

        var applied = await api.TrySetGeneratedSessionTitleAsync(task.Id, "Fix the login redirect");

        applied.Should().BeTrue();
        (await api.GetTaskSnapshotAsync(task.Id))!.Title.Should().Be("Fix the login redirect");
    }

    [Fact]
    public async Task GeneratedTitle_NeverOverwritesATitleTheUserChose()
    {
        var task = await OpenSessionAsync("Interactive Named", "Logo work, do not rename");

        using var scope = CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();

        var applied = await api.TrySetGeneratedSessionTitleAsync(task.Id, "Create the app logo");

        applied.Should().BeFalse();
        (await api.GetTaskSnapshotAsync(task.Id))!.Title.Should().Be("Logo work, do not rename");
    }

    [Fact]
    public async Task GeneratedTitle_AppliesOnlyOnce()
    {
        // The second turn must not re-title a session the first turn already named.
        var task = await OpenSessionAsync("Interactive Titled Once");

        using var scope = CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();

        (await api.TrySetGeneratedSessionTitleAsync(task.Id, "First title")).Should().BeTrue();
        (await api.TrySetGeneratedSessionTitleAsync(task.Id, "Second title")).Should().BeFalse();

        (await api.GetTaskSnapshotAsync(task.Id))!.Title.Should().Be("First title");
    }

    [Fact]
    public async Task GeneratedTitle_IsOnlyForInteractiveTasks()
    {
        var (projectId, repoId) = await SeedProjectAsync("Interactive Not A Session");
        var taskResp = await _client.PostAsJsonAsync($"/api/projects/{projectId}/tasks",
            new CreateTaskRequest(repoId, InteractiveSessionDefaults.UntitledSession, "desc", "Medium"));
        var task = await taskResp.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default);

        using var scope = CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();

        // An ordinary task that happens to share the placeholder name is still not a session.
        (await api.TrySetGeneratedSessionTitleAsync(task!.Id, "Something else")).Should().BeFalse();
    }
}
