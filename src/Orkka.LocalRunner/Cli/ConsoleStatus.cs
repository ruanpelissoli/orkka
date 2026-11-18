namespace Orkka.LocalRunner.Cli;

/// <summary>
/// Friendly connection-status lines for the default (non-debug) terminal. Disabled in
/// debug mode, where the structured logs already narrate the same events. The first
/// successful connection prints the "ready" block — that is the moment the app is
/// actually usable. With several workspaces every line names its workspace, so each job's
/// connection state reads on its own line.
/// </summary>
public sealed class ConsoleStatus(bool enabled, TextWriter? writer = null)
{
    private readonly TextWriter _out = writer ?? Console.Out;
    private readonly Lock _gate = new();
    private readonly HashSet<string> _readyShown = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _states = new(StringComparer.Ordinal);

    /// <summary>The last state each workspace reported ("connected", "reconnecting", …).</summary>
    public IReadOnlyDictionary<string, string> States
    {
        get
        {
            lock (_gate)
                return new Dictionary<string, string>(_states, StringComparer.Ordinal);
        }
    }

    public void Connected(string uiUrl, string? workspace = null)
    {
        lock (_gate)
        {
            _states[workspace ?? ""] = "connected";
            if (!enabled)
                return;
            if (!_readyShown.Add(workspace ?? ""))
            {
                _out.WriteLine(workspace is null ? "Connected." : $"[{workspace}] connected.");
                return;
            }
            _out.WriteLine();
            _out.WriteLine(workspace is null
                ? $"Orkka is ready — open {uiUrl}"
                : $"[{workspace}] Orkka is ready — open {uiUrl}");
            if (_readyShown.Count == 1)
                _out.WriteLine("Press Ctrl+C to stop. Run with --debug for detailed logs.");
            _out.WriteLine();
        }
    }

    public void Reconnecting(string? workspace = null)
        => Report(workspace, "reconnecting", "Connection to Orkka lost — reconnecting...");

    public void ConnectionLost(string? workspace = null)
        => Report(workspace, "closed", "Connection to Orkka closed — retrying...");

    /// <summary>A workspace still dialing (the host is unreachable), shown once until it
    /// connects.</summary>
    public void Unreachable(string hostUrl, string workspace)
    {
        lock (_gate)
        {
            if (_states.GetValueOrDefault(workspace) == "unreachable")
                return;
        }
        Report(workspace, "unreachable", $"Cannot reach {hostUrl} yet — retrying...");
    }

    private void Report(string? workspace, string state, string text)
    {
        lock (_gate)
        {
            _states[workspace ?? ""] = state;
            if (enabled)
                _out.WriteLine(workspace is null ? text : $"[{workspace}] {text}");
        }
    }
}
