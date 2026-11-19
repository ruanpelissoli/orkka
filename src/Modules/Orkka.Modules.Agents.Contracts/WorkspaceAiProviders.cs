namespace Orkka.Modules.Agents.Contracts;

/// <summary>
/// The workspace "AI provider" setting (Settings → Workspace): which vendor's CLI the workspace's
/// teams are seeded on and system-picked work prefers. Keys are the runner's CLI keys, so the UI
/// pairs each choice with that CLI's readiness row without a mapping of its own. Stored on the
/// organization row (<c>IWorkspaceAiSettings</c>); null/absent = <see cref="Automatic"/>.
/// <para>Only providers with fixed tier models are offered. Ollama and OpenCode are left out on
/// purpose: their models are whatever that machine pulled or logged in to, so a workspace-wide
/// default would be a guess (they stay pickable per agent and with "Set every agent's model").</para>
/// </summary>
public static class WorkspaceAiProviders
{
    /// <summary>Claude when it can run, else the first signed-in provider — the behaviour before
    /// the setting existed, and the default.</summary>
    public const string Automatic = "automatic";

    /// <summary>Every accepted value, Automatic first, then the CLIs in preference order.</summary>
    public static readonly IReadOnlyList<string> Choices =
        [Automatic, "claude", "codex", "copilot", "gemini", "grok"];

    /// <summary>The provider a key names; null for Automatic, empty or unknown keys.</summary>
    public static AiProvider? ToProvider(string? key) => key?.Trim().ToLowerInvariant() switch
    {
        "claude" => AiProvider.Anthropic,
        "codex" => AiProvider.OpenAI,
        "copilot" => AiProvider.GitHubCopilot,
        "gemini" => AiProvider.Google,
        "grok" => AiProvider.XAI,
        _ => null,
    };

    /// <summary>The runner CLI key of a provider ("claude", "codex", ...).</summary>
    public static string CliKey(AiProvider provider) => provider switch
    {
        AiProvider.OpenAI => "codex",
        AiProvider.Google => "gemini",
        AiProvider.GitHubCopilot => "copilot",
        AiProvider.Ollama => "ollama",
        AiProvider.XAI => "grok",
        AiProvider.OpenCode => "opencode",
        _ => "claude",
    };

    /// <summary>A request's value in canonical form: lower-case key, or null when it is not a
    /// choice. An empty value means Automatic.</summary>
    public static string? Normalize(string? value)
    {
        var key = string.IsNullOrWhiteSpace(value) ? Automatic : value.Trim().ToLowerInvariant();
        return Choices.Contains(key) ? key : null;
    }

    /// <summary>The stored form: null for Automatic, else the key.</summary>
    public static string? ToStored(string normalizedKey)
        => normalizedKey == Automatic ? null : normalizedKey;

    /// <summary>The model a role gets on <paramref name="provider"/> — the seeding rule, shared by
    /// seeding and the bulk "switch existing projects" so the two never disagree: PM the Fast
    /// tier, everyone else the Capable tier. (QA is seeded on <see cref="ModelCatalogSnapshot.QaModel"/>
    /// instead and a bulk switch skips it whenever the target cannot drive browser tools.)</summary>
    public static string ModelForRole(AgentRole role, AiProvider provider, ModelCatalogSnapshot catalog)
        => role == AgentRole.PM ? catalog.FastModel(provider) : catalog.CapableModel(provider);
}

/// <summary>What seeding resolved for a workspace: the provider it picked and, when the
/// workspace chose one that cannot run, why not (the blocker is shown on Settings → Workspace).</summary>
/// <param name="Choice">The stored key, or <see cref="WorkspaceAiProviders.Automatic"/>.</param>
/// <param name="Provider">The provider new agents are seeded on.</param>
/// <param name="Blocker">Why the chosen provider was not used; null when it was (or for Automatic).</param>
public sealed record WorkspaceProviderResolution(string Choice, AiProvider Provider, string? Blocker);

/// <summary>
/// The workspace-aware <see cref="ProviderReadinessExtensions.PreferredProvider(IProviderReadiness, AiProvider?)"/>:
/// reads the workspace's choice and its developer's runner readiness. Seeding and the roster
/// backfill call <see cref="ResolveAsync"/>; work that already holds a route readiness (the merge
/// resolver) only needs <see cref="GetChosenAsync"/>.
/// </summary>
public interface IWorkspaceAiProviderResolver
{
    /// <summary>The provider the workspace chose, or null for Automatic.</summary>
    Task<AiProvider?> GetChosenAsync(Ulid orgId, CancellationToken ct = default);

    /// <summary>The provider new agents of the workspace get. <paramref name="userId"/> is whose
    /// runner to read (the project's creator); null reads the workspace's runner, if any.</summary>
    Task<WorkspaceProviderResolution> ResolveAsync(Ulid orgId, string? userId, CancellationToken ct = default);
}
