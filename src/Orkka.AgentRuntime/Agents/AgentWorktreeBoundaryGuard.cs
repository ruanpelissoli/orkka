namespace Orkka.Modules.Execution.Agents;

using System.Text.Json;
using ih0.Claude.Agent.SDK.Types;
using Microsoft.Extensions.Logging;

/// <summary>
/// Pre-execution hook that keeps the agent's file tools inside the task's worktree.
///
/// An agent's whole output is its branch: whatever it writes outside the worktree is never
/// committed, never diffed, never reviewed — and on a user's own machine it is a stray write
/// to their filesystem. It happens in practice: a repo whose Dockerfile says
/// <c>WORKDIR /home/user/api</c> had an agent Write to <c>/home/user/api/docs/…</c> (on
/// Windows that landed in <c>C:\home\user\api</c>), then report "done". The live diff showed
/// nothing, the agent believed it had finished, and a human had to notice.
///
/// Scope: the tools whose input names a file — Edit, Write, MultiEdit, NotebookEdit. A path is
/// resolved against the hook's <c>cwd</c> (the CLI's working directory, i.e. the worktree as
/// the CLI sees it) and must land under that directory or under the worktree path the run was
/// given — the two differ only when a sandbox mounts the worktree elsewhere, and either is the
/// same tree. <c>..</c> and mixed separators are normalised before the check; <c>~</c> is never
/// inside a worktree. Bash is covered separately by the Bash guard; shell redirects cannot be
/// reasoned about from the command text with the same certainty, and a wrong deny there would
/// break builds.
///
/// Denying is survivable: the reason tells the agent exactly which root to write under, so the
/// next attempt lands in the repo instead of the run failing later for lack of a diff.
/// </summary>
public sealed class AgentWorktreeBoundaryGuard
{
    private readonly string _worktreePath;
    private readonly ILogger _logger;
    private readonly Func<string, string, Task>? _onBlocked;
    private readonly bool _ignoreCase;

    /// <param name="worktreePath">The run's worktree — the one root that is always allowed.</param>
    /// <param name="onBlocked">Audit callback: (tool, path).</param>
    /// <param name="ignoreCase">Path comparison mode; defaults to the host's convention.</param>
    public AgentWorktreeBoundaryGuard(
        string worktreePath,
        ILogger logger,
        Func<string, string, Task>? onBlocked = null,
        bool? ignoreCase = null)
    {
        _worktreePath = worktreePath;
        _logger = logger;
        _onBlocked = onBlocked;
        _ignoreCase = ignoreCase ?? OperatingSystem.IsWindows();
    }

    /// <summary>The tools this guard must be registered for (a hook matcher regex).</summary>
    public const string ToolMatcher = "Edit|Write|MultiEdit|NotebookEdit";

    public async Task<HookOutput> Evaluate(HookInput input, string? toolUseId, HookContext? context)
    {
        if (input is not PreToolUseHookInput pre)
            return AgentHookResponses.Allow();

        var path = ExtractPath(pre.ToolInput);
        if (string.IsNullOrWhiteSpace(path))
            return AgentHookResponses.Allow();

        var cwd = string.IsNullOrWhiteSpace(pre.Cwd) ? _worktreePath : pre.Cwd;
        var roots = new[] { cwd, _worktreePath }
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(Normalize)
            .Distinct(_ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToArray();
        if (roots.Length == 0)
            return AgentHookResponses.Allow();

        var resolved = Resolve(path, cwd);
        if (roots.Any(r => IsUnder(resolved, r)))
            return AgentHookResponses.Allow();

        _logger.LogWarning(
            "Blocked {Tool} outside the worktree: {Path} resolves to {Resolved}, worktree {Root}",
            pre.ToolName, path, resolved, roots[0]);
        if (_onBlocked is not null)
            await _onBlocked(pre.ToolName ?? "file", path);

        return AgentHookResponses.Deny(
            $"'{path}' is outside the task's worktree ({roots[0]}). Every file for this task " +
            "must live under the repository root, which is your current working directory — " +
            "anything written elsewhere is not on the task's branch, is never committed or " +
            "reviewed, and on a developer's machine is a stray write to their disk. Use a path " +
            "relative to the repository root (for example docs/NOTES.md), or that root's absolute " +
            "path; do not use paths from Dockerfiles, READMEs or other environments.");
    }

    private static string ExtractPath(JsonElement toolInput)
    {
        if (toolInput.ValueKind != JsonValueKind.Object) return "";
        if (toolInput.TryGetProperty("file_path", out var p) && p.ValueKind == JsonValueKind.String)
            return p.GetString() ?? "";
        if (toolInput.TryGetProperty("notebook_path", out p) && p.ValueKind == JsonValueKind.String)
            return p.GetString() ?? "";
        return "";
    }

    /// <summary>Absolute, normalised form of <paramref name="path"/> as seen from <paramref name="cwd"/>.</summary>
    internal static string Resolve(string path, string cwd)
    {
        var trimmed = path.Trim();
        if (trimmed.StartsWith('~'))
            return Normalize(trimmed); // never inside a worktree; keep it recognisable in the deny
        return IsRooted(trimmed) ? Normalize(trimmed) : Normalize(cwd.TrimEnd('/', '\\') + "/" + trimmed);
    }

    private static bool IsRooted(string p) =>
        p.StartsWith('/') || p.StartsWith('\\') || (p.Length >= 2 && char.IsAsciiLetter(p[0]) && p[1] == ':');

    /// <summary>
    /// Forward slashes, drive letter upper-cased, <c>.</c>/<c>..</c> collapsed, no trailing slash.
    /// OS-agnostic on purpose: the worker may evaluate a path written for a sandbox with a
    /// different convention, and tests must not depend on the machine running them.
    /// </summary>
    public static string Normalize(string p)
    {
        var s = p.Trim().Replace('\\', '/');
        var drive = "";
        if (s.Length >= 2 && char.IsAsciiLetter(s[0]) && s[1] == ':')
        {
            drive = char.ToUpperInvariant(s[0]) + ":";
            s = s[2..];
        }
        var unc = s.StartsWith("//");
        var parts = new List<string>();
        foreach (var seg in s.Split('/'))
        {
            if (seg.Length == 0 || seg == ".") continue;
            if (seg == "..")
            {
                if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(seg);
        }
        var body = string.Join("/", parts);
        if (unc) return "//" + body;
        return drive + "/" + body;
    }

    private bool IsUnder(string full, string root)
    {
        var cmp = _ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (full.Equals(root, cmp)) return true;
        var prefix = root.EndsWith('/') ? root : root + "/";
        return full.StartsWith(prefix, cmp);
    }
}
