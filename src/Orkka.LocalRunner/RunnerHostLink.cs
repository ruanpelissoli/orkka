namespace Orkka.LocalRunner;

using Microsoft.AspNetCore.SignalR.Client;

/// <summary>
/// The runner's current connection to the host, shared with the few components that need to call
/// the host rather than merely answer it.
///
/// A one-property holder rather than a dependency on <see cref="RunnerSession"/> itself,
/// because that dependency is a cycle: the connection is built from RunnerOperations, which needs
/// the agent invoker, which needs the approval gate, which needs the connection. This has no
/// dependencies at all, so both sides can hold it.
///
/// Null whenever the runner is between connections — callers must treat that as "cannot reach the
/// host", never as a default answer.
/// </summary>
public sealed class RunnerHostLink
{
    public HubConnection? Connection { get; set; }
}
