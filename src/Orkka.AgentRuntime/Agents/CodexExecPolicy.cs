namespace Orkka.Modules.Execution.Agents;

using System.Text;

/// <summary>
/// Orkka's command guard, written as Codex execpolicy rules into the task's worktree.
///
/// The codex CLI has no PreToolUse hook to call back into, but it loads every
/// <c>.codex/rules/*.rules</c> file under its working root and refuses a command whose argv
/// starts with a <c>forbidden</c> prefix — through the shell wrapper too (verified against codex
/// 0.163: <c>pwsh -Command 'git log -1'</c> was "rejected" by a <c>["git","log"]</c> rule). So
/// the same prefixes <see cref="AgentBashGuard.CommandPrefixRules"/> derives for every
/// deny-list CLI land here, and the file is hidden from git so it never reaches a commit.
/// </summary>
public static class CodexExecPolicy
{
    /// <summary>Relative to the worktree root.</summary>
    public const string RulesPath = ".codex/rules/orkka.rules";

    /// <summary>Writes the rules file and excludes it from git. Throws on I/O failure: a run
    /// that cannot be guarded must not start unguarded.</summary>
    public static void WriteInto(string worktreePath)
    {
        var path = Path.Combine(worktreePath, ".codex", "rules", "orkka.rules");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Render(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        ExcludeFromGit(worktreePath);
    }

    /// <summary>The rules file's content (Starlark).</summary>
    public static string Render()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Written by Orkka for this task's agent run. Not part of the repository.");
        foreach (var rule in AgentBashGuard.CommandPrefixRules)
        {
            var pattern = string.Join(", ", rule.Select(Quote));
            sb.Append("prefix_rule(pattern = [").Append(pattern).Append("], decision = \"forbidden\", ")
              .Append("justification = \"Blocked by Orkka on the developer's machine: credentials, pushes, ")
              .Append("publishing, other hosts and cloud/forge CLIs are off limits. Orkka pushes and ")
              .AppendLine("opens the pull request itself.\")");
        }

        return sb.ToString();
    }

    private static string Quote(string token) =>
        "\"" + token.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>
    /// Adds the rules path to the clone's <c>info/exclude</c>, so <c>git add -A</c> never stages
    /// it. A standalone clone keeps it in <c>.git/info/exclude</c>; a legacy linked worktree's
    /// <c>.git</c> is a file pointing at <c>{common}/worktrees/{name}</c>, whose shared
    /// <c>info/exclude</c> lives two levels up.
    /// </summary>
    internal static void ExcludeFromGit(string worktreePath)
    {
        var dotGit = Path.Combine(worktreePath, ".git");
        string? infoDir = null;
        if (Directory.Exists(dotGit))
        {
            infoDir = Path.Combine(dotGit, "info");
        }
        else if (File.Exists(dotGit))
        {
            var line = File.ReadAllText(dotGit).Trim();
            if (line.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
            {
                var gitDir = line["gitdir:".Length..].Trim();
                if (!Path.IsPathRooted(gitDir))
                    gitDir = Path.GetFullPath(Path.Combine(worktreePath, gitDir));
                var common = Path.GetFullPath(Path.Combine(gitDir, "..", ".."));
                infoDir = Path.Combine(common, "info");
            }
        }

        if (infoDir is null)
            return;

        Directory.CreateDirectory(infoDir);
        var exclude = Path.Combine(infoDir, "exclude");
        var existing = File.Exists(exclude) ? File.ReadAllText(exclude) : "";
        if (existing.Split('\n').Any(l => l.Trim() == "/" + RulesPath))
            return;

        var prefix = existing.Length > 0 && !existing.EndsWith('\n') ? "\n" : "";
        File.AppendAllText(exclude, prefix + "/" + RulesPath + "\n");
    }
}
