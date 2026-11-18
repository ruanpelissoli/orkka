namespace Orkka.Git;

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

/// <summary>
/// One git command to run. Arguments are a list, never a string to be re-parsed by the
/// shell or by <c>CreateProcess</c> — a branch name or a commit message with a quote in it
/// is passed through intact.
/// </summary>
/// <param name="Identity">The author/committer for any commit this command creates. When the
/// caller passes none the developer's own git config decides.</param>
/// <param name="Timeout">Wall-clock ceiling; the process tree is killed past it and the result
/// carries <see cref="GitErrorCode.Timeout"/>. Null means <see cref="GitProcess.DefaultTimeout"/>.</param>
public sealed record GitCommand(
    string WorkingDirectory,
    IReadOnlyList<string> Arguments,
    GitIdentity? Identity = null,
    TimeSpan? Timeout = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    // A yes/no question whose "no" is a non-zero exit (`rev-parse --verify --quiet`,
    // `config --get` of a key that may be absent). "No" is an answer, not a failure: it is
    // not logged as one and does not become the ledger's "last failed command".
    bool Probe = false)
{
    public GitCommand(string workingDirectory, params string[] arguments)
        : this(workingDirectory, (IReadOnlyList<string>)arguments) { }

    /// <summary>The command line as it may be logged: arguments joined, URL credentials
    /// replaced.</summary>
    public string Redacted() => GitUrlRedactor.Redact(string.Join(' ', Arguments.Select(QuoteForDisplay)));

    private static string QuoteForDisplay(string arg) =>
        arg.Length == 0 || arg.Any(char.IsWhiteSpace) ? $"\"{arg}\"" : arg;
}

/// <summary>
/// The only way Orkka starts a git process.
///
/// Git only runs on the developer's machine (the runner), and every command there goes
/// through here: no prompts, a deadline, redacted logging, one classification of what went
/// wrong, and a record in the operation ledger.
///
/// Git authenticates with the developer's own setup — credential manager, SSH agent and keys,
/// <c>~/.ssh/config</c> host aliases — and commits as whoever their git config says (or the
/// identity their runner workspace was given), because the developer is the one doing the
/// work, on repositories only their account can reach. Orkka never hands git a credential.
/// </summary>
public sealed class GitProcess(ILogger<GitProcess> logger)
{
    /// <summary>The executable, resolved through PATH at spawn time. The runner puts its
    /// bundled git on the PATH of its children when the machine has none.</summary>
    public static string Executable { get; set; } = "git";

    /// <summary>Generous enough for a large clone on a slow link; a git that runs longer is
    /// not going to finish. Callers with tighter budgets (probes) pass their own.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(15);

    /// <summary>Runs a command written as one line. Double quotes group, <c>\"</c> inside a
    /// group is a literal quote. Exists for the large body of callers written against a
    /// string API; new code passes a list.</summary>
    public Task<GitCommandResult> RunAsync(
        string workingDirectory, string argumentLine, CancellationToken ct,
        GitIdentity? identity = null, TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? environment = null)
        => RunAsync(new GitCommand(workingDirectory, GitArgumentLine.Split(argumentLine),
            identity, timeout, environment), ct);

    public async Task<GitCommandResult> RunAsync(GitCommand command, CancellationToken ct)
    {
        var redacted = command.Redacted();
        logger.LogDebug("git {Arguments} in {WorkingDirectory}", redacted, command.WorkingDirectory);

        var result = await SpawnAsync(command, ct);

        if (result.Succeeded || !command.Probe)
            GitOperationScope.Active?.Observe(result, redacted);

        if (!result.Succeeded && !command.Probe)
        {
            logger.LogWarning("git {Arguments} failed ({Code}, exit {ExitCode}): {Error}",
                redacted, result.Code, result.ExitCode, result.Error);
        }

        return result;
    }

    private async Task<GitCommandResult> SpawnAsync(GitCommand command, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(Executable)
        {
            WorkingDirectory = command.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in command.Arguments)
            psi.ArgumentList.Add(arg);

        ApplyEnvironment(psi, command);

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Win32Exception ex)
        {
            return GitCommandResult.Failed(GitErrorCode.GitMissing,
                $"git could not be started ({ex.Message}). Install git or re-run the Orkka installer.");
        }

