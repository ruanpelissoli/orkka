namespace Orkka.Modules.Execution.Agents;

/// <summary>
/// Opt-in OS-level sandbox for the in-process agent runner (P2 migration step M0). When enabled,
/// the Claude Agent SDK runs the agent under an OS sandbox that restricts filesystem access and can
/// force network traffic through a filtering proxy — defense-in-depth <i>inside</i> whatever host or
/// container the run executes on. This is NOT a tenant boundary on its own (see roadmap P2); it
/// complements container-per-run isolation.
///
/// Default OFF: the OS sandbox depends on host support (Linux bubblewrap/landlock) and — for network
/// restriction — on an egress proxy that does not exist until migration step M3. Enable per
/// environment only after validating that Anthropic connectivity still works.
/// </summary>
public sealed class AgentSandboxOptions
{
    public const string SectionName = "AgentSandbox";

    /// <summary>Master switch for the SDK OS sandbox.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// HTTP proxy port the sandbox should route egress through (the egress-allowlist proxy). When
    /// null, no proxy is configured — enable network restriction only once the proxy exists (M3).
    /// </summary>
    public int? HttpProxyPort { get; set; }

    /// <summary>SOCKS proxy port, if the egress proxy is SOCKS rather than HTTP.</summary>
    public int? SocksProxyPort { get; set; }

    /// <summary>Allow the agent to bind local ports (needed for QA/Playwright local dev servers).</summary>
    public bool AllowLocalBinding { get; set; } = true;
}
