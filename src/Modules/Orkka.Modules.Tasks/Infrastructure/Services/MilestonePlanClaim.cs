namespace Orkka.Modules.Tasks.Infrastructure.Services;

/// <summary>
/// Which replica may run a queued milestone plan.
///
/// Pulled out of <see cref="MilestonePlanQueueProcessor"/> as a pure rule because it is the one
/// piece of that class with a wrong answer worth writing down: defer forever and the plan never
/// runs, defer never and a replica without the requester's runner socket fails a plan the
/// socket's holder could have served (there is no platform-key fallback).
/// </summary>
internal static class MilestonePlanClaim
{
    /// <param name="canExecuteHere">This process holds the requester's runner socket.</param>
    /// <param name="age">How long the job has been queued.</param>
    /// <param name="grace">How long a MyRunner job waits for the socket's holder.</param>
    public static bool ShouldClaimHere(
        bool canExecuteHere, TimeSpan age, TimeSpan grace) =>
        // The socket is here: this replica is the one that can serve it for free.
        canExecuteHere
        // Nobody claimed it in time — the runner is offline everywhere, or its holder is
        // wedged. Any replica takes it (and fails it with the runner-offline message) rather
        // than letting the job rot until the stale sweep gives up on it.
        || age > grace;
}
