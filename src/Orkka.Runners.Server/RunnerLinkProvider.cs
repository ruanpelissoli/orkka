namespace Orkka.Runners.Server;

/// <summary>
/// Resolves the <see cref="IRunnerLink"/> for a runner identity. Never returns null:
/// an identity whose runner is offline gets a link that reports a disconnected
/// <c>Status</c> and throws <see cref="RunnerUnavailableException"/> on use — callers
/// keep one error-handling path regardless of presence.
/// </summary>
public interface IRunnerLinkProvider
{
    IRunnerLink GetLink(Ulid orgId, string userId);
}

/// <summary>
/// Orkka Local: one machine, one user, one runner — the identity key is ignored and
/// every caller shares THE link. A hosted edition substitutes a multi-tenant provider
/// keyed by (org, user).
/// </summary>
public sealed class SingleRunnerLinkProvider(RunnerLink link) : IRunnerLinkProvider
{
    public IRunnerLink GetLink(Ulid orgId, string userId) => link;
}
