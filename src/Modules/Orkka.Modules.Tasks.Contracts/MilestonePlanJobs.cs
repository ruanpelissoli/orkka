namespace Orkka.Modules.Tasks.Contracts;

/// <summary>
/// Lifecycle of one asynchronous milestone-planning run.
///
/// Planning is a single large model call that measured 90s–210s in production, so it cannot
/// live on the request thread: a browser that navigates away, sleeps, or is closed used to
/// abort the HTTP call and destroy minutes of work the org had already paid for in tokens.
/// The row is the durable handle a reconnecting client picks the result back up from.
/// </summary>
public enum MilestonePlanStatus
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
}

/// <summary>
/// Why a plan failed, in a form the client can branch on. The message alone is not enough:
/// a budget block used to arrive as HTTP 402 and drive a "top up" prompt, and collapsing it
/// into a generic error would lose that.
/// </summary>
public static class MilestonePlanFailureCodes
{
    /// <summary>The requester's runner could not serve the plan (offline, or no AI CLI signed
    /// in). Planning runs only there — there is no platform-key fallback — so the client asks
    /// them to run <c>./orkka start</c>; the message says so too.</summary>
    public const string RunnerOffline = "runner_offline";

    /// <summary>The model replied with something unusable — truncated, fenced prose, no
    /// tasks. Retrying is the right move and the client says so.</summary>
    public const string Unusable = "unusable";

    /// <summary>Anything else. The message carries the root cause.</summary>
    public const string Error = "error";
}

/// <summary>
/// Named steps a planning run moves through, pushed to the client as they change.
///
/// Deliberately coarse and honest: the model call is one opaque request, so there is no real
/// percentage to report. What the client can show — which phase, and how long it has been
/// running — is what actually stops someone abandoning the operation, which is what destroyed
/// the work in the first place.
/// </summary>
public static class MilestonePlanSteps
{
    /// <summary>Queued, not yet claimed by a worker.</summary>
    public const string Queued = "Queued";

    /// <summary>Reading the repositories and the existing board.</summary>
    public const string ReadingProject = "ReadingProject";

    /// <summary>The model call. Nearly all of the wall clock.</summary>
    public const string Planning = "Planning";

    /// <summary>Resolving build order across repositories.</summary>
    public const string Ordering = "Ordering";
}
