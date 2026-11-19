namespace Orkka.Modules.Agents.Infrastructure.Services;

using Microsoft.Extensions.Logging;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Organizations.Contracts;
using Orkka.Modules.Runners.Contracts;

/// <summary>
/// The workspace's provider for new agents: its choice (Settings → Workspace → AI provider) when
/// the developer's runner can run it, else the Automatic pick. Readiness is the runner's last
/// Hello for the workspace (<see cref="IRunnerFleet.GetProviderCoverageAsync"/> — live, or the
/// last report while offline). A runner that never reported is "unknown", not "nothing": the
/// choice is trusted (the run-time route adapts and says so) and Automatic answers Claude, the
/// behaviour before the setting existed.
/// </summary>
internal sealed class WorkspaceAiProviderResolver(
    IWorkspaceAiSettings settings,
    IRunnerFleet fleet,
    IProviderReadiness fallbackReadiness,
    ILogger<WorkspaceAiProviderResolver> logger) : IWorkspaceAiProviderResolver
{
    public async Task<AiProvider?> GetChosenAsync(Ulid orgId, CancellationToken ct = default)
        => WorkspaceAiProviders.ToProvider(await settings.GetAiProviderAsync(orgId, ct));

    public async Task<WorkspaceProviderResolution> ResolveAsync(
        Ulid orgId, string? userId, CancellationToken ct = default)
    {
        var stored = await settings.GetAiProviderAsync(orgId, ct);
        var chosen = WorkspaceAiProviders.ToProvider(stored);
        var choice = chosen is null ? WorkspaceAiProviders.Automatic : stored!.Trim().ToLowerInvariant();

        var readiness = await ReadinessAsync(orgId, userId, ct) ?? fallbackReadiness;
        var provider = readiness.PreferredProvider(chosen);
        var blocker = chosen is { } c && c != provider ? readiness.GetBlocker(c) : null;

        if (blocker is not null)
            logger.LogWarning(
                "Workspace {OrgId} chose {Chosen} but it cannot run ({Blocker}); seeding on {Provider}",
                orgId, chosen, blocker, provider);

        return new WorkspaceProviderResolution(choice, provider, blocker);
    }

    /// <summary>The runner coverage of <paramref name="userId"/>, or of the workspace's runner
    /// when no user is given (the startup backfill). Null when nothing is known.</summary>
    private async Task<IProviderReadiness?> ReadinessAsync(Ulid orgId, string? userId, CancellationToken ct)
    {
        try
        {
            userId ??= (await fleet.ListPresenceAsync(orgId, ct)).FirstOrDefault()?.UserId;
            if (string.IsNullOrEmpty(userId))
                return null;
            var coverage = await fleet.GetProviderCoverageAsync(orgId, userId, ct);
            return coverage is null ? null : new CoverageProviderReadiness(coverage);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Never fail a project creation over a readiness read — seed as if nothing were known.
            logger.LogWarning(ex, "Could not read runner coverage for workspace {OrgId}", orgId);
            return null;
        }
    }
}

/// <summary>Readiness from a runner's last provider report: ready = CLI found AND signed in
/// (the two-part rule every other reader uses).</summary>
internal sealed class CoverageProviderReadiness(RunnerProviderCoverage coverage) : IProviderReadiness
{
    public string? GetBlocker(AiProvider provider)
    {
        var key = WorkspaceAiProviders.CliKey(provider);
        if (coverage.IsReady(key))
            return null;
        var status = coverage.Find(key);
        if (!string.IsNullOrWhiteSpace(status?.Detail))
            return status!.Detail;
        var name = ProviderDisplayNames.Of(provider);
        return status is { CliFound: true }
            ? $"{name} is not signed in on {coverage.MachineName}."
            : $"The {key} CLI is not installed on {coverage.MachineName}.";
    }

    public IReadOnlyList<AiProvider> Connected
        => ProviderPreference.Order
            .Where(p => coverage.IsReady(WorkspaceAiProviders.CliKey(p)))
            .ToList();

    public string? GetAnyProviderBlocker()
        => Connected.Count > 0 ? null : $"No AI provider is signed in on {coverage.MachineName}.";
}
