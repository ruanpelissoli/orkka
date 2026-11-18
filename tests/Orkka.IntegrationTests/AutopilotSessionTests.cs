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
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>
/// Session bookkeeping and candidate selection — the queries every autopilot stop condition
/// is computed from.
/// </summary>
[Collection("Integration")]
public sealed class AutopilotSessionTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private IServiceScope CreateScope() => factory.Services.CreateScope();

    private async Task<(Ulid ProjectId, Ulid RepoId)> SeedProjectAsync()
    {
        var projectResp = await _client.PostAsJsonAsync("/api/projects",
            new CreateProjectRequest("Autopilot Session Project", "desc"));
        var project = await projectResp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);

        var repoResp = await _client.PostAsJsonAsync($"/api/projects/{project!.Id}/repos",
            new AddProjectRepoRequest("ap-repo", "https://github.com/test/ap.git", "main", null, null));
        var repo = await repoResp.Content.ReadFromJsonAsync<ProjectRepoResponse>(TestJsonOptions.Default);

        return (project.Id, repo!.Id);
    }

    private async Task<Ulid> SeedTaskAsync(Ulid projectId, Ulid repoId, string title)
    {
        var resp = await _client.PostAsJsonAsync($"/api/projects/{projectId}/tasks",
            new CreateTaskRequest(repoId, title, "desc", "Medium"));
        var task = await resp.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default);
        return task!.Id;
    }

    private static async Task<AutopilotSession> OpenSessionAsync(
        TasksDbContext db, Ulid projectId, decimal? cap = null)
    {
        var orgId = await db.Tasks.AsNoTracking()
            .Where(t => t.ProjectId == projectId).Select(t => t.OrgId).FirstAsync();

        var session = new AutopilotSession
        {
            Id = Ulid.NewUlid(),
            OrgId = orgId,
            ProjectId = projectId,
            StartedByUserId = "user_test",
            StartedAt = DateTimeOffset.UtcNow,
            CapUsd = cap,
        };

        db.AutopilotSessions.Add(session);
        await db.SaveChangesAsync();
        return session;
    }

    [Fact]
    public async Task OnlyOneSessionPerProjectCanBeOpen()
    {
        var (projectId, repoId) = await SeedProjectAsync();
        await SeedTaskAsync(projectId, repoId, "seed");

        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();

        await OpenSessionAsync(db, projectId);

        // The filtered unique index is what makes "is autopilot running" a single fact rather
        // than something the toggle and the sweep can disagree about.
        var second = async () => await OpenSessionAsync(db, projectId);
        await second.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task StoppingASessionFreesTheProjectForANewOne()
    {
        var (projectId, repoId) = await SeedProjectAsync();
        await SeedTaskAsync(projectId, repoId, "seed");

        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();

        var first = await OpenSessionAsync(db, projectId);

        (await api.StopAutopilotSessionAsync(first.Id, AutopilotStopReason.Drained))
            .Should().BeTrue();
        (await api.StopAutopilotSessionAsync(first.Id, AutopilotStopReason.Manual))
            .Should().BeFalse("a closed session must not be re-closed with a different reason");

        db.ChangeTracker.Clear();
        var reopened = async () => await OpenSessionAsync(db, projectId);
        await reopened.Should().NotThrowAsync("toggling back on starts a fresh session");
    }

    [Fact]
    public async Task Candidates_AreDraftingOnly_AndExcludeUnsubmittableWork()
    {
        var (projectId, repoId) = await SeedProjectAsync();
        var draft = await SeedTaskAsync(projectId, repoId, "draft");
        var noRepo = await SeedTaskAsync(projectId, repoId, "no repo");
        var archived = await SeedTaskAsync(projectId, repoId, "archived");

        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();

        await db.Tasks.Where(t => t.Id == noRepo)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RepoId, (Ulid?)null));
        await db.Tasks.Where(t => t.Id == archived)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.IsArchived, true));

        var orgId = await db.Tasks.AsNoTracking()
            .Where(t => t.Id == draft).Select(t => t.OrgId).FirstAsync();

        var candidates = await api.ListAutopilotCandidatesAsync(orgId, projectId, 50);

        candidates.Select(c => c.Id).Should().Contain(draft);
        candidates.Select(c => c.Id).Should().NotContain(noRepo, "it can never be submitted");
        candidates.Select(c => c.Id).Should().NotContain(archived);
    }

    [Fact]
    public async Task Candidates_PutCriticalFirst()
    {
        var (projectId, repoId) = await SeedProjectAsync();
        var high = await SeedTaskAsync(projectId, repoId, "high");
        var critical = await SeedTaskAsync(projectId, repoId, "critical");

        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();

        await db.Tasks.Where(t => t.Id == high)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Priority, Priority.High));
        await db.Tasks.Where(t => t.Id == critical)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Priority, Priority.Critical));

        var orgId = await db.Tasks.AsNoTracking()
            .Where(t => t.Id == high).Select(t => t.OrgId).FirstAsync();

        // Priority is persisted as a STRING, so ordering by the enum property sorts
        // alphabetically and puts Critical LAST — silently, because the result still looks
        // like a deterministic priority order.
        var candidates = await api.ListAutopilotCandidatesAsync(orgId, projectId, 10);

        candidates[0].Id.Should().Be(critical, "Critical outranks High");
    }

    [Fact]
    public async Task Candidates_ExcludeTasksWhoseBlockerIsStillInDrafting()
    {
        var (projectId, repoId) = await SeedProjectAsync();
        var blocker = await SeedTaskAsync(projectId, repoId, "auth endpoints");
        var dependent = await SeedTaskAsync(projectId, repoId, "auth screens");

        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();

        var orgId = await db.Tasks.AsNoTracking()
            .Where(t => t.Id == blocker).Select(t => t.OrgId).FirstAsync();

        db.TaskDependencies.Add(new TaskDependency
        {
            Id = Ulid.NewUlid(),
            OrgId = orgId,
            TaskId = dependent,
            DependsOnTaskId = blocker,
            Source = DependencySource.Ai,
        });
        await db.SaveChangesAsync();

        var candidates = await api.ListAutopilotCandidatesAsync(orgId, projectId, 10);

        // The queue can defer a task behind one that is RUNNING; it cannot defer behind one
        // still in Drafting. Submitting the dependent first parks it in the queue forever —
        // and, because queued work counts as in-flight, it can consume the headroom the
        // blocker needed to be submitted at all.
        candidates.Select(c => c.Id).Should().Contain(blocker);
        candidates.Select(c => c.Id).Should().NotContain(dependent,
            "its blocker has not even been submitted yet");
    }

    /// <summary>
    /// The next milestone plans while the previous one finishes its last build, and builds only
    /// once it ships — on the real database, where the nested milestone/task query has to translate.
    /// Prod, 2026-09-17: ten drafted tasks sat while one task finished, and autopilot stopped
    /// itself twice.
    /// </summary>
    [Fact]
    public async Task NextMilestone_PlansWhileTheLastTaskBuilds_AndBuildsOnlyOnceItShips()
    {
        var (projectId, repoId) = await SeedProjectAsync();
        var lastTask = await SeedTaskAsync(projectId, repoId, "order history API");
        var nextTask = await SeedTaskAsync(projectId, repoId, "product reviews API");

        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var orgId = await db.Tasks.AsNoTracking().Where(t => t.Id == lastTask).Select(t => t.OrgId).FirstAsync();

        var current = new Milestone
        {
            Id = Ulid.NewUlid(), OrgId = orgId, ProjectId = projectId, Title = "Checkout", Position = 3,
            Status = MilestoneStatus.Active,
        };
        var next = new Milestone
        {
            Id = Ulid.NewUlid(), OrgId = orgId, ProjectId = projectId, Title = "Reviews", Position = 4,
        };
        db.Milestones.AddRange(current, next);
        db.MilestoneDependencies.Add(new MilestoneDependency
        {
            Id = Ulid.NewUlid(), OrgId = orgId, MilestoneId = next.Id, DependsOnMilestoneId = current.Id,
            Source = DependencySource.Manual,
        });
        await db.SaveChangesAsync();
        await db.Tasks.Where(t => t.Id == lastTask).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.MilestoneId, current.Id)
            .SetProperty(t => t.CurrentLane, Lane.Planning)
            .SetProperty(t => t.Status, TaskStatus.Active)
            .SetProperty(t => t.DependencyAnalysisStartedAt, (DateTimeOffset?)null));
        await db.Tasks.Where(t => t.Id == nextTask).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.MilestoneId, next.Id)
            .SetProperty(t => t.DependencyAnalysisStartedAt, (DateTimeOffset?)null));

        (await api.ListAutopilotCandidatesAsync(orgId, projectId, 10)).Select(c => c.Id)
            .Should().NotContain(nextTask, "the last task of the current milestone is still being planned");

        await db.Tasks.Where(t => t.Id == lastTask).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.CurrentLane, Lane.Developing));
        (await api.ListAutopilotCandidatesAsync(orgId, projectId, 10)).Select(c => c.Id)
            .Should().Contain(nextTask, "every task of the milestone it waits on is building");

        // Submitted by autopilot: its planning run is picked up past the gate…
        var session = await OpenSessionAsync(db, projectId);
        await db.Tasks.Where(t => t.Id == nextTask).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.AutopilotSessionId, session.Id)
            .SetProperty(t => t.CurrentLane, Lane.Planning)
            .SetProperty(t => t.Status, TaskStatus.InQueue));
        (await api.GetTasksWithUnmetDependenciesAsync([nextTask])).Should().NotContain(nextTask);
        (await api.GetMilestoneHeldTaskIdsAsync([nextTask])).Should().Contain(nextTask);
        (await api.IsTaskMilestoneHeldAsync(nextTask)).Should().BeTrue();

        // …its approved plan is held at Developing…
        await db.Tasks.Where(t => t.Id == nextTask).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.CurrentLane, Lane.Developing));
        (await api.GetTasksWithUnmetDependenciesAsync([nextTask])).Should().Contain(nextTask);
        (await api.HasMilestoneWorkInFlightAsync(projectId)).Should().BeTrue(
            "the current milestone's last task is still building");

        // …and released the moment the milestone ships.
        await db.Milestones.Where(m => m.Id == current.Id).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.Status, MilestoneStatus.Completed)
            .SetProperty(m => m.ShipState, MilestoneShipState.Shipped));
        (await api.GetTasksWithUnmetDependenciesAsync([nextTask])).Should().NotContain(nextTask);
        (await api.IsTaskMilestoneHeldAsync(nextTask)).Should().BeFalse();
    }

    [Fact]
    public async Task Candidates_IncludeADependentOnceItsBlockerIsSubmitted()
    {
        var (projectId, repoId) = await SeedProjectAsync();
        var blocker = await SeedTaskAsync(projectId, repoId, "auth endpoints");
        var dependent = await SeedTaskAsync(projectId, repoId, "auth screens");

        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();

        var orgId = await db.Tasks.AsNoTracking()
            .Where(t => t.Id == blocker).Select(t => t.OrgId).FirstAsync();

        db.TaskDependencies.Add(new TaskDependency
        {
            Id = Ulid.NewUlid(),
            OrgId = orgId,
            TaskId = dependent,
            DependsOnTaskId = blocker,
            Source = DependencySource.Ai,
        });
        await db.SaveChangesAsync();

        await db.Tasks.Where(t => t.Id == blocker).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.CurrentLane, Lane.Ready)
            .SetProperty(t => t.Status, TaskStatus.InQueue));

        // Now the queue CAN defer it, which is the intended pipelining — the exclusion is
        // specifically about blockers that are not in the pipeline at all.
        (await api.ListAutopilotCandidatesAsync(orgId, projectId, 10))
            .Select(c => c.Id).Should().Contain(dependent);
    }

    [Fact]
    public async Task ApiContracts_AreOnlyPublishedForCrossRepoDependents()
    {
        var (projectId, repoId) = await SeedProjectAsync();

        var repoResp = await _client.PostAsJsonAsync($"/api/projects/{projectId}/repos",
            new AddProjectRepoRequest("ap-web", "https://github.com/test/web.git", "main", null, null));
        var webRepo = await repoResp.Content.ReadFromJsonAsync<ProjectRepoResponse>(TestJsonOptions.Default);

        var api1 = await SeedTaskAsync(projectId, repoId, "endpoints");
        var sameRepoDependent = await SeedTaskAsync(projectId, repoId, "same repo work");
        var crossRepoDependent = await SeedTaskAsync(projectId, repoId, "screens");

        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();

        var orgId = await db.Tasks.AsNoTracking()
            .Where(t => t.Id == api1).Select(t => t.OrgId).FirstAsync();

        await db.Tasks.Where(t => t.Id == crossRepoDependent)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RepoId, webRepo!.Id));

        db.TaskDependencies.Add(new TaskDependency
        {
            Id = Ulid.NewUlid(), OrgId = orgId,
            TaskId = sameRepoDependent, DependsOnTaskId = api1, Source = DependencySource.Ai,
        });
        await db.SaveChangesAsync();

        // A same-repo dependent can read the code in its own worktree — extracting a contract
        // for it would be a model call nobody reads.
        (await api.HasCrossRepoDependentsAsync(api1)).Should().BeFalse();

        db.TaskDependencies.Add(new TaskDependency
        {
            Id = Ulid.NewUlid(), OrgId = orgId,
            TaskId = crossRepoDependent, DependsOnTaskId = api1, Source = DependencySource.Ai,
        });
        await db.SaveChangesAsync();

        (await api.HasCrossRepoDependentsAsync(api1)).Should().BeTrue(
            "the dependent is in another repo, where merging this task's PR lands nothing");
    }

    [Fact]
    public async Task BlockerApiContracts_ExposeOnlyCrossRepoBlockers()
    {
        var (projectId, repoId) = await SeedProjectAsync();

        var repoResp = await _client.PostAsJsonAsync($"/api/projects/{projectId}/repos",
            new AddProjectRepoRequest("ap-api", "https://github.com/test/api.git", "main", null, null));
        var apiRepo = await repoResp.Content.ReadFromJsonAsync<ProjectRepoResponse>(TestJsonOptions.Default);

        var backend = await SeedTaskAsync(projectId, repoId, "auth endpoints");
        var sameRepoBlocker = await SeedTaskAsync(projectId, repoId, "shared helper");
        var client = await SeedTaskAsync(projectId, repoId, "auth screens");

        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();

        var orgId = await db.Tasks.AsNoTracking()
            .Where(t => t.Id == client).Select(t => t.OrgId).FirstAsync();

        await db.Tasks.Where(t => t.Id == backend)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RepoId, apiRepo!.Id));

        foreach (var blocker in new[] { backend, sameRepoBlocker })
        {
            db.TaskDependencies.Add(new TaskDependency
            {
                Id = Ulid.NewUlid(), OrgId = orgId,
                TaskId = client, DependsOnTaskId = blocker, Source = DependencySource.Ai,
            });
        }
        await db.SaveChangesAsync();

        await api.SetApiSurfaceAsync(backend, "### Endpoints\nPOST /api/auth/login");
        await api.SetApiSurfaceAsync(sameRepoBlocker, "### Endpoints\nGET /internal/helper");

        var contracts = await api.ListBlockerApiContractsAsync(client);

        // The same-repo blocker's code is already checked out where this task runs — injecting
        // its contract would spend context re-describing files the agent can just open.
        contracts.Should().HaveCount(1);
        contracts[0].TaskId.Should().Be(backend);
        contracts[0].ApiSurface.Should().Contain("POST /api/auth/login");
    }

    [Fact]
    public async Task BlockerApiContracts_AreEmptyWhenNoBlockerPublishedOne()
    {
        var (projectId, repoId) = await SeedProjectAsync();
        var task = await SeedTaskAsync(projectId, repoId, "solo");

        using var scope = CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();

        (await api.ListBlockerApiContractsAsync(task)).Should().BeEmpty();
    }

    [Fact]
    public async Task Candidates_RespectTheLimit()
    {
        var (projectId, repoId) = await SeedProjectAsync();
        for (var i = 0; i < 5; i++)
            await SeedTaskAsync(projectId, repoId, $"task {i}");

        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();

        var orgId = await db.Tasks.AsNoTracking()
            .Where(t => t.ProjectId == projectId).Select(t => t.OrgId).FirstAsync();

        // Headroom is what stops the whole backlog landing in InQueue at once.
        (await api.ListAutopilotCandidatesAsync(orgId, projectId, 2)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Progress_SeparatesMovingWorkFromWorkWaitingOnAHuman()
    {
        var (projectId, repoId) = await SeedProjectAsync();
        var running = await SeedTaskAsync(projectId, repoId, "running");
        var parked = await SeedTaskAsync(projectId, repoId, "parked");
        var failed = await SeedTaskAsync(projectId, repoId, "failed");

        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();

        var session = await OpenSessionAsync(db, projectId);

        await db.Tasks.Where(t => t.Id == running).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.AutopilotSessionId, session.Id)
            .SetProperty(t => t.Status, TaskStatus.Active));
        await db.Tasks.Where(t => t.Id == parked).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.AutopilotSessionId, session.Id)
            .SetProperty(t => t.Status, TaskStatus.WaitingAnswer));
        await db.Tasks.Where(t => t.Id == failed).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.AutopilotSessionId, session.Id)
            .SetProperty(t => t.Status, TaskStatus.Blocked));

        var progress = await api.GetAutopilotProgressAsync(session.Id);

        // The distinction the drain check lives on: a running task keeps the session open, a
        // parked one does not — it will never move until somebody looks at it.
        progress.Progressing.Should().Be(1);
        progress.ParkedForHuman.Should().Be(1);
        progress.Failed.Should().Be(1);
    }

    [Fact]
    public async Task Progress_ReportsTerminalOutcomesNewestFirst()
    {
        var (projectId, repoId) = await SeedProjectAsync();
        var older = await SeedTaskAsync(projectId, repoId, "older");
        var newer = await SeedTaskAsync(projectId, repoId, "newer");

        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();

        var session = await OpenSessionAsync(db, projectId);
        var now = DateTimeOffset.UtcNow;

        await db.Tasks.Where(t => t.Id == older).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.AutopilotSessionId, session.Id)
            .SetProperty(t => t.Status, TaskStatus.Completed)
            .SetProperty(t => t.UpdatedAt, now.AddMinutes(-10)));
        await db.Tasks.Where(t => t.Id == newer).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.AutopilotSessionId, session.Id)
            .SetProperty(t => t.Status, TaskStatus.Blocked)
            .SetProperty(t => t.UpdatedAt, now));

        var progress = await api.GetAutopilotProgressAsync(session.Id);

        // Newest first, so the consecutive-failure streak reads off the head of the list.
        progress.RecentlyTerminal.Should().Equal([false, true]);
    }

    [Fact]
    public async Task SessionCounters_OnlyBumpUpdatedAtWhenSomethingChanged()
    {
        var (projectId, repoId) = await SeedProjectAsync();
        await SeedTaskAsync(projectId, repoId, "seed");

        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();

        var session = await OpenSessionAsync(db, projectId);

        await api.UpdateAutopilotSessionAsync(session.Id, 1.5m, 2, 1, 0, 0);
        db.ChangeTracker.Clear();
        var afterChange = await db.AutopilotSessions.AsNoTracking().SingleAsync(s => s.Id == session.Id);
        afterChange.SpentUsd.Should().Be(1.5m);

        // The idle sweep must be a no-op. UpdatedAt is the drain check's quiet-period clock —
        // if every poll touched it, a session could never be declared drained.
        await api.UpdateAutopilotSessionAsync(session.Id, 1.5m, 2, 1, 0, 0);
        db.ChangeTracker.Clear();
        var afterNoop = await db.AutopilotSessions.AsNoTracking().SingleAsync(s => s.Id == session.Id);
        afterNoop.UpdatedAt.Should().Be(afterChange.UpdatedAt);
    }

    /// <summary>
    /// The sweep hands in a sum of UsageRecord.CostTotal — numeric(18,8) — while SpentUsd is
    /// numeric(18,2). Without rounding, the guard above compares 8 decimals against the 2 the
    /// column kept, finds them different every single time, and turns an idle 15-second sweep
    /// into a row write plus an AutopilotChanged broadcast. In prod that was 5,220 sends in 11
    /// hours to one board with nobody connected, and it also pinned UpdatedAt to "just now", so
    /// no open session could ever satisfy the drain check's quiet period.
    /// </summary>
    [Fact]
    public async Task SessionCounters_SubCentSpend_DoesNotRewriteTheRowEverySweep()
    {
        var (projectId, repoId) = await SeedProjectAsync();
        await SeedTaskAsync(projectId, repoId, "seed");

        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var notifier = (MockTaskNotifier)factory.Services.GetRequiredService<ITaskNotifier>();

        var session = await OpenSessionAsync(db, projectId);

        // Eight decimals, exactly as summed from the usage rows.
        const decimal spent = 1.23456789m;

        await api.UpdateAutopilotSessionAsync(session.Id, spent, 2, 1, 0, 0);
        db.ChangeTracker.Clear();
        var afterChange = await db.AutopilotSessions.AsNoTracking().SingleAsync(s => s.Id == session.Id);
        afterChange.SpentUsd.Should().Be(1.23m);

        var broadcastsAfterChange = CountAutopilotBroadcasts(notifier, projectId);
        broadcastsAfterChange.Should().Be(1);

        // Same numbers, next sweep: nothing to write, nothing to say.
        await api.UpdateAutopilotSessionAsync(session.Id, spent, 2, 1, 0, 0);
        db.ChangeTracker.Clear();
        var afterNoop = await db.AutopilotSessions.AsNoTracking().SingleAsync(s => s.Id == session.Id);

        afterNoop.UpdatedAt.Should().Be(afterChange.UpdatedAt);
        CountAutopilotBroadcasts(notifier, projectId).Should().Be(broadcastsAfterChange);
    }

    /// <summary>Real movement still has to reach the board — the guard must be silent, not deaf.</summary>
    [Fact]
    public async Task SessionCounters_SpendCrossingACent_StillBroadcasts()
    {
        var (projectId, repoId) = await SeedProjectAsync();
        await SeedTaskAsync(projectId, repoId, "seed");

        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var notifier = (MockTaskNotifier)factory.Services.GetRequiredService<ITaskNotifier>();

        var session = await OpenSessionAsync(db, projectId);

        await api.UpdateAutopilotSessionAsync(session.Id, 1.23456789m, 2, 1, 0, 0);
        var before = CountAutopilotBroadcasts(notifier, projectId);

        await api.UpdateAutopilotSessionAsync(session.Id, 1.24111111m, 2, 1, 0, 0);
        db.ChangeTracker.Clear();

        var after = await db.AutopilotSessions.AsNoTracking().SingleAsync(s => s.Id == session.Id);
        after.SpentUsd.Should().Be(1.24m);
        CountAutopilotBroadcasts(notifier, projectId).Should().Be(before + 1);
    }

    private static int CountAutopilotBroadcasts(MockTaskNotifier notifier, Ulid projectId) =>
        notifier.SentEvents.OfType<AutopilotChangedEvent>().Count(e => e.ProjectId == projectId);

    [Fact]
    public async Task OpenSessions_ListsAcrossOrgsWithoutAUserContext()
    {
        var (projectId, repoId) = await SeedProjectAsync();
        await SeedTaskAsync(projectId, repoId, "seed");

        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();

        var session = await OpenSessionAsync(db, projectId);

        // The Worker has no ICurrentUser, so the tenant query filter would otherwise hide
        // every session from the sweep that is supposed to run them.
        (await api.ListOpenAutopilotSessionsAsync())
            .Should().Contain(s => s.Id == session.Id);

        await api.StopAutopilotSessionAsync(session.Id, AutopilotStopReason.Manual);
        (await api.ListOpenAutopilotSessionsAsync())
            .Should().NotContain(s => s.Id == session.Id);
    }
}
