namespace Orkka.Tests.Worker;

using Orkka.Modules.Execution.Orchestration;
using Orkka.Modules.Tasks.Contracts;
using FluentAssertions;

/// <summary>
/// When does a developing run resume its previous session, and when must it start cold?
///
/// Resume is the deliberate default — cold starts were removed from the retry path because
/// they re-paid the whole context on every retry. The one exception is a session whose last
/// run COMPLETED in seconds: that is the signature of a mid-response connection drop poisoning
/// the session, where every resume answers the bounce nudge instantly, commits nothing, and
/// bounces again (observed in production at 15-second intervals, burning a review cycle per
/// bounce until the cap escalated). Resuming that session again is not fast — it is a fast
/// way to do nothing.
/// </summary>
public sealed class PoisonedResumeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 23, 19, 32, 0, TimeSpan.Zero);

    private static ExecutionSessionInfo Session(
        int durationSeconds, bool interrupted = false, bool knownStart = true) => new(
        "session-1",
        CompletedAt: T0 + TimeSpan.FromSeconds(durationSeconds),
        Interrupted: interrupted,
        StartedAt: knownStart ? T0 : null);

    [Fact]
    public void ARealRunsSession_IsResumed()
    {
        // Minutes of work — a review rejection or QA bounce coming back to a live session.
        // This is the path the cold-start removal exists for; it must stay warm.
        TaskOrchestrator.ResumeWouldRepeatAnEmptyRun(Session(durationSeconds: 300))
            .Should().BeFalse();
    }

    [Fact]
    public void AnInstantCompletedRun_RunsColdInstead()
    {
        // The poisoned case: 4-second "runs" that answered the nudge and did nothing.
        TaskOrchestrator.ResumeWouldRepeatAnEmptyRun(Session(durationSeconds: 4))
            .Should().BeTrue();
    }

    [Fact]
    public void AnInterruptedRun_IsAlwaysResumed_HoweverShort()
    {
        // A run cut off seconds in was interrupted, not empty — continuing cut-off work is
        // the whole reason session resume exists, and the scaffold budget logic depends on
        // interrupted sessions resuming.
        TaskOrchestrator.ResumeWouldRepeatAnEmptyRun(Session(durationSeconds: 4, interrupted: true))
            .Should().BeFalse();
    }

    [Fact]
    public void AnUnknownStartTime_IsGivenTheBenefitOfTheDoubt()
    {
        // Executions recorded before StartedAt travelled on this DTO — or by an older host —
        // must keep the old behavior (resume) rather than being judged on data they don't have.
        TaskOrchestrator.ResumeWouldRepeatAnEmptyRun(Session(durationSeconds: 4, knownStart: false))
            .Should().BeFalse();
    }

    [Fact]
    public void TheThresholdIsSecondsNotMinutes()
    {
        // Anything that survived past the CLI's own boot-and-read time is treated as a real
        // attempt. The boundary sits where no genuine developing run can finish.
        TaskOrchestrator.ResumeWouldRepeatAnEmptyRun(Session(durationSeconds: 29)).Should().BeTrue();
        TaskOrchestrator.ResumeWouldRepeatAnEmptyRun(Session(durationSeconds: 31)).Should().BeFalse();
    }
}
