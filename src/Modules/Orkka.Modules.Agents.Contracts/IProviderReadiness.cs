namespace Orkka.Modules.Agents.Contracts;

/// <summary>
/// Which AI providers can run right now. The hosted edition is always ready (Orkka's own
/// Anthropic key); Orkka Local replaces this with a runner-backed implementation so every
/// consumer (task submit pre-flight, merge resolver, agent seeding, utility fallback)
/// adapts to what the user actually has connected — without local-mode flags in shared code.
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
    /// <summary>The provider system-picked work should run on: Claude when it can run
    /// (hosted always → byte-identical behavior), else the first connected provider,
    /// else Anthropic as a passthrough — callers' own pre-flights surface the blocker.</summary>
    public static AiProvider PreferredProvider(this IProviderReadiness readiness)
        => readiness.GetBlocker(AiProvider.Anthropic) is null
            ? AiProvider.Anthropic
            : readiness.Connected is { Count: > 0 } connected
                ? connected[0]
                : AiProvider.Anthropic;
}
