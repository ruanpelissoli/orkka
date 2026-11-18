namespace Orkka.LocalRunner.Apps;

using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using Orkka.LocalRunner.Contracts;

/// <summary>
/// Answers "who is holding port N on this machine, and can I have it back".
///
/// .NET can tell us a port is taken (<see cref="IPGlobalProperties"/>) but not by whom, and
/// the PID is the whole point: a stale dev server the user forgot about is worth offering to
/// kill, an unrelated process is not. So the listener lookup shells out to the platform's own
/// tool — `netstat -ano` on Windows, `lsof` on Unix — and degrades to "port is busy, owner
/// unknown" when that fails rather than pretending the port is free.
/// </summary>
internal static class PortInspector
{
    /// <summary>True when something is accepting on the port (any local address).</summary>
    public static bool IsPortInUse(int port)
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Any(endpoint => endpoint.Port == port);
        }
        catch
        {
            // Locked-down environments can refuse the enumeration; treating the port as free
            // means we try to bind and the child process reports the real error itself.
            return false;
        }
    }

    /// <summary>
    /// Waits until something listens on the port, or the timeout passes. Listener presence,
    /// not an HTTP probe: this is used to hold a dependent back until its database or API is
    /// bound, and "accepting connections" is the only readiness signal every kind of service
    /// shares. Returns false on timeout — the caller starts the dependent anyway and lets it
    /// report its own connection error, exactly as before the wait existed.
    /// </summary>
    public static async Task<bool> WaitForPortAsync(int port, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!IsPortInUse(port))
        {
            if (DateTimeOffset.UtcNow >= deadline || ct.IsCancellationRequested)
                return false;
            await Task.Delay(250, CancellationToken.None);
        }
        return true;
    }

    /// <summary>The conflict to report for a busy port, or null when the port is free.</summary>
    public static AppRunPortConflict? FindConflict(int port)
    {
        if (!IsPortInUse(port))
            return null;
        var pid = FindListenerPid(port);
        return new AppRunPortConflict(port, pid, pid is { } id ? ProcessNameOf(id) : null);
    }

    /// <summary>Kills the process holding the port, tree and all. Returns false when the
    /// owner could not be identified or the kill was refused (another user's process,
    /// insufficient rights) — the caller surfaces that instead of starting a target that
    /// would fail to bind anyway.</summary>
    public static bool TryFreePort(int port)
    {
        if (FindListenerPid(port) is not { } pid)
            return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch
        {
            return false;
        }

        // The socket lingers briefly after the owner dies; a target that binds immediately
        // would still hit "address in use".
        for (var attempt = 0; attempt < 20 && IsPortInUse(port); attempt++)
            Thread.Sleep(100);
        return !IsPortInUse(port);
    }

    private static int? FindListenerPid(int port)
    {
        try
        {
            return OperatingSystem.IsWindows()
                ? FindListenerPidWindows(port)
                : FindListenerPidUnix(port);
        }
        catch
        {
            return null;
        }
    }

    private static int? FindListenerPidWindows(int port)
    {
        // Columns: Proto  Local Address  Foreign Address  State  PID
        var output = RunTool("netstat.exe", "-ano -p tcp");
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5 || !parts[3].Equals("LISTENING", StringComparison.OrdinalIgnoreCase))
                continue;
            if (LocalPortOf(parts[1]) == port && int.TryParse(parts[4], out var pid))
                return pid;
        }
        return null;
    }

    private static int? FindListenerPidUnix(int port)
    {
        // -t: pids only, -i: internet files, -sTCP:LISTEN: skip the client side of a
        // connection to the same port, which is not the owner.
        var output = RunTool("lsof", $"-ti tcp:{port} -sTCP:LISTEN");
        foreach (var line in output.Split('\n'))
        {
            if (int.TryParse(line.Trim(), out var pid))
                return pid;
        }
        return null;
    }

    /// <summary>Port half of a netstat local address — IPv6 rows are "[::]:3000", so the
    /// last colon is the only reliable separator.</summary>
    private static int? LocalPortOf(string localAddress)
    {
        var colon = localAddress.LastIndexOf(':');
        return colon >= 0 && int.TryParse(localAddress[(colon + 1)..], out var port) ? port : null;
    }

    private static string ProcessNameOf(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch
        {
            return "unknown";
        }
    }

    private static string RunTool(string fileName, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (process is null)
            return "";
        var output = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(5000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return "";
        }
        return output;
    }

    /// <summary>Convenience for callers that only have a URL: 127.0.0.1 vs localhost vs ::1
    /// are all the same listener as far as the conflict check is concerned.</summary>
    public static bool IsLoopback(string host) =>
        host is "localhost" || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));
}
