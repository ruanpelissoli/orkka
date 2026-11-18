namespace Orkka.LocalRunner;

using System.Diagnostics;

/// <summary>
/// The GitHub CLI's login state, for one purpose only: the copilot CLI authenticates through
/// gh, so "gh is logged in" is "copilot is logged in". Nothing here is ever used as a git
/// credential — git on this machine authenticates through the developer's own git setup.
/// </summary>
public static class GhCliCredentials
{
    /// <summary>`gh auth token` reads a local keyring — near-instant. Anything approaching
    /// this cap is gh itself wedged.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Whether gh holds a github.com login at all. Existence only: the token is never returned
    /// or kept. Synchronous because the caller is the runner's readiness probe, which is sync
    /// all the way down; a missing gh reads as "not logged in".
    /// </summary>
    /// <param name="runGh">Test seam: runs gh with the given arguments and returns stdout, or
    /// null when gh is missing or exited non-zero.</param>
    public static bool IsLoggedIn(Func<string[], Task<string?>>? runGh = null)
    {
        runGh ??= RunGhAsync;
        return Normalize(runGh(["auth", "token", "--hostname", "github.com"]).GetAwaiter().GetResult())
               is not null;
    }

    /// <summary>A credential or nothing: one non-empty line without spaces. gh prints its
    /// errors to stderr, but guard against advice text on stdout ever reading as a token.</summary>
    private static string? Normalize(string? output)
    {
        var line = output?.Trim();
        return string.IsNullOrEmpty(line) || line.Contains(' ') || line.Contains('\n')
            ? null
            : line;
    }

    private static async Task<string?> RunGhAsync(string[] args)
    {
        var psi = new ProcessStartInfo("gh")
        {
            // Irrelevant to `auth token`, but it must exist — the daemon's own cwd may
            // have been deleted.
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // gh is not installed.
        }

        if (process is null)
            return null;

        using var _ = process;
        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(DefaultTimeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return null;
        }

        var stdout = await stdoutTask;
        await stderrTask;
        return process.ExitCode == 0 ? stdout : null;
    }
}
