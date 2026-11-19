namespace Orkka.LocalRunner;

using System.Diagnostics;

/// <summary>
/// The GitHub CLI's login, for two purposes only: the copilot CLI authenticates through gh, so
/// "gh is logged in" is "copilot is logged in"; and a GitHub Projects tracker connection may use
/// it (<see cref="TokenAsync"/>) instead of a stored token. Nothing here is ever used as a git
/// credential — git on this machine authenticates through the developer's own git setup — and
/// nothing here logs, stores or returns gh's output except to the caller that asked.
/// </summary>
public static class GhCliCredentials
{
    /// <summary>What <c>gh auth token</c> answered: a token, or why there is none.</summary>
    public sealed class TokenResult(string? token, string? error)
    {
        internal string? Token { get; } = token;
        public string? Error { get; } = error;
        public bool Ok => Token is not null;

        // Never the token, whatever prints this.
        public override string ToString() => Ok ? "gh token (hidden)" : $"no gh token: {Error}";
    }

    /// <summary>One gh run: exit code (-1 = gh is not installed, -2 = it timed out) and stdout.</summary>
    public sealed record GhRun(int ExitCode, string? Stdout);

    /// <summary>
    /// The developer's gh token for <paramref name="host"/> — read-only (<c>gh auth token
    /// --hostname {host}</c>), at call time, never stored. The errors say what to run.
    /// </summary>
    /// <param name="runGh">Test seam: runs gh with the given arguments.</param>
    public static async Task<TokenResult> TokenAsync(string host, CancellationToken ct,
        Func<string[], CancellationToken, Task<GhRun>>? runGh = null)
    {
        runGh ??= RunAsync;
        var run = await runGh(["auth", "token", "--hostname", host], ct);
        if (run.ExitCode == -1)
            return new TokenResult(null,
                $"The GitHub CLI (gh) is not installed on this machine — install it from https://cli.github.com and run `gh auth login -h {host}`, or connect GitHub with a token instead.");
        if (run.ExitCode == -2)
            return new TokenResult(null, $"The GitHub CLI did not answer within {DefaultTimeout.TotalSeconds:0} seconds (`gh auth token -h {host}`).");
        return run.ExitCode == 0 && Normalize(run.Stdout) is { } token
            ? new TokenResult(token, null)
            : new TokenResult(null,
                $"The GitHub CLI is not signed in to {host} — run `gh auth login -h {host}`, then `gh auth refresh -h {host} -s project`.");
    }

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
        var run = await RunAsync(args, CancellationToken.None);
        return run.ExitCode == 0 ? run.Stdout : null;
    }

    private static async Task<GhRun> RunAsync(string[] args, CancellationToken ct)
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
            return new GhRun(-1, null); // gh is not installed.
        }

        if (process is null)
            return new GhRun(-1, null);

        using var _ = process;
        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(DefaultTimeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            ct.ThrowIfCancellationRequested();
            return new GhRun(-2, null);
        }

        var stdout = await stdoutTask;
        await stderrTask; // gh's errors stay here: never shown, they can quote a token's prefix
        return new GhRun(process.ExitCode, stdout);
    }
}
