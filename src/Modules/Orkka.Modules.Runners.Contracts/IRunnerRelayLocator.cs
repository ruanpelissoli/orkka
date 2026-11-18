namespace Orkka.Modules.Runners.Contracts;

/// <summary>Where to reach a specific user's runner: the Worker pod holding their
/// WebSocket, addressed directly.</summary>
public sealed record RunnerRelayTarget(Ulid OrgId, string UserId, string BaseUrl);

public static class RunnerRelay
{
    /// <summary>Named client for request/response relay ops, carrying the Worker↔API shared
    /// secret and a real timeout. Deliberately separate from the chat relay's client, which
    /// streams for minutes and therefore runs with no timeout at all — a stuck editor launch
    /// should not hang a request forever just because chat needs to.</summary>
    public const string HttpClientName = "runner-relay-ops";
}

/// <summary>
/// Finds the Worker pod that holds a given user's runner connection.
///
/// The API host has no runner socket of its own — the hub lives on the Worker — so anything
/// the API needs from a user's machine is forwarded to the one pod that can reach it, named
/// by the presence row's <c>WorkerInstance</c> and substituted into
/// <c>Runners:RelayUrlTemplate</c>.
///
/// Deliberately answers only "where is this user's runner", with no opinion about whether
/// the caller *should* route there. Chat weighs the caller's execution preference; opening
/// an editor follows the task's runner instead. Those policies belong to their callers.
/// </summary>
public interface IRunnerRelayLocator
{
    /// <summary>Null when that user's runner is offline, or when no relay template is
    /// configured for this deployment.</summary>
    Task<RunnerRelayTarget?> LocateAsync(
        Ulid orgId, string userId, CancellationToken ct = default);
}
