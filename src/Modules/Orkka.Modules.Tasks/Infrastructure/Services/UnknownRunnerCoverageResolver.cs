namespace Orkka.Modules.Tasks.Infrastructure.Services;

using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Runners.Contracts;

/// <summary>
/// The Tasks module's default <see cref="IRunnerCoverageResolver"/>: the task runs on the asking
/// user's runner and nothing is known about its coverage. Hosts that compose the Runners module
/// replace it with the coverage-aware resolver.
/// </summary>
public sealed class UnknownRunnerCoverageResolver : IRunnerCoverageResolver
{
    public Task<RunnerCoverageDecision> ResolveAsync(
        RunnerCoverageQuery query, CancellationToken ct = default)
    {
        var model = DevModelResolution.Resolve(
            query.TaskModelOverride, query.AgentModel, query.ProjectDefaultModel);
        return Task.FromResult(new RunnerCoverageDecision(
            query.UserId, ModelProviders.FromModelId(model), model,
            CoveredByRunner: false, RunnerCoverageReason.NoRunnerCoverageKnown,
            CoverageAsOf: null, RunnerMachineName: null, ProviderDetail: null));
    }
}
