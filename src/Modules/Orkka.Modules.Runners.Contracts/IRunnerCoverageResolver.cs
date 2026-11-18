namespace Orkka.Modules.Runners.Contracts;

using Orkka.Modules.Agents.Contracts;

/// <summary>What the developer's runner last reported about the provider a task's model needs.
/// Every task runs on its owner's runner; this only decides what the owner is told.</summary>
public enum RunnerCoverageReason
{
    /// <summary>The runner's last-known coverage has the model's provider ready.</summary>
    RunnerCoversProvider,

    /// <summary>The runner has never connected, so nothing is known about its providers.</summary>
    NoRunnerCoverageKnown,

    /// <summary>The runner's last report says the model's provider is not signed in there. The
    /// task is created and waits for the developer to sign in — there is nowhere else for it to run.</summary>
    ProviderNotSignedIn,
}

/// <param name="TaskModelOverride">The model picked on the create form, if any.</param>
/// <param name="AgentModel">The dev agent's Team-page model, when the caller resolved the agent.</param>
/// <param name="ProjectDefaultModel">The project's default model, the last fallback.</param>
public sealed record RunnerCoverageQuery(
    Ulid OrgId,
    string UserId,
    string? TaskModelOverride,
    string? AgentModel,
    string? ProjectDefaultModel);

/// <summary>The coverage reading for one task: whose runner runs it (<see cref="RunnerUserId"/>,
/// always the asking user), the model and provider it will run, and whether that runner can.</summary>
public sealed record RunnerCoverageDecision(
    string RunnerUserId,
    AiProvider Provider,
    string? Model,
    bool CoveredByRunner,
    RunnerCoverageReason Reason,
    DateTimeOffset? CoverageAsOf,
    string? RunnerMachineName,
    string? ProviderDetail)
{
    /// <summary>User-facing one-liner for the task thread; null when nothing needs explaining.</summary>
    public string? Explanation => RunnerCoverageMessages.Explain(this);
}

/// <summary>
/// Reads the developer's runner coverage for the model a task will run. A provider their runner
/// does not have signed in is said at creation, and the task waits for it rather than being
/// refused. The Runners module supplies the real implementation; hosts without it get the Tasks
/// module's default, which knows nothing about coverage.
/// </summary>
public interface IRunnerCoverageResolver
{
    Task<RunnerCoverageDecision> ResolveAsync(RunnerCoverageQuery query, CancellationToken ct = default);
}

/// <summary>All user-facing wording for the decision, in one place so the call sites never drift.</summary>
public static class RunnerCoverageMessages
{
    public static string? Explain(RunnerCoverageDecision d) => d.Reason switch
    {
        RunnerCoverageReason.ProviderNotSignedIn =>
            $"This task uses {d.Model} ({ProviderDisplayNames.Of(d.Provider)}), which isn't signed in on your "
            + $"runner{Machine(d)}{AsOf(d)}. Sign in to the {Orkka.LocalRunner.Contracts.RunnerProviders.FromAiProviderName(d.Provider.ToString())} "
            + "CLI there, or pick a model from a provider your runner has.",
        _ => null,
    };

    private static string Machine(RunnerCoverageDecision d)
        => string.IsNullOrWhiteSpace(d.RunnerMachineName) ? "" : $" {d.RunnerMachineName}";

    private static string AsOf(RunnerCoverageDecision d)
        => d.CoverageAsOf is { } t ? $" (as of {Ago(t)})" : "";

    private static string Ago(DateTimeOffset t)
    {
        var span = DateTimeOffset.UtcNow - t;
        if (span < TimeSpan.FromMinutes(2)) return "just now";
        if (span < TimeSpan.FromHours(2)) return $"{(int)span.TotalMinutes} minutes ago";
        if (span < TimeSpan.FromDays(2)) return $"{(int)span.TotalHours} hours ago";
        return $"{(int)span.TotalDays} days ago";
    }
}
