namespace Orkka.Modules.Runners.PublicApi;

using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Runners.Contracts;
using Orkka.SharedKernel.Ai;

/// <summary>
/// Every task runs on its creator's own runner: agents use the developer's AI subscription and
/// git credentials, and there is no platform key to fall back on. The model only decides what
/// the user is told — a provider their runner does not have signed in is said at creation, and
/// the task waits for it rather than being refused.
/// </summary>
public sealed class RunnerCoverageResolver(IRunnerFleet fleet) : IRunnerCoverageResolver
{
    public async Task<RunnerCoverageDecision> ResolveAsync(
        RunnerCoverageQuery query, CancellationToken ct = default)
    {
        var model = DevModelResolution.Resolve(
            query.TaskModelOverride, query.AgentModel, query.ProjectDefaultModel);
        var provider = ModelProviders.FromModelId(model);

        var coverage = await fleet.GetProviderCoverageAsync(query.OrgId, query.UserId, ct);
        var key = RunnerProviders.FromAiProviderName(provider.ToString());
        if (coverage is null)
            return Decision(query.UserId, provider, model, false,
                RunnerCoverageReason.NoRunnerCoverageKnown, null);
        return coverage.IsReady(key)
            ? Decision(query.UserId, provider, model, true,
                RunnerCoverageReason.RunnerCoversProvider, coverage)
            : Decision(query.UserId, provider, model, false,
                RunnerCoverageReason.ProviderNotSignedIn, coverage, coverage.Find(key)?.Detail);
    }

    private static RunnerCoverageDecision Decision(
        string runnerUserId, AiProvider provider, string? model, bool covered,
        RunnerCoverageReason reason, RunnerProviderCoverage? coverage, string? detail = null)
        => new(runnerUserId, provider, model, covered, reason,
            coverage?.CapturedAt, coverage?.MachineName, detail);
}
