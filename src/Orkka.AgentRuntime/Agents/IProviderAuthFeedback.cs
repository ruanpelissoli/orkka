namespace Orkka.Modules.Execution.Agents;

/// <summary>
/// Where an invoker reports what a run taught it about the provider's sign-in state on
/// THIS machine. Static credential probes cannot see everything — Copilot and Grok may
/// keep their tokens only in the OS credential store, a Claude refresh can be lost to a
/// parallel-run race between two probes — so the run itself is the authoritative signal:
/// an auth-shaped failure proves the provider is NOT usable right now, and a clean
/// completion proves it is. The local runner feeds these into its readiness reports
/// (<c>ProviderAuthOverrides</c>) so the status panel, the model pickers and the run
/// blocker flip within seconds instead of at the next structural probe. Hosts that own no
/// readiness state (the hosted Worker) leave the no-op default.
/// </summary>
public interface IProviderAuthFeedback
{
    /// <summary>A run failed with an auth-shaped error (see <see cref="CliAuthFailure"/>).
    /// <paramref name="detail"/> is the user-facing message already carrying the remedy.</summary>
    void ReportAuthFailure(AiProvider provider, string detail);

    /// <summary>A run completed cleanly — whatever the probe says, the credentials work.</summary>
    void ReportSuccess(AiProvider provider);
}

/// <summary>Default for hosts with nothing to update. A singleton rather than null so
/// invokers never branch on the feedback's presence.</summary>
public sealed class NoOpProviderAuthFeedback : IProviderAuthFeedback
{
    public static readonly NoOpProviderAuthFeedback Instance = new();

    private NoOpProviderAuthFeedback() { }

    public void ReportAuthFailure(AiProvider provider, string detail) { }

    public void ReportSuccess(AiProvider provider) { }
}
