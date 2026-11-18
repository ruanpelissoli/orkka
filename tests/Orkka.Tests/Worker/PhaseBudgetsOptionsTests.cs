namespace Orkka.Tests.Worker;

using Orkka.Modules.Execution.Agents;
using FluentAssertions;

public sealed class PhaseBudgetsOptionsTests
{
    // The reason this feature exists: code work must have more headroom than a PM refine,
    // and a from-scratch scaffold more than an incremental edit. Wall-clock is the only cap
    // left, so it carries that ordering — guard it so a future tweak can't silently invert it.
    [Fact]
    public void Defaults_GiveHeavyPhasesMoreHeadroom()
    {
        var b = new PhaseBudgetsOptions();

        b.Developing.MaxWallClockSeconds.Should().BeGreaterThan(b.PmRefine.MaxWallClockSeconds);
    }

    // Regression anchor for the "Agent run reached the 40 tool-call limit and was stopped"
    // failures: a pipeline agent decides how many tools and turns the task needs. Only
    // wall-clock stops a run, so no phase may ship with a turn or tool-call cap armed.
    [Fact]
    public void Defaults_LeaveTurnsAndToolCallsUncapped()
    {
        var b = new PhaseBudgetsOptions();

        var phases = new[]
        {
            b.PmRefine, b.Planning, b.Developing, b.Testing, b.Review, b.MergeResolve,
        };

        phases.Should().OnlyContain(p => p.MaxTurns <= 0 && p.MaxToolCalls <= 0);
        // ...while every phase keeps a real wall-clock stop for a genuinely stuck run.
        phases.Should().OnlyContain(p => p.MaxWallClockSeconds > 0);
    }
}
