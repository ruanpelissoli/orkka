namespace Orkka.Modules.Execution.Orchestration;

using System.Text.RegularExpressions;
using Orkka.Modules.Projects.Contracts;

/// <summary>
/// Turns the project's stored run targets into the shell line the QA agent starts the app
/// with, for repos whose owner never typed a <c>QaRunCommand</c>.
///
/// The Run button already knows how to start every app in the project; the QA lane asking
/// for the same information in a separate settings field is why so many projects escalated
/// with "QA needs a run command". Derived here, in bash terms (the QA agent's Bash tool),
/// from the target a tester would drive: the web app when there is one, else the API.
/// </summary>
internal static class QaRunCommandDerivation
{
    /// <summary>The bash line, or null when the repo has no runnable target.</summary>
    public static string? Derive(IReadOnlyList<RunTargetDto>? targets, Ulid repoId)
    {
        var target = PickTarget(targets, repoId);
        if (target is null)
            return null;

        var steps = new List<string>();

        // Supporting services first, detached — the agent's Bash runs one line in the
        // background and a foreground `docker compose up` would swallow the app command.
        foreach (var dependency in InfraDependencies(targets!, target))
            steps.Add(Detach(dependency.Command));

        var cwd = string.IsNullOrWhiteSpace(target.WorkingDirectory) ? null : target.WorkingDirectory.Trim();
        if (cwd is not null)
            steps.Add($"cd {Quote(cwd)}");

        // A fresh worktree has no .env; the repo's example holds the local defaults.
        steps.Add("( [ -f .env ] || [ ! -f .env.example ] || cp .env.example .env )");

        if (!string.IsNullOrWhiteSpace(target.InstallCommand))
            steps.Add(target.InstallCommand.Trim());

        steps.Add(target.Command.Trim());
        return string.Join(" && ", steps);
    }

    /// <summary>
    /// The worktree setup line when the repo has no <c>SetupCommand</c>: the driven target's
    /// install command, in its folder. Portable across cmd.exe and sh (that is what runs
    /// setup commands), so no bash-only constructs here. Null when there is nothing to
    /// install.
    /// </summary>
    public static string? DeriveSetupCommand(IReadOnlyList<RunTargetDto>? targets, Ulid repoId)
    {
        var target = PickTarget(targets, repoId);
        if (target is null || string.IsNullOrWhiteSpace(target.InstallCommand))
            return null;
        var cwd = string.IsNullOrWhiteSpace(target.WorkingDirectory) ? null : target.WorkingDirectory.Trim();
        return cwd is null
            ? target.InstallCommand.Trim()
            : $"cd {Quote(cwd)} && {target.InstallCommand.Trim()}";
    }

    /// <summary>The target QA drives: browsable kinds first, then the API, never infra.</summary>
    internal static RunTargetDto? PickTarget(IReadOnlyList<RunTargetDto>? targets, Ulid repoId)
    {
        if (targets is null || targets.Count == 0)
            return null;
        var repoKey = repoId.ToString();
        var mine = targets
            .Where(t => string.Equals(t.RepoId, repoKey, StringComparison.OrdinalIgnoreCase))
            .Where(t => !string.IsNullOrWhiteSpace(t.Command))
            .ToList();
        return mine.FirstOrDefault(t => RunTargetKinds.Normalize(t.Kind) == RunTargetKinds.Web)
            ?? mine.FirstOrDefault(t => RunTargetKinds.IsBrowsable(RunTargetKinds.Normalize(t.Kind)))
            ?? mine.FirstOrDefault(t => RunTargetKinds.Normalize(t.Kind) == RunTargetKinds.Api)
            ?? mine.FirstOrDefault(t => RunTargetKinds.Normalize(t.Kind)
                is not (RunTargetKinds.Infra or RunTargetKinds.Database));
    }

    /// <summary>Infra targets the chosen one (transitively) depends on, in the same repo —
    /// the compose file lives there.</summary>
    private static IEnumerable<RunTargetDto> InfraDependencies(
        IReadOnlyList<RunTargetDto> targets, RunTargetDto target)
    {
        var byKey = targets.ToDictionary(t => t.Key, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(target.DependsOn ?? []);
        while (queue.Count > 0)
        {
            var key = queue.Dequeue();
            if (!seen.Add(key) || !byKey.TryGetValue(key, out var dep))
                continue;
            foreach (var next in dep.DependsOn ?? [])
                queue.Enqueue(next);
            if (RunTargetKinds.Normalize(dep.Kind) == RunTargetKinds.Infra
                && string.Equals(dep.RepoId, target.RepoId, StringComparison.OrdinalIgnoreCase))
                yield return dep;
        }
    }

    /// <summary>`docker compose up X` → `docker compose up -d X`; anything else unchanged.</summary>
    internal static string Detach(string command)
    {
        var trimmed = command.Trim();
        if (ComposeUp.IsMatch(trimmed) && !DetachedFlag.IsMatch(trimmed))
            return ComposeUp.Replace(trimmed, "$1 -d", 1);
        return trimmed;
    }

    private static string Quote(string path)
        => path.Any(char.IsWhiteSpace) ? $"\"{path}\"" : path;

    private static readonly Regex ComposeUp = new(
        @"^(docker(?:\s+compose|-compose)\s+up)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DetachedFlag = new(
        @"\s(-d|--detach)\b", RegexOptions.Compiled);
}
