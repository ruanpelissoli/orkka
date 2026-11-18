namespace Orkka.Tests.Worker;

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Runners.Server;
using Orkka.Runners.Server.Hosting;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>
/// The Worker routes every task to its owner's runner link — there is no other route. A task
/// without an owner gets a link nobody can connect to, so it reads as an offline runner rather
/// than running anywhere else.
/// </summary>
public sealed class RunnerExecutionRouterTests
{
    private static readonly Ulid OrgId = Ulid.NewUlid();

    private static TaskSnapshot Task(string? runnerUserId) => new(
        Id: Ulid.NewUlid(), OrgId: OrgId, ProjectId: Ulid.NewUlid(), RepoId: Ulid.NewUlid(),
        Number: 1, Title: "Runs on the runner", Description: null,
        Priority: Priority.Medium, Type: TaskType.Feature,
        CurrentLane: Lane.Developing, Status: TaskStatus.Active, IsArchived: false,
        RequesterLocale: null, TotalTokensUsed: 0, HasAgentWorking: false,
        BranchName: null, PrUrl: null, Tags: null,
        ContextFiles: null, ContextDocumentIds: null,
        SkipPlanning: false, SkipTesting: false, SkipCodeReview: false, IsMerged: false,
        ReviewCycleCount: 0, QaCycleCount: 0, QuestionSourceLane: null,
        AssignedAgentId: null, CurrentAgentId: null, SourceMessageId: null,
        StartedAt: null, CompletedAt: null,
        RunnerUserId: runnerUserId);

    private static (RunnerExecutionRouter Router, Mock<IRunnerLinkProvider> Links) Router()
    {
        var links = new Mock<IRunnerLinkProvider>();
        links.Setup(l => l.GetLink(It.IsAny<Ulid>(), It.IsAny<string>()))
            .Returns(() =>
            {
                var link = new Mock<IRunnerLink>();
                link.SetupGet(l => l.IsConnected).Returns(false);
                return link.Object;
            });
        return (new RunnerExecutionRouter(
            links.Object, new ProjectToolRegistry(),
            new Orkka.Git.NullGitOperationLedger(), NullLoggerFactory.Instance), links);
    }

    [Fact]
    public void ATask_RoutesToItsOwnersRunnerLink()
    {
        var (router, links) = Router();

        var route = router.Resolve(Task("user_a"));

        links.Verify(l => l.GetLink(OrgId, "user_a"), Times.Once);
        route.Invoker.Should().BeOfType<RunnerAgentInvoker>();
        route.Git.Should().BeOfType<RunnerGitWorkspaceManager>();
        route.Repo.Should().BeOfType<RunnerGitRepoReader>();
        route.IsBackendLive.Should().BeFalse("liveness is the link's");
    }

    [Fact]
    public void Routes_AreCachedPerOwner()
    {
        var (router, links) = Router();

        var first = router.Resolve(Task("user_a"));
        var again = router.Resolve(Task("user_a"));
        var other = router.Resolve(Task("user_b"));

        again.Should().BeSameAs(first);
        other.Should().NotBeSameAs(first);
        links.Verify(l => l.GetLink(OrgId, "user_a"), Times.Once);
    }

    [Fact]
    public void ATaskWithNoOwner_GetsALinkNobodyCanConnectTo()
    {
        var (router, links) = Router();

        router.Resolve(Task(null)).IsBackendLive.Should().BeFalse();
        links.Verify(l => l.GetLink(OrgId, ""), Times.Once);
    }
}
