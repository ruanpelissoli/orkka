namespace Orkka.LocalRunner;

using System.Text.Json;
using Orkka.Git;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Projects.Contracts;

/// <summary>
/// Finds the git repositories in a project's base folder — the <c>DiscoverRepos</c> op.
///
/// Two shapes. A folder that is itself a repository (<c>.git</c> directly inside it) is a
/// monorepo: the one repository plus its top-level directories as "areas". Anything else is a
/// folder of repositories, found up to two levels down. Hidden folders, dependency and build
/// output folders, Orkka's own task folders and anything inside a repository already found
/// (submodules, vendored checkouts) are skipped.
///
/// Read-only by construction: git is only asked questions (remote, branches, status, counts),
/// and nothing is created — no <c>git init</c>, no marker files.
/// </summary>
public static class RepoDiscovery
{
    /// <summary>How deep a folder of repositories is searched: <c>base/api</c> and
    /// <c>base/services/billing</c>, not deeper.</summary>
    public const int MaxDepth = 2;

    private const int DescriptionMaxLength = 300;

    private static readonly HashSet<string> SkippedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", ".orkka",
    };

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Null when the folder may be scanned, else the sentence to show.</summary>
    public static string? RejectPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "Pick a folder.";
        if (!Path.IsPathFullyQualified(path.Trim()))
            return "Give the folder's full path.";

        var expanded = Path.GetFullPath(path.Trim());
        var full = Path.TrimEndingDirectorySeparator(expanded);

        if (Path.GetPathRoot(expanded) is { Length: > 0 } root
            && string.Equals(Path.TrimEndingDirectorySeparator(root), full, PathComparison))
            return "That is the root of a drive, not a project. Pick the folder that holds your repositories.";

        var home = Path.TrimEndingDirectorySeparator(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (home.Length > 0 && string.Equals(home, full, PathComparison))
            return "That is your home folder, not a project. Pick the folder that holds your repositories.";

        return null;
    }

    public static async Task<DiscoverReposResult> DiscoverAsync(
        GitProcess git, string? path, CancellationToken ct = default)
    {
        if (RejectPath(path) is { } refusal)
            return new DiscoverReposResult(path, null, [], [], refusal);

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path!.Trim()));
        if (!Directory.Exists(root))
            return new DiscoverReposResult(root, null, [], [], "That folder does not exist on this machine.");

        try
        {
            if (IsRepositoryRoot(root))
            {
                var repo = await DescribeAsync(git, root, "", ct);
                var areas = SafeDirectories(root)
                    .Where(d => !IsSkipped(d))
                    .Select(Path.GetFileName)
                    .OfType<string>()
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return new DiscoverReposResult(root, DiscoveredShapes.Monorepo, [repo], areas, null);
            }

            var found = new List<string>();
            Scan(root, depth: 1, found);

            var repos = new List<DiscoveredRepo>(found.Count);
            foreach (var dir in found.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var relative = Path.GetRelativePath(root, dir).Replace('\\', '/');
                repos.Add(await DescribeAsync(git, dir, relative, ct));
            }

            return new DiscoverReposResult(
                root, DiscoveredShapes.MultiRepo, [.. repos], [],
                repos.Count == 0
                    ? "No git repositories were found in that folder or the two levels below it."
                    : null);
        }
        catch (UnauthorizedAccessException)
        {
            return new DiscoverReposResult(root, null, [], [], "Access to that folder was denied.");
        }
    }

    private static void Scan(string dir, int depth, List<string> found)
    {
        foreach (var child in SafeDirectories(dir))
        {
            if (IsSkipped(child))
                continue;

            if (IsRepositoryRoot(child))
            {
                // Never descend into a repository: anything below it is its own business
                // (submodules, vendored checkouts, its own nested tools).
                found.Add(child);
                continue;
            }

            if (depth < MaxDepth)
                Scan(child, depth + 1, found);
        }
    }

    /// <summary><c>.git</c> as a directory (a normal clone) or a file (a linked worktree or a
    /// submodule checkout).</summary>
    private static bool IsRepositoryRoot(string dir)
    {
        var dotGit = Path.Combine(dir, ".git");
        return Directory.Exists(dotGit) || File.Exists(dotGit);
    }

    private static bool IsSkipped(string dir)
    {
        var name = Path.GetFileName(dir);
        if (string.IsNullOrEmpty(name) || name.StartsWith('.') || SkippedNames.Contains(name)
            || name.EndsWith("-worktrees", StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            var attributes = File.GetAttributes(dir);
            return attributes.HasFlag(FileAttributes.Hidden) || attributes.HasFlag(FileAttributes.System)
                || attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static IEnumerable<string> SafeDirectories(string dir)
    {
        try
        {
            return Directory.EnumerateDirectories(dir).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static async Task<DiscoveredRepo> DescribeAsync(
        GitProcess git, string dir, string relativePath, CancellationToken ct)
    {
        async Task<string?> Ask(params string[] args)
        {
            var result = await git.RunAsync(
                new GitCommand(dir, args) { Timeout = TimeSpan.FromSeconds(30), Probe = true }, ct);
            return result.Succeeded ? result.Output : null;
        }

        // The URL as the developer configured it — not `remote get-url`, which applies their
        // url.*.insteadOf rewrites (a mirror, an ssh alias, a local path in a test rig). The
        // configured URL names the forge the pull request goes to; git applies the rewrite
        // anyway when the runner clones, fetches and pushes.
        var origin = await Ask("config", "--get", "remote.origin.url");
        var originUrl = string.IsNullOrWhiteSpace(origin) ? null : GitUrls.Clean(origin.Trim());

        var hasCommits = await Ask("rev-parse", "--verify", "--quiet", "HEAD") is { Length: > 0 };
        var current = await Ask("symbolic-ref", "--short", "-q", "HEAD");
        current = string.IsNullOrWhiteSpace(current) ? null : current.Trim();

        string? defaultBranch = null;
        if (originUrl is not null
            && await Ask("symbolic-ref", "--short", "-q", "refs/remotes/origin/HEAD") is { Length: > 0 } originHead)
        {
            defaultBranch = originHead.StartsWith("origin/", StringComparison.Ordinal)
                ? originHead["origin/".Length..]
                : originHead;
        }
        defaultBranch ??= current;

        var status = await Ask("status", "--porcelain");
        var isDirty = !string.IsNullOrWhiteSpace(status);

        var unpushed = false;
        if (originUrl is not null && hasCommits)
        {
            var ahead = await Ask("rev-list", "--count", "@{u}..HEAD")
                // No upstream: anything no origin branch has yet.
                ?? await Ask("rev-list", "--count", "HEAD", "--not", "--remotes=origin");
            unpushed = int.TryParse(ahead, out var count) && count > 0;
        }

        var stack = RepoStackClassifier.Classify(dir);

        return new DiscoveredRepo(
            relativePath,
            Path.GetFileName(dir),
            originUrl,
            defaultBranch,
            current,
            isDirty,
            unpushed,
            hasCommits,
            ReadDescription(dir),
            stack == RepoStacks.General ? null : stack);
    }

    /// <summary>
    /// What the repository says it is: <c>orkka.json</c>'s <c>description</c>, else the first
    /// paragraph of its README (headings, badges and HTML skipped), at most 300 characters.
    /// </summary>
    public static string? ReadDescription(string dir)
    {
        try
        {
            var manifest = Path.Combine(dir, OrkkaManifest.FileName);
            if (File.Exists(manifest)
                && OrkkaManifest.TryReadDescription(File.ReadAllText(manifest)) is { } declared)
                return Clip(declared);

            var readme = Directory.EnumerateFiles(dir)
                .Where(f => Path.GetFileName(f).StartsWith("readme", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => Path.GetExtension(f).Equals(".md", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .FirstOrDefault();
            return readme is null ? null : FirstParagraph(File.ReadAllText(readme));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    internal static string? FirstParagraph(string text)
    {
        var paragraph = new List<string>();
        var inFence = false;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence)
                continue;

            var noise = line.Length == 0
                || line.StartsWith('#')
                || line.StartsWith('<')
                || line.StartsWith("[![", StringComparison.Ordinal)
                || line.StartsWith("![", StringComparison.Ordinal)
                || line.StartsWith("---", StringComparison.Ordinal)
                || line.StartsWith("===", StringComparison.Ordinal);
            if (noise)
            {
                if (paragraph.Count > 0)
                    break;
                continue;
            }

            paragraph.Add(line);
        }

        return paragraph.Count == 0 ? null : Clip(string.Join(' ', paragraph));
    }

    private static string? Clip(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
            return null;
        return trimmed.Length <= DescriptionMaxLength
            ? trimmed
            : trimmed[..(DescriptionMaxLength - 1)].TrimEnd() + "…";
    }
}
