namespace Orkka.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orkka.IntegrationTests.Fixtures;
using Orkka.IntegrationTests.Helpers;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Projects.Features.ProjectRepos;
using Orkka.Modules.Projects.Features.Projects;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Features.TaskMessages;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.Modules.Trackers.Contracts;
using Orkka.Modules.Trackers.Domain;
using Orkka.Modules.Trackers.Infrastructure;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.Modules.SourceControl.Infrastructure;
using Orkka.Modules.SourceControl.Infrastructure.Services;
using Orkka.SharedKernel.Utilities;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// A runner's poll, end to end on the real database: the lease (a conditional update — exactly what
/// keeps a developer's second machine from importing every ticket again), an intake ticket
/// becoming a task with its key, link, epic and folder, a ticket that moves on staying tracked,
/// and a ticket no rule can place asking which repository — answered on the task.
/// </summary>
[Collection("Integration")]
public sealed class TrackerIngestionEndToEndTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<(Ulid ProjectId, Ulid[] RepoIds, string ConnectionKey)> SeedSourceAsync(
        bool withEpic = true, params string[] repoNames)
    {
        if (repoNames.Length == 0)
            repoNames = ["shop"];
        var projectResp = await _client.PostAsJsonAsync("/api/projects",
            new CreateProjectRequest("Polled Project", "desc"));
        var project = await projectResp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);
        await _client.StartSetupAsync(project!.Id);
        var repoIds = new List<Ulid>();
        foreach (var name in repoNames)
        {
            var repoResp = await _client.PostAsJsonAsync($"/api/projects/{project!.Id}/repos",
                new AddProjectRepoRequest(name, $"git@github.com:acme/{name}.git", "main", null));
            repoIds.Add((await repoResp.Content.ReadFromJsonAsync<ProjectRepoResponse>(TestJsonOptions.Default))!.Id);
        }

        var key = $"jira-{Ulid.NewUlid()}";
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackersDbContext>();
        var connection = new TrackerConnection
        {
            Id = Ulid.NewUlid(), OrgId = TestCurrentUser.TestOrgId, UserId = TestCurrentUser.TestUserId,
            ConnectionKey = key, Kind = TrackerKinds.Jira, SiteUrl = "https://acme.atlassian.net",
            MachineName = "dana-laptop", Healthy = true, LastSeenAt = DateTimeOffset.UtcNow,
        };
        db.Connections.Add(connection);
        db.ProjectConfigs.Add(new ProjectTrackerConfig
        {
            Id = Ulid.NewUlid(), OrgId = TestCurrentUser.TestOrgId, ProjectId = project!.Id,
            OwnerUserId = TestCurrentUser.TestUserId, TrackerConnectionId = connection.Id, Scope = "SHOP",
            AutoSubmit = false,
            ScopeMode = withEpic ? TrackerScopeMode.Epics : TrackerScopeMode.All,
            IntakeStatusesJson = """["To Do","Selected"]""",
            Epics = withEpic
                ?
                [
                    new TrackerEpicMapping
                    {
                        Id = Ulid.NewUlid(), OrgId = TestCurrentUser.TestOrgId, EpicKey = "SHOP-1",
                        EpicTitle = "Checkout", RepoId = repoIds[0], Subpath = "packages/api",
                    },
                ]
                : [],
        });
        await db.SaveChangesAsync();
        return (project.Id, [.. repoIds], key);
    }

    private static TrackerTicket Intake(string key, string? epic, TrackerTicketBody? body, params TrackerTicketComment[] comments)
        => new(key, "10012", $"https://acme.atlassian.net/browse/{key}", epic, "h1", body, comments,
            Status: "To Do", StatusCategory: "todo", AssignedToMe: true, InIntake: true, ParentKey: epic, ParentType: "Epic");

    private static TrackerTicketBody Body(string title = "Add invoice export") => new(title, "Bug", "To Do", "todo", "High",
        "Export invoices as CSV.", null, null, ["backend"], [], DateTimeOffset.UtcNow);

    /// <summary>A restarted runner (new InstanceId, the active connection) takes the lease its
    /// predecessor still held instead of waiting five minutes; the old instance's late report is
    /// refused (e2e, 2026-11-17).</summary>
    [Fact]
    public async Task TheActiveRunner_TakesOverALeaseHeldByTheInstanceItReplaced()
    {
        var (_, _, connectionKey) = await SeedSourceAsync();
        using var scope = factory.Services.CreateScope();
        var trackers = scope.ServiceProvider.GetRequiredService<ITrackersApi>();

        var before = await trackers.SyncAsync(TestCurrentUser.TestOrgId, TestCurrentUser.TestUserId,
            new TrackerSyncRequest([connectionKey], "runner-before-restart"));
        var assignment = before.Assignments.Should().ContainSingle().Subject;

        var after = await trackers.SyncAsync(TestCurrentUser.TestOrgId, TestCurrentUser.TestUserId,
            new TrackerSyncRequest([connectionKey], "runner-after-restart"), supersedesOtherInstances: true);
        after.Assignments.Should().ContainSingle("the restarted runner is the active connection");

        // A fresh scope per call, as each hub invocation gets in production.
        using var reportScope = factory.Services.CreateScope();
        var stale = await reportScope.ServiceProvider.GetRequiredService<ITrackersApi>().ReportAsync(
            TestCurrentUser.TestOrgId, TestCurrentUser.TestUserId,
            new TrackerTicketReport(assignment.ConfigId, "runner-before-restart", true, null, DateTimeOffset.UtcNow, []));
        stale.Accepted.Should().BeFalse("the replaced instance no longer holds the lease");
    }

    [Fact]
    public async Task OneMachineHoldsTheLease_AndItsReportBecomesATask_ThatStaysTrackedInProgress()
    {
        var (projectId, repoIds, connectionKey) = await SeedSourceAsync();
        using var scope = factory.Services.CreateScope();
        var trackers = scope.ServiceProvider.GetRequiredService<ITrackersApi>();

        var first = await trackers.SyncAsync(TestCurrentUser.TestOrgId, TestCurrentUser.TestUserId,
            new TrackerSyncRequest([connectionKey], "laptop-runner"));
        var second = await trackers.SyncAsync(TestCurrentUser.TestOrgId, TestCurrentUser.TestUserId,
            new TrackerSyncRequest([connectionKey], "desktop-runner"));

        var assignment = first.Assignments.Should().ContainSingle().Subject;
        assignment.EpicKeys.Should().Equal("SHOP-1");
        assignment.Scope.Should().Be("SHOP");
        assignment.ScopeMode.Should().Be(TrackerScopeModes.Epics);
        assignment.IntakeStatuses.Should().Equal("To Do", "Selected");
        assignment.Known.Should().BeEmpty();
        second.Assignments.Should().BeEmpty("the laptop holds the lease");

        var ack = await trackers.ReportAsync(TestCurrentUser.TestOrgId, TestCurrentUser.TestUserId,
            new TrackerTicketReport(assignment.ConfigId, "laptop-runner", true, null, DateTimeOffset.UtcNow,
                [Intake("SHOP-12", "SHOP-1", Body(), new TrackerTicketComment("c1", "Priya", DateTimeOffset.UtcNow, "Semicolons for the EU."))]));
        ack.Accepted.Should().BeTrue(ack.Error);

        var tasksDb = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var task = await tasksDb.Tasks.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(t => t.ProjectId == projectId && t.ExternalKey == "SHOP-12");
        task.Title.Should().Be("[SHOP-12] Add invoice export");
        task.Type.Should().Be(TaskType.BugFix);
        task.RepoId.Should().Be(repoIds[0]);
        task.ExternalUrl.Should().Be("https://acme.atlassian.net/browse/SHOP-12");
        task.ExternalEpic.Should().Be("SHOP-1 Checkout");
        task.WorkingSubpath.Should().Be("packages/api");
        task.RunnerUserId.Should().Be(TestCurrentUser.TestUserId);
        task.CurrentLane.Should().Be(Lane.Drafting, "auto-submit is off for this source");

        // The ticket moves to In Progress: it is no longer in intake but stays tracked — its
        // comments keep coming and nothing is retired.
        await trackers.ReportAsync(TestCurrentUser.TestOrgId, TestCurrentUser.TestUserId,
            new TrackerTicketReport(assignment.ConfigId, "laptop-runner", true, null, DateTimeOffset.UtcNow,
            [
                Intake("SHOP-12", "SHOP-1", null, new TrackerTicketComment("c2", "Priya", DateTimeOffset.UtcNow.AddSeconds(5), "And a total row."))
                    with { InIntake = false, Status = "In Progress", StatusCategory = "inprogress" },
            ]));

        (await tasksDb.Tasks.IgnoreQueryFilters().CountAsync(t => t.ProjectId == projectId)).Should().Be(1);
        (await tasksDb.Tasks.IgnoreQueryFilters().AsNoTracking().SingleAsync(t => t.Id == task.Id)).IsArchived.Should().BeFalse();
        (await tasksDb.TaskMessages.IgnoreQueryFilters().AsNoTracking()
                .Where(m => m.TaskId == task.Id && m.MessageType == MessageType.ExternalComment).CountAsync())
            .Should().Be(2);
        var trackersDb = scope.ServiceProvider.GetRequiredService<TrackersDbContext>();
        var link = await trackersDb.TicketLinks.IgnoreQueryFilters().AsNoTracking().SingleAsync(l => l.TaskId == task.Id);
        link.RemoteStatus.Should().Be("In Progress");
        link.ParentKey.Should().Be("SHOP-1");
        link.TrackingEndedAt.Should().BeNull();
        link.AssignedToOwner.Should().BeTrue();
    }

    [Fact]
    public async Task NoEpic_SeveralRepositories_RunnerOffline_TheTaskAsks_AndTheAnswerSetsTheRepository()
    {
        var (projectId, repoIds, connectionKey) = await SeedSourceAsync(withEpic: false, "shop-api", "shop-web");
        using var scope = factory.Services.CreateScope();
        var trackers = scope.ServiceProvider.GetRequiredService<ITrackersApi>();
        var assignment = (await trackers.SyncAsync(TestCurrentUser.TestOrgId, TestCurrentUser.TestUserId,
            new TrackerSyncRequest([connectionKey], "laptop-runner"))).Assignments.Should().ContainSingle().Subject;
        assignment.ScopeMode.Should().Be(TrackerScopeModes.All);

        var ack = await trackers.ReportAsync(TestCurrentUser.TestOrgId, TestCurrentUser.TestUserId,
            new TrackerTicketReport(assignment.ConfigId, "laptop-runner", true, null, DateTimeOffset.UtcNow,
                [Intake("SHOP-40", null, Body("Show the invoice total"))]));
        ack.Accepted.Should().BeTrue(ack.Error);

        var tasksDb = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var task = await tasksDb.Tasks.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(t => t.ProjectId == projectId && t.ExternalKey == "SHOP-40");
        task.RepoId.Should().BeNull("no rule, no default, and the repo router (the runner) is offline");
        task.CurrentLane.Should().Be(Lane.PendingQuestions);
        var question = await tasksDb.TaskMessages.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(m => m.TaskId == task.Id && m.MessageType == MessageType.Question);
        question.Content.Should().Contain("```questions").And.Contain("shop-api").And.Contain("shop-web");

        // Submitting cannot start planning without a repository.
        (await _client.PostAsync($"/api/tasks/{task.Id}/submit", null)).IsSuccessStatusCode.Should().BeFalse();

        // An answer naming no repository is refused, and the task keeps waiting.
        var refused = await _client.PostAsJsonAsync($"/api/tasks/{task.Id}/messages", new SendMessageRequest("the frontend one"));
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var answered = await _client.PostAsJsonAsync($"/api/tasks/{task.Id}/messages", new SendMessageRequest("shop-web"));
        answered.IsSuccessStatusCode.Should().BeTrue(await answered.Content.ReadAsStringAsync());

        var after = await tasksDb.Tasks.IgnoreQueryFilters().AsNoTracking().SingleAsync(t => t.Id == task.Id);
        after.RepoId.Should().Be(repoIds[1]);
        after.CurrentLane.Should().Be(Lane.Drafting, "auto-submit is off, so the answer returns it to Drafting");
        after.QuestionSourceLane.Should().BeNull();
    }

    /// <summary>
    /// A Story with two sub-tasks, end to end on the real database: intake makes the story (members in
    /// order on one branch, chained), the first member landing opens a draft pull request and
    /// releases the second, the second landing marks it ready — and write-back then moves the
    /// Story and both sub-tasks to Code Review together.
    /// </summary>
    [Fact]
    public async Task AStoryWithTwoSubTasks_LandsAsOneDraftThenReadyPullRequest_AndMovesAllThreeTicketsTogether()
    {
        var (projectId, repoIds, connectionKey) = await SeedSourceAsync(withEpic: false, "storefront");
        using var scope = factory.Services.CreateScope();
        var trackersDb = scope.ServiceProvider.GetRequiredService<TrackersDbContext>();
        var config = await trackersDb.ProjectConfigs.IgnoreQueryFilters().SingleAsync(c => c.ProjectId == projectId);
        config.StatusMapJson = """{"planning":"In Progress","prOpened":"Code Review"}""";
        await trackersDb.SaveChangesAsync();
        // The lease is taken with a conditional update below; a tracked copy would hide it.
        trackersDb.ChangeTracker.Clear();
        var trackers = scope.ServiceProvider.GetRequiredService<ITrackersApi>();
        var tasksApi = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var assignment = (await trackers.SyncAsync(TestCurrentUser.TestOrgId, TestCurrentUser.TestUserId,
            new TrackerSyncRequest([connectionKey], "laptop-runner"))).Assignments.Should().ContainSingle().Subject;

        TrackerTicket Child(string key, string title) => new(
            key, key, $"https://acme.atlassian.net/browse/{key}", null, $"h-{key}", Body(title), [],
            Status: "To Do", StatusCategory: "todo", AssignedToMe: true, InIntake: false, ParentKey: "SHOP-10", ParentType: "Story");
        var story = new TrackerTicket("SHOP-10", "10010", "https://acme.atlassian.net/browse/SHOP-10", null, "h-10",
            Body("Checkout v2") with { Type = "Story" }, [],
            Status: "To Do", StatusCategory: "todo", AssignedToMe: true, InIntake: true, ChildKeys: ["SHOP-11", "SHOP-12"]);

        var ack = await trackers.ReportAsync(TestCurrentUser.TestOrgId, TestCurrentUser.TestUserId,
            new TrackerTicketReport(assignment.ConfigId, "laptop-runner", true, null, DateTimeOffset.UtcNow,
                [Child("SHOP-12", "Pay"), story, Child("SHOP-11", "Cart")]));
        ack.Accepted.Should().BeTrue(ack.Error);

        var tasksDb = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var group = await tasksDb.TaskGroups.IgnoreQueryFilters().AsNoTracking().Include(g => g.Branches)
            .SingleAsync(g => g.ProjectId == projectId);
        group.Kind.Should().Be(TaskGroupKind.Story);
        group.BranchName.Should().StartWith("feature/SHOP-10");
        group.Branches.Should().ContainSingle().Which.RepoId.Should().Be(repoIds[0]);
        var members = await tasksDb.Tasks.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.GroupId == group.Id).OrderBy(t => t.StoryOrder).ToListAsync();
        members.Select(m => m.ExternalKey).Should().Equal("SHOP-11", "SHOP-12");
        members.Should().OnlyContain(m => m.BranchName == group.BranchName && m.RepoId == repoIds[0]);
        (await tasksApi.GetTasksWithUnmetDependenciesAsync([members[1].Id])).Should().Contain(members[1].Id,
            "the second sub-task waits for the first");
        (await trackersDb.TicketLinks.IgnoreQueryFilters().AsNoTracking().Where(l => l.ProjectId == projectId)
                .OrderBy(l => l.TicketKey).Select(l => l.Role).ToListAsync())
            .Should().Equal(TicketRole.Story, TicketRole.SubTask, TicketRole.SubTask);

        // Orkka's pull requests and the landing, with the developer's machine faked.
        var git = new StoryForge();
        var sourceDb = scope.ServiceProvider.GetRequiredService<SourceControlDbContext>();
        var projectsApi = scope.ServiceProvider.GetRequiredService<Orkka.Modules.Projects.Contracts.IProjectsApi>();
        var pullRequests = new OrkkaPullRequestService(sourceDb, projectsApi, scope.ServiceProvider, git,
            NullLogger<OrkkaPullRequestService>.Instance,
            new StoryLandingService(sourceDb, projectsApi, scope.ServiceProvider, git, NullLogger<StoryLandingService>.Instance));

        async Task LandAsync(Orkka.Modules.Tasks.Domain.TaskEntity member, string baseSha)
        {
            await tasksApi.SetTaskReviewBaseShaAsync(member.Id, baseSha);
            var pr = await pullRequests.CreateForTaskAsync(repoIds[0], member.Id, member.BranchName!, "main",
                member.Title, "body", baseSha);
            pr.IsSuccess.Should().BeTrue(pr.Error);
            var row = await tasksDb.Tasks.IgnoreQueryFilters().SingleAsync(t => t.Id == member.Id);
            row.CurrentLane = Lane.Done;
            row.Status = Orkka.Modules.Tasks.Contracts.TaskStatus.Completed;
            row.PrUrl = pr.Value!.HtmlUrl;
            await tasksDb.SaveChangesAsync();
            var landed = await pullRequests.MergeAsync(repoIds[0], pr.Value.Number);
            landed.IsSuccess.Should().BeTrue(landed.Error);
            await tasksApi.MarkTaskMergedAsync(member.Id, null);
        }

        await LandAsync(members[0], "1111111111111111111111111111111111111111");
        git.Drafts.Should().ContainSingle();
        git.Readied.Should().BeEmpty();
        (await tasksApi.GetTasksWithUnmetDependenciesAsync([members[1].Id])).Should().NotContain(members[1].Id,
            "the first sub-task passed review and is on the shared branch");

        var beforeReady = await trackers.SyncAsync(TestCurrentUser.TestOrgId, TestCurrentUser.TestUserId,
            new TrackerSyncRequest([connectionKey], "laptop-runner"));
        beforeReady.Transitions.Should().NotContain(t => t.TargetStatus == "Code Review",
            "Code Review waits for the whole story");

        await LandAsync(members[1], "2222222222222222222222222222222222222222");
        git.Readied.Should().ContainSingle().Which.Should().Be(StoryForge.DraftUrl);
        (await tasksDb.TaskGroups.IgnoreQueryFilters().AsNoTracking().SingleAsync(g => g.Id == group.Id))
            .ReadyAt.Should().NotBeNull();

        // Clear the window on moves handed out before ready, then sync again.
        foreach (var link in await trackersDb.TicketLinks.IgnoreQueryFilters().Where(l => l.ProjectId == projectId).ToListAsync())
        {
            link.PendingPhase = null;
            link.PendingSince = null;
        }
        await trackersDb.SaveChangesAsync();
        var afterReady = await trackers.SyncAsync(TestCurrentUser.TestOrgId, TestCurrentUser.TestUserId,
            new TrackerSyncRequest([connectionKey], "laptop-runner"));
        afterReady.Transitions.Where(t => t.TargetStatus == "Code Review").Select(t => t.TicketKey)
            .Should().BeEquivalentTo(["SHOP-10", "SHOP-11", "SHOP-12"]);
    }

    /// <summary>The developer's machine for the Story test: drafts, ready, bodies.</summary>
    private sealed class StoryForge : ILocalRepoGitOps
    {
        public const string DraftUrl = "https://github.com/acme/storefront/pull/21";
        public List<string> Drafts { get; } = [];
        public List<string> Readied { get; } = [];

        public Task<Result> SquashMergeAsync(Ulid orgId, Ulid repoId, string? runnerUserId,
            string headBranch, string baseBranch, string commitMessage, CancellationToken ct = default)
            => Task.FromResult(Result.Success());

        public Task<Result<string>> GetDiffAsync(Ulid orgId, Ulid repoId, Ulid taskId, string? runnerUserId,
            string baseBranch, string headBranch, CancellationToken ct = default)
            => Task.FromResult(Result<string>.Success(""));

        public Task<Result<string>> GetWorkingTreeDiffAsync(Ulid orgId, Ulid repoId, Ulid taskId, string? runnerUserId,
            string baseBranch, CancellationToken ct = default) => Task.FromResult(Result<string>.Success(""));

        public Task<bool?> WouldMergeCleanlyAsync(Ulid orgId, Ulid repoId, string? runnerUserId,
            string headBranch, string baseBranch, CancellationToken ct = default) => Task.FromResult<bool?>(true);

        public Task<Result<string>> OpenPullRequestAsync(Ulid orgId, Ulid repoId, string? runnerUserId,
            string headBranch, string baseBranch, string title, string body, ForgeKind forge, string? cliAccount,
            CancellationToken ct = default) => Task.FromResult(Result<string>.Failure("not a story call"));

        public Task<Result<string>> OpenDraftPullRequestAsync(Ulid orgId, Ulid repoId, string? runnerUserId,
            string headBranch, string baseBranch, string title, string body, ForgeKind forge, string? cliAccount,
            CancellationToken ct = default)
        {
            Drafts.Add(headBranch);
            return Task.FromResult(Result<string>.Success(DraftUrl));
        }

        public Task<Result> MarkPullRequestReadyAsync(Ulid orgId, Ulid repoId, string? runnerUserId,
            string pullRequestUrl, bool undo, ForgeKind forge, string? cliAccount, CancellationToken ct = default)
        {
            Readied.Add(pullRequestUrl);
            return Task.FromResult(Result.Success());
        }

        public Task<Result> UpdatePullRequestBodyAsync(Ulid orgId, Ulid repoId, string? runnerUserId,
            string pullRequestUrl, string body, ForgeKind forge, string? cliAccount, CancellationToken ct = default)
            => Task.FromResult(Result.Success());

        public Task<string?> GetPullRequestStateAsync(Ulid orgId, Ulid repoId, string? runnerUserId,
            string pullRequestUrl, ForgeKind forge, string? cliAccount, CancellationToken ct = default)
            => Task.FromResult<string?>("open");
    }
}
