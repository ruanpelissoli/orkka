namespace Orkka.Tests.Execution;

using FluentAssertions;
using Orkka.Modules.Execution.Orchestration;
using Orkka.Modules.Tasks.Contracts;

/// <summary>
/// When autopilot may call itself drained. A task waiting for its owner's runner is not
/// waiting on a person — the runner reconnecting re-queues it — so it must hold the session
/// open exactly as a running task does. Prod, 2026-09-17: a Worker restart parked the last
/// task WaitingForRunner and the session drained fifteen seconds before the runner returned.
/// </summary>
public sealed class AutopilotDrainRuleTests
{
    [Fact]
    public void ATaskWaitingForItsRunner_HoldsTheSessionOpen()
    {
        var progress = new AutopilotProgress(Progressing: 0, ParkedForHuman: 1, Completed: 10, Failed: 0,
            RecentlyTerminal: [], WaitingForRunner: 1);

        AutopilotProcessor.HasNothingLeftToMove(progress, candidateCount: 0).Should().BeFalse();
    }

    [Fact]
    public void OnlyTasksParkedOnAPerson_LetTheSessionDrain()
    {
        var progress = new AutopilotProgress(Progressing: 0, ParkedForHuman: 1, Completed: 10, Failed: 0,
            RecentlyTerminal: []);

        AutopilotProcessor.HasNothingLeftToMove(progress, candidateCount: 0).Should().BeTrue();
    }

    [Fact]
    public void RunningWork_OrSomethingToSubmit_IsNeverDrained()
    {
        var running = new AutopilotProgress(1, 0, 0, 0, []);
        AutopilotProcessor.HasNothingLeftToMove(running, candidateCount: 0).Should().BeFalse();

        var submittable = new AutopilotProgress(0, 0, 0, 0, []);
        AutopilotProcessor.HasNothingLeftToMove(submittable, candidateCount: 2).Should().BeFalse();
    }
}