        if (process is null)
            return GitCommandResult.Failed(GitErrorCode.GitMissing, "git could not be started.");

        using (process)
        {
            // Nothing is ever fed to git; closing stdin turns any read into EOF instead of a hang.
            process.StandardInput.Close();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(command.Timeout ?? DefaultTimeout);

            try
            {
                await process.WaitForExitAsync(deadline.Token);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                if (ct.IsCancellationRequested)
                    throw;

                var budget = command.Timeout ?? DefaultTimeout;
                return GitCommandResult.Failed(GitErrorCode.Timeout,
                    $"git {FirstWord(command)} did not finish within {budget.TotalSeconds:0}s and was stopped.");
            }

            var stdout = await stdoutTask;
            var stderr = GitUrlRedactor.Redact((await stderrTask).Trim());
            var code = GitErrorClassifier.Classify(process.ExitCode, stderr);
            return new GitCommandResult(process.ExitCode, stdout, stderr, code);
        }
    }

    /// <summary>
    /// The environment every git Orkka spawns runs with. Prompts are impossible; the identity
    /// is the command's own; everything else is inherited — including the
    /// <c>GIT_CONFIG_*</c> entries the runner's bundled git sets up, which are appended to, not
    /// replaced.
    /// </summary>
    internal static void ApplyEnvironment(ProcessStartInfo psi, GitCommand command)
    {
        var env = psi.Environment;

        // Never interactive: a credential prompt or an LFS smudge dialog would hang the
        // process forever, and a failed command is the answer we want instead.
        env["GIT_TERMINAL_PROMPT"] = "0";
        env["GCM_INTERACTIVE"] = "never";
        env["GIT_ASKPASS"] = "";
        env["SSH_ASKPASS"] = "";

        if (command.Environment is not null)
            foreach (var (key, value) in command.Environment)
                env[key] = value;

        if (command.Identity is { } identity)
            foreach (var (key, value) in identity.EnvironmentEntries())
                env[key] = value;

        var configEntries = new List<(string Key, string Value)>();

        // Windows git refuses a path past 260 characters unless told otherwise: "Filename too
        // long" from a clone's temporary pack name under a deep worktree, or from a checkout
        // with a node_modules tree. The \\?\ form is the fix git itself documents, and every
        // command this engine runs on a user's PC gets it.
        if (OperatingSystem.IsWindows())
            configEntries.Add(("core.longpaths", "true"));

        AppendConfig(env, configEntries);
    }

    /// <summary>Appends <c>GIT_CONFIG_KEY_n/VALUE_n</c> pairs after any the process already
    /// carries. These take precedence over every config file.</summary>
    internal static void AppendConfig(
        IDictionary<string, string?> env, IReadOnlyList<(string Key, string Value)> entries)
    {
        var count = 0;
        if (env.TryGetValue("GIT_CONFIG_COUNT", out var existing)
            && int.TryParse(existing, out var parsed) && parsed > 0)
        {
            count = parsed;
        }

        foreach (var (key, value) in entries)
        {
            env[$"GIT_CONFIG_KEY_{count}"] = key;
            env[$"GIT_CONFIG_VALUE_{count}"] = value;
            count++;
        }

        env["GIT_CONFIG_COUNT"] = count.ToString();
    }

    private static void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch { /* already gone */ }
    }

    private static string FirstWord(GitCommand command) =>
        command.Arguments.FirstOrDefault(a => !a.StartsWith('-')) ?? "";
}

/// <summary>Splits a one-line git invocation into arguments the way the old string-based
/// callers meant it: whitespace separates, double quotes group, <c>\"</c> inside quotes is a
/// literal quote, an empty <c>""</c> is an empty argument.</summary>
public static class GitArgumentLine
{
    public static IReadOnlyList<string> Split(string line)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (inQuotes)
            {
                if (c == '\\' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    inQuotes = false;
                }
                else
                {
                    current.Append(c);
                }
                continue;
            }

            if (c == '"')
            {
                inQuotes = true;
                hasToken = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (hasToken)
                {
                    args.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
            }
            else
            {
                current.Append(c);
                hasToken = true;
            }
        }

        if (hasToken)
            args.Add(current.ToString());

        return args;
    }
}
