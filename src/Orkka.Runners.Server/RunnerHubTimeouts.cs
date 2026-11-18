namespace Orkka.Runners.Server;

/// <summary>
/// How long the runner hub and the runner client wait for each other before calling the
/// connection dead. SignalR's 30-second defaults dropped a laptop four times in twenty
/// minutes while it was compiling the user's app, running an agent and diffing a worktree at
/// once (prod, 2026-09-17): every drop failed the runs in flight and re-queued the task, so
/// the work that overloaded the machine was started over. Two minutes rides out a busy spell;
/// a machine that is genuinely gone is still noticed. The runner mirrors this value on its
/// side (<c>RunnerConnection.HostTimeout</c>).
/// </summary>
public static class RunnerHubTimeouts
{
    public static readonly TimeSpan ClientTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Hub methods one runner connection may have running at once. SignalR's default is 1, and
    /// several of this hub's methods await a CLIENT RESULT from the same runner (ticket intake
    /// routes a ticket with a utility completion on the runner while <c>ReportTickets</c> runs).
    /// With one slot, any other invocation the runner makes meanwhile (<c>RotateToken</c> on the
    /// session's first minute tick, a tool call) parks the hub's read loop waiting for the slot,
    /// so the completion behind it is never read: <c>ReportTickets</c> waited out the whole 900 s
    /// op timeout and the Story was routed as if the runner were offline (R5 e2e run,
    /// 2026-11-18; a dump showed DispatchMessagesAsync parked in the invocation semaphore).
    /// </summary>
    public const int MaxParallelInvocationsPerClient = 8;
}
