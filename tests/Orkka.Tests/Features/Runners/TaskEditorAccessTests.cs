namespace Orkka.Tests.Features.Runners;

using FluentAssertions;
using Moq;
using Orkka.Modules.Runners.Contracts;
using Orkka.Modules.Runners.Features.Editors;
using Orkka.Modules.Tasks.Contracts;
using Orkka.SharedKernel.Security;

/// <summary>
/// Opening an editor launches a process on somebody's laptop, so who that laptop belongs to
/// is the whole security question. These pin the two rules that answer it.
/// </summary>
public sealed class TaskEditorAccessTests
{
    private readonly Ulid _orgId = Ulid.NewUlid();
    private readonly Ulid _taskId = Ulid.NewUlid();
    private readonly Ulid _repoId = Ulid.NewUlid();
    private const string Owner = "user_owner";

    private TaskSnapshot Task(
        string? runnerUserId = Owner,
        Ulid? repoId = null)
        => new(
            _taskId, _orgId, Ulid.NewUlid(), repoId ?? _repoId, 1, "Add login", null,
            Priority.Medium, TaskType.Feature, Lane.Testing,
            Orkka.Modules.Tasks.Contracts.TaskStatus.WaitingUserVerification,
            false, null, 0, false, "orkka/BRW-1", null, null, null, null,
            false, false, false, false, 0, 0, null, null, null, null, null, null,
            runnerUserId);

    private static ICurrentUser User(Ulid orgId, string userId)
    {
        var user = new Mock<ICurrentUser>();
        user.SetupGet(u => u.IsAuthenticated).Returns(true);
        user.SetupGet(u => u.OrgId).Returns(orgId);
        user.SetupGet(u => u.UserId).Returns(userId);
        return user.Object;
    }

    private static IRunnerRelayLocator Locator(RunnerRelayTarget? target)
    {
        var locator = new Mock<IRunnerRelayLocator>();
        locator
            .Setup(l => l.LocateAsync(It.IsAny<Ulid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(target);
        return locator.Object;
    }

    private static ITasksApi Tasks(TaskSnapshot? snapshot)
    {
        var api = new Mock<ITasksApi>();
        api.Setup(a => a.GetTaskSnapshotAsync(It.IsAny<Ulid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot);
        return api.Object;
    }

    private Task<(TaskSnapshot Task, RunnerRelayTarget Relay)?> ResolveAsync(
        TaskSnapshot? task, string callerUserId, RunnerRelayTarget? relay = null)
        => TaskEditorAccess.ResolveAsync(
            _taskId, Tasks(task), User(_orgId, callerUserId),
            Locator(relay ?? new RunnerRelayTarget(_orgId, Owner, "http://pod:8080")),
            CancellationToken.None);

    [Fact]
    public async Task The_runners_owner_can_open_their_own_task()
    {
        var resolved = await ResolveAsync(Task(), Owner);

        resolved.Should().NotBeNull();
        resolved!.Value.Relay.BaseUrl.Should().Be("http://pod:8080");
    }

    [Fact]
    public async Task A_teammate_cannot_open_a_task_that_ran_on_someone_elses_machine()
    {
        // The whole point: a colleague must never launch a process on your laptop.
        var resolved = await ResolveAsync(Task(), "user_teammate");

        resolved.Should().BeNull();
    }

    [Fact]
    public async Task A_task_with_no_runner_owner_has_no_machine_to_open()
    {
        var resolved = await ResolveAsync(Task(runnerUserId: null), Owner);

        resolved.Should().BeNull();
    }

    [Fact]
    public async Task A_task_from_another_org_is_not_resolvable()
    {
        var foreign = Task() with { OrgId = Ulid.NewUlid() };

        var resolved = await ResolveAsync(foreign, Owner);

        resolved.Should().BeNull();
    }

    [Fact]
    public async Task A_task_with_no_repository_has_no_worktree()
    {
        var resolved = await TaskEditorAccess.ResolveAsync(
            _taskId,
            Tasks(Task() with { RepoId = null }),
            User(_orgId, Owner),
            Locator(new RunnerRelayTarget(_orgId, Owner, "http://pod:8080")),
            CancellationToken.None);

        resolved.Should().BeNull();
    }

    [Fact]
    public async Task An_offline_runner_resolves_to_nothing()
    {
        var locator = new Mock<IRunnerRelayLocator>();
        locator
            .Setup(l => l.LocateAsync(It.IsAny<Ulid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((RunnerRelayTarget?)null);

        var resolved = await TaskEditorAccess.ResolveAsync(
            _taskId, Tasks(Task()), User(_orgId, Owner), locator.Object, CancellationToken.None);

        resolved.Should().BeNull();
    }
}
