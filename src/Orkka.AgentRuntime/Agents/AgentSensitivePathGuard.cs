namespace Orkka.Modules.Execution.Agents;

using System.Text.Json;
using ih0.Claude.Agent.SDK.Types;
using Microsoft.Extensions.Logging;

/// <summary>
/// Pre-execution hook that keeps the agent's READ tools away from the developer's credentials
/// and from every other workspace on the machine.
///
/// Reads are not confined to the worktree, on purpose: an agent legitimately reads a package
/// cache (<c>~/.nuget/packages</c>, <c>~/go/pkg/mod</c>, a global <c>node_modules</c>) to see
/// the API it is coding against, and the CLI writes oversized tool output to its own folder and
/// reads it back. What it must never read is listed in <see cref="DeveloperSecrets"/>: keys,
/// sign-ins, registry tokens, the AI CLIs' logins, browser profiles, and the Orkka home outside
/// this run's worktree — another task's or another employer's code included.
///
/// Writes are already confined to the worktree by <see cref="AgentWorktreeBoundaryGuard"/>;
/// shell lines by <see cref="AgentBashGuard"/>, which consults the same list.
/// </summary>
public sealed class AgentSensitivePathGuard(
    string worktreePath,
    ILogger logger,
    Func<string, string, Task>? onBlocked = null)
{
    /// <summary>The tools this guard must be registered for (a hook matcher regex).</summary>
    public const string ToolMatcher = "Read|Glob|Grep|LS|NotebookRead";

    private readonly string _orkkaHome = DeveloperSecrets.OrkkaHome();

    public async Task<HookOutput> Evaluate(HookInput input, string? toolUseId, HookContext? context)
    {
        if (input is not PreToolUseHookInput pre)
            return AgentHookResponses.Allow();

        var cwd = string.IsNullOrWhiteSpace(pre.Cwd) ? worktreePath : pre.Cwd;
        foreach (var candidate in CandidatePaths(pre.ToolInput))
        {
            var resolved = AgentWorktreeBoundaryGuard.Resolve(ExpandHome(candidate), cwd);
            if (!DeveloperSecrets.IsSensitivePath(resolved, worktreePath, _orkkaHome))
                continue;

            logger.LogWarning(
                "SECURITY: blocked {Tool} of a protected path: {Path} resolves to {Resolved}",
                pre.ToolName, candidate, resolved);
            if (onBlocked is not null)
            {
                try
                {
                    await onBlocked(pre.ToolName ?? "Read", candidate);
                }
                catch
                {
                    // Auditing must never interfere with the guard decision.
                }
            }

            return AgentHookResponses.Deny(
                $"'{candidate}' is a protected location on the developer's machine — credentials, "
                + "sign-ins, browser data, a .env file outside this task's repository, or Orkka's "
                + "own files outside this task's repository. "
                + "Work from the repository (your current working directory) and the public "
                + "documentation of the tools it uses.");
        }

        return AgentHookResponses.Allow();
    }

    /// <summary>
    /// Every path the tool input names. For Glob the pattern itself may be absolute, so its
    /// literal prefix (up to the first wildcard) is checked as well.
    /// </summary>
    internal static IEnumerable<string> CandidatePaths(JsonElement toolInput)
    {
        if (toolInput.ValueKind != JsonValueKind.Object)
            yield break;

        foreach (var name in new[] { "file_path", "notebook_path", "path" })
        {
            if (toolInput.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(p.GetString()))
                yield return p.GetString()!;
        }

        if (toolInput.TryGetProperty("pattern", out var pattern) && pattern.ValueKind == JsonValueKind.String
            && pattern.GetString() is { Length: > 0 } glob)
        {
            var wildcard = glob.IndexOfAny(['*', '?', '[', '{']);
            var prefix = wildcard < 0 ? glob : glob[..wildcard];
            // Only an absolute or home-anchored pattern can leave the worktree; a relative one
            // ("src/**/*.ts", a regex for Grep) is resolved under it by the tool itself.
            if (prefix.StartsWith('/') || prefix.StartsWith('~') || prefix.StartsWith('\\')
                || (prefix.Length >= 2 && char.IsAsciiLetter(prefix[0]) && prefix[1] == ':'))
                yield return prefix.Length == 0 ? "/" : prefix;
        }
    }

    private static string ExpandHome(string path)
    {
        var trimmed = path.Trim();
        if (trimmed == "~" || trimmed.StartsWith("~/", StringComparison.Ordinal) || trimmed.StartsWith("~\\", StringComparison.Ordinal))
            return DeveloperSecrets.UserHome() + trimmed[1..].Replace('\\', '/');
        return trimmed;
    }
}
