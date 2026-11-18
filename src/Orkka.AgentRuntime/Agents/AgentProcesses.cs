namespace Orkka.Modules.Execution.Agents;

using System.Collections.Concurrent;
using System.Diagnostics;

/// <summary>
/// Every AI CLI process this process started for an agent run, chat turn or utility — the Claude
/// SDK's transport and <see cref="CliAgentInvokerBase"/> register theirs on spawn. It is the
/// backstop for stopping Orkka with runs in flight: the runner first cancels its runs and waits
/// for them to tear down, but a run whose output stream nobody is pulling at that moment never
/// sees the cancellation, and a process outlives the async teardown of the process that started
/// it. <see cref="KillAll"/> ends whatever is left, each with its whole tree (the CLI's shells,
/// MCP servers, test runners), so `./orkka stop` and Ctrl+C leave no agent behind (R5, 2026-11-18:
/// claude.exe and its MCP servers outlived a stop).
/// </summary>
public static class AgentProcesses
{
    private static readonly ConcurrentDictionary<int, Process> Live = new();

    /// <summary>Records a started process. Exited ones are dropped as new ones arrive.</summary>
    public static void Track(Process process)
    {
        try
        {
            Live[process.Id] = process;
        }
        catch (InvalidOperationException)
        {
            return; // never started, or already gone
        }
        foreach (var (pid, p) in Live)
        {
            if (HasExited(p))
                Live.TryRemove(pid, out _);
        }
    }

    /// <summary>How many tracked processes are still running.</summary>
    public static int Count => Live.Values.Count(p => !HasExited(p));

    /// <summary>
    /// Kills every tracked process that is still running, with its whole tree, and forgets them
    /// all. Returns how many it had to kill. Never throws.
    /// </summary>
    public static int KillAll()
    {
        var killed = 0;
        foreach (var (pid, process) in Live)
        {
            Live.TryRemove(pid, out _);
            if (HasExited(process))
                continue;
            try
            {
                process.Kill(entireProcessTree: true);
                killed++;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // Exited in between, or not ours to kill any more.
            }
        }
        return killed;
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return true;
        }
    }
}
