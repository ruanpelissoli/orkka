namespace Orkka.LocalRunner.Apps;

using System.Diagnostics;

/// <summary>
/// "Is Docker usable right now?" — asked before an infra target starts, so the user gets
/// "start Docker Desktop" instead of compose's pipe error. Cached briefly: a stack of three
/// infra targets should not shell out three times, but a daemon the user starts between two
/// presses of Run must be noticed on the second.
/// </summary>
internal static class DockerProbe
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(10);
    private static readonly object Gate = new();
    private static (DateTimeOffset At, bool Running)? _last;

    public static bool IsDaemonRunning()
    {
        lock (Gate)
        {
            if (_last is { } cached && DateTimeOffset.UtcNow - cached.At < CacheFor)
                return cached.Running;
        }

        var running = Probe();
        lock (Gate)
            _last = (DateTimeOffset.UtcNow, running);
        return running;
    }

    private static bool Probe()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = "info --format {{.ServerVersion}}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
                return false;
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return false;
            }
            return process.ExitCode == 0;
        }
        catch
        {
            // `docker` not on PATH counts as "not running" — the message the user gets
            // ("start Docker Desktop") is the right one either way.
            return false;
        }
    }
}
