namespace Orkka.Modules.Agents.Contracts;

/// <summary>
/// Which AI providers can run right now. The module default (<c>AlwaysReadyProviderReadiness</c>)
/// never blocks; each task's route carries a runner-backed one (the developer's last Hello), and
/// agent seeding reads the workspace's runner coverage (<see cref="IWorkspaceAiProviderResolver"/>),
/// so every consumer (task submit pre-flight, merge resolver, agent seeding, utility fallback)
/// adapts to what the developer actually has signed in.
/// </summary>
public interface IProviderReadiness
{
    /// <summary>Null when the provider can run now; otherwise an actionable, user-facing
    /// reason it can't (e.g. "the codex CLI is not logged in on the runner ...").</summary>
    string? GetBlocker(AiProvider provider);

    /// <summary>Providers currently able to run, in preference order (Anthropic first
    /// when available). Hosted: all providers, Anthropic first. Empty when nothing can run.</summary>
    IReadOnlyList<AiProvider> Connected { get; }

    /// <summary>Null when at least one provider can run; otherwise why nothing can
    /// ("runner offline" vs "no CLI logged in"). Hosted: always null.</summary>
    string? GetAnyProviderBlocker();
}

public static class ProviderReadinessExtensions
{
    /// <summary>The provider system-picked work should run on when the workspace is on
    /// Automatic: Claude when it can run (a readiness that never blocks — the module default —
    /// therefore always answers Claude), else the first connected provider, else Anthropic as a
    /// passthrough — callers' own pre-flights surface the blocker.</summary>
    public static AiProvider PreferredProvider(this IProviderReadiness readiness)
        => readiness.GetBlocker(AiProvider.Anthropic) is null
            ? AiProvider.Anthropic
            : readiness.Connected is { Count: > 0 } connected
                ? connected[0]
                : AiProvider.Anthropic;

    /// <summary>Workspace-aware: the workspace's <paramref name="chosen"/> provider (Settings →
    /// Workspace → AI provider) when it can run; otherwise — and for Automatic (null) — exactly
    /// <see cref="PreferredProvider(IProviderReadiness)"/>. A chosen provider that cannot run
    /// never strands work: it falls back, and the caller surfaces the blocker.</summary>
    public static AiProvider PreferredProvider(this IProviderReadiness readiness, AiProvider? chosen)
        => chosen is { } provider && readiness.GetBlocker(provider) is null
            ? provider
            : readiness.PreferredProvider();
}
