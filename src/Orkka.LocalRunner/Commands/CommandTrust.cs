namespace Orkka.LocalRunner.Commands;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Orkka.LocalRunner.Contracts;

/// <summary>
/// Decides whether a shell command the host asks this machine to run — a repository's setup,
/// check or build command, or a run target's start/install/stop command — may run.
///
/// The host is not trusted to introduce commands. A command runs only when one of two things
/// that the DEVELOPER controls vouches for it:
/// <list type="number">
/// <item>The repository declares it in <c>orkka.json</c> on its default branch
/// (<c>origin/HEAD</c>): reviewed and merged by the team, the same trust as the code itself.
/// A task branch never counts — an agent wrote that, and the platform pushed it.</item>
/// <item>The developer approved this exact command for this repository on this machine
/// (<c>./orkka commands approve &lt;id&gt;</c>).</item>
/// </list>
/// Anything else is refused and recorded as pending, with the id the developer approves it by.
/// So a compromised Orkka account or server can ask, but cannot make a developer's machine run
/// something new.
/// </summary>
public sealed class CommandTrust(
    CommandApprovalStore store, Orkka.Git.GitProcess git, ILogger<CommandTrust> logger)
{
    /// <summary>The verdict, and when refused, what the developer is told.</summary>
    public sealed record Verdict(bool Allowed, CommandApproval? Pending)
    {
        public static readonly Verdict Allow = new(true, null);
    }

    /// <summary>
    /// Checks <paramref name="command"/> against the repository at <paramref name="repoDir"/>
    /// (a clone or the developer's own folder — its <c>origin/HEAD</c> is read) or, for a bare
    /// mirror, its <c>HEAD</c>.
    /// </summary>
    public async Task<Verdict> CheckAsync(
        string repoDir, string command, CommandKind kind, string workspace, CancellationToken ct = default)
    {
        var normalized = Normalize(command);
        if (normalized.Length == 0)
            return Verdict.Allow;

        var repoKey = await RepoKeyAsync(repoDir, ct);
        if ((await DeclaredCommandsAsync(repoDir, ct)).Contains(normalized))
            return Verdict.Allow;

        if (store.IsApproved(repoKey, normalized))
            return Verdict.Allow;

        var pending = store.RecordPending(repoKey, normalized, kind, workspace);
        logger.LogWarning(
            "Refused an unapproved {Kind} command for {Repo} — approve with `./orkka commands approve {Id}`: {Command}",
            kind, repoKey, pending.Id, normalized);
        return new Verdict(false, pending);
    }

    /// <summary>One line per command, line endings unified, outer whitespace dropped — so a
    /// command typed in the UI and the same one in orkka.json compare equal.</summary>
    public static string Normalize(string command) =>
        command.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    /// <summary>
    /// Every command <c>orkka.json</c> declares on the repository's default branch: the
    /// top-level <c>setup</c>, <c>check</c> and <c>build</c>, and each run target's
    /// <c>command</c>, <c>installCommand</c> and <c>stopCommand</c>. Empty when there is no
    /// such file or it does not parse — never an exception.
    /// </summary>
    internal async Task<HashSet<string>> DeclaredCommandsAsync(string repoDir, CancellationToken ct)
    {
        var json = await ShowAsync(repoDir, "origin/HEAD:orkka.json", ct)
                   ?? (IsBare(repoDir) ? await ShowAsync(repoDir, "HEAD:orkka.json", ct) : null);
        return ParseDeclared(json);
    }

    internal static HashSet<string> ParseDeclared(string? json)
    {
        var declared = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json))
            return declared;

        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return declared;

            foreach (var key in ManifestCommandKeys)
                Add(root, key);

            if (root.TryGetProperty("run", out var run) && run.ValueKind == JsonValueKind.Object
                && run.TryGetProperty("targets", out var targets) && targets.ValueKind == JsonValueKind.Array)
            {
                foreach (var target in targets.EnumerateArray())
                {
                    if (target.ValueKind != JsonValueKind.Object)
                        continue;
                    Add(target, "command");
                    Add(target, "installCommand");
                    Add(target, "stopCommand");
                }
            }
        }
        catch (JsonException)
        {
            // A broken manifest vouches for nothing.
        }

        return declared;

        void Add(JsonElement obj, string name)
        {
            if (obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                && Normalize(value.GetString() ?? "") is { Length: > 0 } cmd)
                declared.Add(cmd);
        }
    }

    /// <summary>The top-level orkka.json keys that hold a repository command.</summary>
    public static readonly string[] ManifestCommandKeys = ["setup", "check", "build"];

    /// <summary>
    /// What identifies a repository across its clones on this machine: the origin URL with any
    /// credentials stripped (every task clone of one remote shares approvals), or the folder
    /// path when there is no remote.
    /// </summary>
    internal async Task<string> RepoKeyAsync(string repoDir, CancellationToken ct)
    {
        var origin = await GitAsync(repoDir, "config --get remote.origin.url", ct);
        if (!string.IsNullOrWhiteSpace(origin))
            return Orkka.Git.GitUrlRedactor.Redact(origin.Trim());
        return Path.GetFullPath(repoDir).TrimEnd('/', '\\');
    }

    private static bool IsBare(string repoDir) =>
        !Directory.Exists(Path.Combine(repoDir, ".git")) && !File.Exists(Path.Combine(repoDir, ".git"))
        && File.Exists(Path.Combine(repoDir, "HEAD"));

    private Task<string?> ShowAsync(string repoDir, string spec, CancellationToken ct) =>
        GitAsync(repoDir, $"show {spec}", ct);

    private async Task<string?> GitAsync(string repoDir, string arguments, CancellationToken ct)
    {
        try
        {
            var result = await git.RunAsync(new Orkka.Git.GitCommand(
                repoDir, Orkka.Git.GitArgumentLine.Split(arguments),
                Timeout: TimeSpan.FromSeconds(20)), ct);
            return result.Succeeded ? result.Stdout : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>The short id a pending command is approved by: stable for one (repo, command).</summary>
    public static string IdFor(string repoKey, string command)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(repoKey + "\n" + Normalize(command)));
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }
}
