namespace Orkka.Tests.Worker;

using System.Diagnostics;

/// <summary>
/// A throwaway process with a child of its own, standing in for an agent CLI and the shells /
/// MCP servers it starts. <see cref="ChildAlive"/> tells whether the CHILD still runs — the part
/// a plain <c>Process.Kill()</c> leaves behind. Windows: the child holds a file open, so the
/// file can be deleted only once it is gone. Elsewhere: the script writes the child's pid.
/// </summary>
internal sealed class ProcessTreeProbe : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("orkka-tree-").FullName;

    /// <summary>A script usable as a CLI path: answers <c>-v</c> (the SDK's version check)
    /// with a version and exits; otherwise starts a long-lived child and waits for it.</summary>
    public string Script { get; }

    private string Held => Path.Combine(_dir, "held.txt");
    private string ChildPidFile => Path.Combine(_dir, "child.pid");

    public ProcessTreeProbe()
    {
        if (OperatingSystem.IsWindows())
        {
            Script = Path.Combine(_dir, "fake-cli.cmd");
            File.WriteAllText(Script,
                "@echo off\r\n" +
                "if \"%1\"==\"-v\" (echo 2.1.0& exit /b 0)\r\n" +
                $"ping -n 120 127.0.0.1 > \"{Held}\"\r\n");
        }
        else
        {
            Script = Path.Combine(_dir, "fake-cli.sh");
            File.WriteAllText(Script,
                "#!/bin/sh\n" +
                "if [ \"$1\" = \"-v\" ]; then echo 2.1.0; exit 0; fi\n" +
                $"sleep 120 & echo $! > '{ChildPidFile}'\n" +
                "wait\n");
            File.SetUnixFileMode(Script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>Starts the script directly (what CliAgentInvokerBase does with a CLI).</summary>
    public Process Start()
    {
        var process = Process.Start(new ProcessStartInfo(Script)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        })!;
        return process;
    }

    /// <summary>Waits until the child is up (holds its file / wrote its pid).</summary>
    public async Task WaitForChildAsync()
    {
        var until = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < until)
        {
            if (OperatingSystem.IsWindows() ? File.Exists(Held) : File.Exists(ChildPidFile) && File.ReadAllText(ChildPidFile).Trim().Length > 0)
                return;
            await Task.Delay(100);
        }
        throw new TimeoutException("the probe's child never started");
    }

    /// <summary>True while the child process is still running (polls up to <paramref name="settle"/>
    /// for it to go away).</summary>
    public async Task<bool> ChildAliveAsync(TimeSpan settle)
    {
        var until = DateTime.UtcNow + settle;
        do
        {
            if (!ChildRunningNow())
                return false;
            await Task.Delay(200);
        }
        while (DateTime.UtcNow < until);
        return true;
    }

    private bool ChildRunningNow()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                File.Delete(Held);
                return false;
            }
            catch (IOException)
            {
                return true; // still held open by the child
            }
        }
        var pid = int.Parse(File.ReadAllText(ChildPidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            using var child = Process.GetProcessById(pid);
            return !child.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* a stuck child keeps it; the OS temp cleaner will */ }
    }
}
