namespace Orkka.Tests.Services;

using Orkka.LocalRunner.Contracts;
using Orkka.Runners.Server;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

/// <summary>
/// The host-side half of task-scoped cancellation: the runner proxy forwards CancelTaskRuns
/// when the connected runner can honor it, degrades loudly-but-safely when it cannot, and
/// lets an offline runner surface as RunnerUnavailableException so the reset sweep keeps
/// the task pending instead of deleting a worktree a live agent may still be reading.
/// </summary>
public sealed class RunnerAgentInvokerCancelTests
{
    private static RunnerHello Hello(params string[] capabilities) => new(
        RunnerProtocol.ProtocolVersion, "dev-pc", true, true, [], [],
        Capabilities: capabilities);

    [Fact]
    public async Task CancelTaskRunsAsync_CapableRunner_SendsCancelTaskRunsForTheTask()
    {
        var taskId = Ulid.NewUlid();
        var link = new Mock<IRunnerLink>();
        link.SetupGet(l => l.IsConnected).Returns(true);
        link.SetupGet(l => l.Status).Returns((true, Hello(RunnerCapabilities.Hybrid, RunnerCapabilities.TaskCancel)));
        object?[]? sentArgs = null;
        link.Setup(l => l.InvokeAsync<RunnerOpResult>(
                RunnerProtocol.CancelTaskRuns, It.IsAny<object?[]>(), It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>()))
            .Callback<string, object?[], CancellationToken, TimeSpan?>((_, args, _, _) => sentArgs = args)
            .ReturnsAsync(RunnerOpResult.Ok("2"));

        var invoker = new RunnerAgentInvoker(link.Object, NullLogger<RunnerAgentInvoker>.Instance);
        await invoker.CancelTaskRunsAsync(taskId);

        sentArgs.Should().NotBeNull();
        sentArgs!.Single().Should().BeOfType<CancelTaskRunsRequest>()
            .Which.TaskId.Should().Be(taskId.ToString());
        link.Verify(l => l.InvokeAsync<RunnerOpResult>(
            RunnerProtocol.CancelTaskRuns, It.IsAny<object?[]>(), It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>()),
            Times.Once);
    }

    [Fact]
    public async Task CancelTaskRunsAsync_RunnerWithoutTheCapability_IsSkippedWithoutThrowing()
    {
        var link = new Mock<IRunnerLink>();
        link.SetupGet(l => l.IsConnected).Returns(true);
        link.SetupGet(l => l.Status).Returns((true, Hello(RunnerCapabilities.Hybrid)));

        var invoker = new RunnerAgentInvoker(link.Object, NullLogger<RunnerAgentInvoker>.Instance);
        var act = () => invoker.CancelTaskRunsAsync(Ulid.NewUlid());

        await act.Should().NotThrowAsync("an old runner cannot be asked, and cleanup must still proceed");
        link.Verify(l => l.InvokeAsync<RunnerOpResult>(
            It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>()),
            Times.Never);
    }

    [Fact]
    public async Task CancelTaskRunsAsync_OfflineRunner_PropagatesRunnerUnavailable()
    {
        // Disconnected links report no Hello and throw from every op — the sweep must see
        // that, not a silent "nothing to cancel".
        var link = new Mock<IRunnerLink>();
        link.SetupGet(l => l.IsConnected).Returns(false);
        link.SetupGet(l => l.Status).Returns((false, (RunnerHello?)null));
        link.Setup(l => l.InvokeAsync<RunnerOpResult>(
                It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>()))
            .ThrowsAsync(new RunnerUnavailableException("Your runner is not connected."));

        var invoker = new RunnerAgentInvoker(link.Object, NullLogger<RunnerAgentInvoker>.Instance);
        var act = () => invoker.CancelTaskRunsAsync(Ulid.NewUlid());

        await act.Should().ThrowAsync<RunnerUnavailableException>();
    }

    [Fact]
    public async Task CancelTaskRunsAsync_RunnerReportsFailure_Throws()
    {
        var link = new Mock<IRunnerLink>();
        link.SetupGet(l => l.IsConnected).Returns(true);
        link.SetupGet(l => l.Status).Returns((true, Hello(RunnerCapabilities.Hybrid, RunnerCapabilities.TaskCancel)));
        link.Setup(l => l.InvokeAsync<RunnerOpResult>(
                RunnerProtocol.CancelTaskRuns, It.IsAny<object?[]>(), It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>()))
            .ReturnsAsync(RunnerOpResult.Fail("boom"));

        var invoker = new RunnerAgentInvoker(link.Object, NullLogger<RunnerAgentInvoker>.Instance);
        var act = () => invoker.CancelTaskRunsAsync(Ulid.NewUlid());

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*boom*");
    }
}
