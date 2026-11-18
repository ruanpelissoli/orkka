namespace Orkka.Modules.Runners.PublicApi;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Orkka.Modules.Runners.Contracts;

/// <summary>
/// Resolves the Worker pod holding a user's runner from the presence row and the
/// <c>Runners:RelayUrlTemplate</c> setting (e.g. <c>http://{pod}.orkka-orkka-worker:8080</c>).
///
/// An unconfigured template is a deployment state, not an error: single-process deployments
/// and Orkka Local have no relay at all, and callers turn a null into "this action isn't
/// available here" rather than a failure.
/// </summary>
internal sealed class RunnerRelayLocator(
    IRunnerFleet fleet,
    IConfiguration configuration,
    ILogger<RunnerRelayLocator> logger) : IRunnerRelayLocator
{
    private readonly string? _relayUrlTemplate = configuration["Runners:RelayUrlTemplate"];

    public async Task<RunnerRelayTarget?> LocateAsync(
        Ulid orgId, string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return null;

        var presence = await fleet.GetPresenceAsync(orgId, userId, ct);
        if (presence is not { Online: true })
            return null;

        if (string.IsNullOrWhiteSpace(_relayUrlTemplate))
        {
            logger.LogWarning(
                "Runners:RelayUrlTemplate not configured — cannot reach pod {Pod} for user {UserId}",
                presence.WorkerInstance, userId);
            return null;
        }

        return new RunnerRelayTarget(
            orgId, userId,
            _relayUrlTemplate.Replace("{pod}", presence.WorkerInstance, StringComparison.Ordinal));
    }
}
