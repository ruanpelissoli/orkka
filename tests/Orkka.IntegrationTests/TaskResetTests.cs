namespace Orkka.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using Orkka.IntegrationTests.Fixtures;
using Orkka.IntegrationTests.Helpers;
using Orkka.Modules.Projects.Features.ProjectRepos;
using Orkka.Modules.Projects.Features.Projects;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Features.TaskMessages;
using Orkka.Modules.Tasks.Features.Tasks;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.Modules.SourceControl.Contracts;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>
/// Reset-to-Drafting: the endpoint only flags the task (ResetRequestedAt, display status
/// Resetting); the worker sweep finalizes via FinalizeTaskResetAsync. These tests exercise
/// the flag's claim/resume exclusions and the finalize wipe against the real database
/// (ExecuteUpdate/ExecuteDelete + transaction paths).
/// </summary>
[Collection("Integration")]
public sealed class TaskResetTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<TaskResponse> SeedTaskAsync()
    {
        var projectResp = await _client.PostAsJsonAsync("/api/projects",
            new CreateProjectRequest("Reset Test Project", "desc"));
        var project = await projectResp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);

        var repoResp = await _client.PostAsJsonAsync($"/api/projects/{project!.Id}/repos",
            new AddProjectRepoRequest("reset-repo", "https://github.com/test/reset.git", "main", null, null));
        var repo = await repoResp.Content.ReadFromJsonAsync<ProjectRepoResponse>(TestJsonOptions.Default);

        var taskResp = await _client.PostAsJsonAsync($"/api/projects/{project.Id}/tasks",
            new CreateTaskRequest(repo!.Id, "Reset task", "desc", "Medium"));
        return (await taskResp.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default))!;
    }

    private IServiceScope CreateScope() => factory.Services.CreateScope();

    private static Task<int> MoveTo(TasksDbContext db, Ulid taskId, Lane lane, TaskStatus status)
        => db.Tasks.Where(t => t.Id == taskId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.CurrentLane, lane)
                .SetProperty(t => t.Status, status));

    [Fact]
    public async Task ResetTask_FlagsTaskAsResetting()
    {
        var task = await SeedTaskAsync();
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        await MoveTo(db, task.Id, Lane.Developing, TaskStatus.Active);

        var response = await _client.PostAsync($"/api/tasks/{task.Id}/reset", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default);
        body!.Status.Should().Be("Resetting");

        var entity = await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == task.Id);
        entity.ResetRequestedAt.Should().NotBeNull("the flag is what the worker sweep polls for");
    }

    [Fact]
    public async Task ResetTask_Drafting_ReturnsBadRequest()
    {
        var task = await SeedTaskAsync();

        var response = await _client.PostAsync($"/api/tasks/{task.Id}/reset", null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ResetTask_Merged_ReturnsBadRequest()
    {
        var task = await SeedTaskAsync();
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        await MoveTo(db, task.Id, Lane.Done, TaskStatus.Completed);
        await db.Tasks.Where(t => t.Id == task.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.IsMerged, true));

        var response = await _client.PostAsync($"/api/tasks/{task.Id}/reset", null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ResetTask_SecondRequest_IsIdempotent()
    {
        var task = await SeedTaskAsync();
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        await MoveTo(db, task.Id, Lane.Planning, TaskStatus.InQueue);

        (await _client.PostAsync($"/api/tasks/{task.Id}/reset", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await _client.PostAsync($"/api/tasks/{task.Id}/reset", null))
            .StatusCode.Should().Be(HttpStatusCode.OK, "a double-click must not error");
    }

    [Fact]
    public async Task ResetPendingTask_IsExcludedFromClaimQueueAndResume()
    {
        var task = await SeedTaskAsync();
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();

        await MoveTo(db, task.Id, Lane.Planning, TaskStatus.InQueue);
        (await _client.PostAsync($"/api/tasks/{task.Id}/reset", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await api.ListResetPendingTasksAsync())
            .Should().Contain(t => t.Id == task.Id);
        (await api.ListQueuedTasksAsync())
            .Should().NotContain(t => t.Id == task.Id, "a reset-pending task must not be picked up");
        (await api.TryClaimTaskAsync(task.Id, "worker-a", TimeSpan.FromMinutes(5)))
            .Should().BeFalse("the claim must refuse reset-pending tasks even in InQueue");

        // Even mid-run shapes (Active + HasAgentWorking, expired lease) must not resume.
        await db.Tasks.Where(t => t.Id == task.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, TaskStatus.Active)
                .SetProperty(t => t.HasAgentWorking, true));
        (await api.ListResumableTasksAsync([Lane.Planning]))
            .Should().NotContain(t => t.Id == task.Id);
    }

    [Fact]
    public async Task SendMessage_WhileResetPending_IsRejected()
    {
        var task = await SeedTaskAsync();
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        await MoveTo(db, task.Id, Lane.Developing, TaskStatus.InQueue);
        await _client.PostAsync($"/api/tasks/{task.Id}/reset", null);

        var response = await _client.PostAsJsonAsync($"/api/tasks/{task.Id}/messages",
            new SendMessageRequest("this would be wiped"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task FinalizeTaskReset_WipesDataAndReturnsTaskToDrafting()
    {
        var task = await SeedTaskAsync();
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var orgId = (await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == task.Id)).OrgId;

        // A task deep in the pipeline: branch, PR, lease, counters, chat, plan, live run.
        await db.Tasks.Where(t => t.Id == task.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.CurrentLane, Lane.Developing)
                .SetProperty(t => t.Status, TaskStatus.Active)
                .SetProperty(t => t.HasAgentWorking, true)
                .SetProperty(t => t.BranchName, "orkka/RST-1")
                .SetProperty(t => t.PrUrl, "https://github.com/test/reset/pull/1")
                .SetProperty(t => t.LeaseOwner, "worker-a")
                .SetProperty(t => t.LeaseExpiresAt, DateTimeOffset.UtcNow.AddMinutes(5))
                .SetProperty(t => t.ReviewCycleCount, 2)
                .SetProperty(t => t.QaCycleCount, 1)
                .SetProperty(t => t.StartedAt, DateTimeOffset.UtcNow)
                .SetProperty(t => t.TotalTokensUsed, 1234));
        db.TaskMessages.Add(new TaskMessage
        {
            OrgId = orgId, TaskId = task.Id, Role = "agent", Content = "working on it"
        });
        db.TaskPlans.Add(new TaskPlan
        {
            OrgId = orgId, TaskId = task.Id, Version = 1, Status = PlanStatus.Approved,
            Content = new PlanContent { Summary = "s", Approach = "a" }
        });
        db.TaskExecutions.Add(new TaskExecution
        {
            Id = Ulid.NewUlid(), OrgId = orgId, TaskId = task.Id, Lane = Lane.Developing,
            Status = ExecutionStatus.Running, StartedAt = DateTimeOffset.UtcNow
        });
        db.TaskExecutions.Add(new TaskExecution
        {
            Id = Ulid.NewUlid(), OrgId = orgId, TaskId = task.Id, Lane = Lane.Planning,
            Status = ExecutionStatus.Completed, TokensUsed = 500
        });
        await db.SaveChangesAsync();

        (await _client.PostAsync($"/api/tasks/{task.Id}/reset", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await api.FinalizeTaskResetAsync(task.Id)).Should().BeTrue();

        var entity = await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == task.Id);
        entity.CurrentLane.Should().Be(Lane.Drafting);
        entity.Status.Should().Be(TaskStatus.Draft);
        entity.HasAgentWorking.Should().BeFalse();
        entity.BranchName.Should().BeNull();
        entity.PrUrl.Should().BeNull();
        entity.LeaseOwner.Should().BeNull();
        entity.LeaseExpiresAt.Should().BeNull();
        entity.ReviewCycleCount.Should().Be(0);
        entity.QaCycleCount.Should().Be(0);
        entity.StartedAt.Should().BeNull();
        entity.ResetRequestedAt.Should().BeNull();
        entity.ResetCount.Should().Be(1, "the -r{n} branch suffix depends on this");
        entity.TotalTokensUsed.Should().Be(1234, "token accounting is audit data — never wiped");

        (await db.TaskMessages.AsNoTracking().CountAsync(m => m.TaskId == task.Id))
            .Should().Be(0, "the conversation must start fresh");
        (await db.TaskPlans.AsNoTracking().CountAsync(p => p.TaskId == task.Id))
            .Should().Be(0, "old plans must not survive a reset");

        var executions = await db.TaskExecutions.AsNoTracking()
            .Where(e => e.TaskId == task.Id).ToListAsync();
        executions.Should().HaveCount(2, "execution history is audit data — never wiped");
        executions.Single(e => e.Lane == Lane.Developing).Status
            .Should().Be(ExecutionStatus.Cancelled, "the in-flight run closes as Cancelled");
        executions.Single(e => e.Lane == Lane.Planning).Status
            .Should().Be(ExecutionStatus.Completed);
    }

    /// <summary>A reset Story member keeps the Story's shared branch — it re-plans on top of it,
    /// its earlier commits included — and drops its review base, which is recorded again (after
    /// those commits) when its next worktree is made.</summary>
    [Fact]
    public async Task FinalizeTaskReset_StoryMember_KeepsTheSharedBranch_AndClearsItsReviewBase()
    {
        var task = await SeedTaskAsync();
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();

        await db.Tasks.Where(t => t.Id == task.Id).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.StoryOrder, 2)
            .SetProperty(t => t.BranchName, "feature/SHOP-10-checkout")
            .SetProperty(t => t.ReviewBaseSha, "0123456789abcdef0123456789abcdef01234567"));
        await MoveTo(db, task.Id, Lane.Developing, TaskStatus.Active);
        (await _client.PostAsync($"/api/tasks/{task.Id}/reset", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await api.FinalizeTaskResetAsync(task.Id)).Should().BeTrue();

        var entity = await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == task.Id);
        entity.BranchName.Should().Be("feature/SHOP-10-checkout", "the story's branch is shared — a reset never leaves it");
        entity.ReviewBaseSha.Should().BeNull();
        entity.CurrentLane.Should().Be(Lane.Drafting);
    }

    [Fact]
    public void StoryResetNote_ShowsThePreviousAttemptsCommitRange()
    {
        var note = Orkka.Modules.Execution.Orchestration.TaskQueueProcessor.StoryResetNote(
            "feature/SHOP-10-checkout", ("0123456789abcdef", "fedcba9876543210"));

        note.Should().Contain("`0123456789..fedcba9876`").And.Contain("never rewrites")
            .And.Contain("re-plans on top of the branch");
        Orkka.Modules.Execution.Orchestration.TaskQueueProcessor.StoryResetNote("b", null)
            .Should().Contain("stays on `b`");
    }

    [Fact]
    public async Task FinalizeTaskReset_SecondFinalizer_LosesTheRace()
    {
        var task = await SeedTaskAsync();
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();

        await MoveTo(db, task.Id, Lane.Planning, TaskStatus.InQueue);
        await _client.PostAsync($"/api/tasks/{task.Id}/reset", null);

        (await api.FinalizeTaskResetAsync(task.Id)).Should().BeTrue();
        (await api.FinalizeTaskResetAsync(task.Id))
            .Should().BeFalse("finalize is single-winner across worker replicas");
    }

    // ── The worker's reset sweep, end to end against the real ITasksApi ───────────────
    //
    // FAN-9: a deploy rolled the Worker replica mid-review; the replacement (same pod name,
    // so the lease reads as its own, and an empty in-process run table) swept the reset and
    // removed the worktree on the user's runner while the agent the dead replica started
    // was still reading it. The sweep must now stop the runner-side run first.

    [Fact]
    public async Task ResetSweep_UserRunnerTask_StopsTheRunnerRunBeforeRemovingTheWorktree()
    {
        var task = await SeedTaskAsync();
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var entity = await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == task.Id);

        await db.Tasks.Where(t => t.Id == task.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.CurrentLane, Lane.CodeReview)
                .SetProperty(t => t.Status, TaskStatus.Active)
                .SetProperty(t => t.BranchName, "orkka/FAN-9")
                .SetProperty(t => t.RunnerUserId, "user_runner_owner")
                // The dead replica's lease, under the name its replacement also carries.
                .SetProperty(t => t.LeaseOwner, Environment.MachineName)
                .SetProperty(t => t.LeaseExpiresAt, DateTimeOffset.UtcNow.AddMinutes(4)));
        (await _client.PostAsync($"/api/tasks/{task.Id}/reset", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var route = new RecordingRoute();
        await BuildSweep(route).ProcessResetRequestsAsync(CancellationToken.None);

        route.Calls.Should().Equal(
            $"cancel-runs:{task.Id}", $"cleanup-worktree:{entity.RepoId}:{task.Id}");
        var after = await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == task.Id);
        after.ResetRequestedAt.Should().BeNull("the sweep finalized the reset");
        after.CurrentLane.Should().Be(Lane.Drafting);
        after.Status.Should().Be(TaskStatus.Draft);
    }

    [Fact]
    public async Task ResetSweep_RunnerOffline_KeepsTheTaskResetPendingForRetry()
    {
        var task = await SeedTaskAsync();
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();

        await db.Tasks.Where(t => t.Id == task.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.CurrentLane, Lane.CodeReview)
                .SetProperty(t => t.Status, TaskStatus.Active)
                .SetProperty(t => t.BranchName, "orkka/FAN-9")
                .SetProperty(t => t.RunnerUserId, "user_runner_owner"));
        await _client.PostAsync($"/api/tasks/{task.Id}/reset", null);

        var route = new RecordingRoute { RunnerOffline = true };
        await BuildSweep(route).ProcessResetRequestsAsync(CancellationToken.None);

        route.Calls.Should().Equal([$"cancel-runs:{task.Id}"],
            "the worktree must not be touched while the run's fate is unknown");
        var after = await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == task.Id);
        after.ResetRequestedAt.Should().NotBeNull("the sweep retries on the next poll");
        after.CurrentLane.Should().Be(Lane.CodeReview);
    }

    private Orkka.Modules.Execution.Orchestration.TaskQueueProcessor BuildSweep(RecordingRoute route) => new(
        factory.Services.GetRequiredService<IServiceScopeFactory>(),
        new NoOrchestrator(),
        new NoDocumentationSync(),
        route,
        new MockTaskNotifier(),
        factory.Services.GetRequiredService<Orkka.Modules.Agents.Contracts.IAgentAuditLog>(),
        new AnywhereAffinity(),
        new Orkka.Modules.Execution.Orchestration.WorkerDrain(),
        factory.Services.GetRequiredService<IConfiguration>(),
        Microsoft.Extensions.Options.Options.Create(
            new Orkka.Modules.Execution.Orchestration.DocumentationSyncOptions()),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<
            Orkka.Modules.Execution.Orchestration.TaskQueueProcessor>.Instance);

    /// <summary>Routes every task to one runner route whose invoker and git record
    /// their calls in order — the sweep's contract is that order.</summary>
    private sealed class RecordingRoute :
        Orkka.Modules.Execution.Orchestration.IExecutionRouter,
        Orkka.Modules.Execution.Agents.IAiAgentInvoker,
        Orkka.Modules.Agents.Contracts.IProviderReadiness
    {
        public List<string> Calls { get; } = [];
        public bool RunnerOffline { get; init; }

        public Orkka.Modules.Execution.Orchestration.ExecutionRoute Resolve(TaskSnapshot task) => new(
            this, RecordingGit.Create(Calls), this,
            Repo: new NullGitRepoReader());

        public Task CancelTaskRunsAsync(Ulid taskId, CancellationToken ct = default)
        {
            Calls.Add($"cancel-runs:{taskId}");
            if (RunnerOffline)
                // The proxy's RunnerUnavailableException (Orkka.Runners.Server, not referenced here);
                // the sweep treats any failure on this path the same way.
                throw new InvalidOperationException("Your runner is not connected.");
            return Task.CompletedTask;
        }

        public IAsyncEnumerable<Orkka.Modules.Execution.Agents.AgentChunk> InvokeAsync(
            Orkka.Modules.Execution.Agents.AgentRequest request, CancellationToken ct = default)
            => throw new NotSupportedException("the reset sweep never starts a run");

        public string? GetBlocker(Orkka.Modules.Agents.Contracts.AiProvider provider) => null;
        public IReadOnlyList<Orkka.Modules.Agents.Contracts.AiProvider> Connected => [];
        public string? GetAnyProviderBlocker() => null;
    }

    /// <summary>The route's git: a DispatchProxy over the (large) IGitWorkspaceManager that
    /// records CleanupWorktreeAsync and answers everything else with a completed default.</summary>
    // Not sealed: DispatchProxy derives from the proxy type at runtime.
    public class RecordingGit : System.Reflection.DispatchProxy
    {
        private List<string> _calls = [];

        public static IGitWorkspaceManager Create(List<string> calls)
        {
            var proxy = Create<IGitWorkspaceManager, RecordingGit>();
            ((RecordingGit)(object)proxy)._calls = calls;
            return proxy;
        }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IGitWorkspaceManager.CleanupWorktreeAsync))
                _calls.Add($"cleanup-worktree:{args![1]}:{args[2]}");

            var returnType = targetMethod.ReturnType;
            if (returnType == typeof(Task))
                return Task.CompletedTask;
            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var inner = returnType.GetGenericArguments()[0];
                var value = inner.IsValueType ? Activator.CreateInstance(inner) : null;
                return typeof(Task).GetMethod(nameof(Task.FromResult))!
                    .MakeGenericMethod(inner).Invoke(null, [value]);
            }
            return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
        }
    }

    private sealed class NoOrchestrator : Orkka.Modules.Execution.Orchestration.ITaskOrchestrator
    {
        public Task ExecuteAsync(Ulid taskId, CancellationToken ct = default) => Task.CompletedTask;
        public Task ResolveConflictsAndMergeAsync(Ulid taskId, CancellationToken ct = default) => Task.CompletedTask;
        public Task RunInteractiveSessionAsync(Ulid taskId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoDocumentationSync : Orkka.Modules.Execution.Orchestration.IDocumentationSyncService
    {
        public Task RunAsync(DocSyncCandidateTask task, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class AnywhereAffinity : Orkka.Modules.Runners.Contracts.IRunnerClaimAffinity
    {
        public bool CanExecuteHere(Ulid orgId, string userId) => true;
        public bool SupportsCapability(Ulid orgId, string userId, string capability) => true;
    }
}
