namespace Orkka.Modules.SourceControl.Infrastructure.Services;

using System.Text;
using Microsoft.Extensions.Logging;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.SharedKernel.Utilities;

/// <summary>
/// Reads the repo on the requesting developer's runner to describe it in prompt-ready markdown.
/// Orkka keeps no copy of anyone's code, so with no runner to ask there is no context.
/// </summary>
public sealed class RepoContextBuilder(
    ILocalRepoReader runnerReader,
    ILogger<RepoContextBuilder> logger) : IRepoContextBuilder
{
    /// <summary>The runner-backed reads for this request, or null when nobody's machine can be
    /// asked.</summary>
    private RunnerReads? ReaderFor(RepoContextRequest request)
        => runnerReader.CanReach && !string.IsNullOrEmpty(request.RunnerUserId)
            ? new RunnerReads(runnerReader, request.RunnerUserId)
            : null;

    private sealed class RunnerReads(ILocalRepoReader local, string userId)
    {
        public Task<Result<List<FileTreeNode>>> GetFileTreeAsync(
            Ulid orgId, Ulid repoId, string branch, CancellationToken ct)
            => local.GetFileTreeAsync(orgId, repoId, userId, branch, ct);

        public Task<Result<string>> GetFileContentAsync(
            Ulid orgId, Ulid repoId, string branch, string filePath, CancellationToken ct)
            => local.GetFileContentAsync(orgId, repoId, userId, branch, filePath, ct);
    }

    public async Task<string?> BuildAsync(RepoContextRequest request, CancellationToken ct = default)
    {
        if (ReaderFor(request) is not { } repoReader)
            return null;
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"## Repository: {request.RepoName} (branch {request.DefaultBranch})")
              .AppendLine();

            var tree = await repoReader.GetFileTreeAsync(
                request.OrgId, request.RepoId, request.DefaultBranch, ct);

            // A readable repo with an unreadable tree still tells the model which repo it is,
            // so the header stands on its own.
            if (tree.IsSuccess && tree.Value is { Count: > 0 } nodes)
            {
                sb.AppendLine("### File tree").AppendLine();
                foreach (var node in nodes.Take(request.MaxTreePaths))
                    sb.AppendLine(node.Path);
                if (nodes.Count > request.MaxTreePaths)
                    sb.AppendLine($"... and {nodes.Count - request.MaxTreePaths} more files");
                sb.AppendLine();

                // Whichever CLAUDE.md the repo actually has — root or .claude/.
                var claudeMdPath = nodes
                    .Select(n => n.Path)
                    .FirstOrDefault(p => p.EndsWith("CLAUDE.md", StringComparison.OrdinalIgnoreCase));

                if (claudeMdPath is not null)
                {
                    var content = await repoReader.GetFileContentAsync(
                        request.OrgId, request.RepoId, request.DefaultBranch, claudeMdPath, ct);

                    if (content.IsSuccess && !string.IsNullOrWhiteSpace(content.Value))
                    {
                        sb.AppendLine($"### {claudeMdPath}").AppendLine()
                          .AppendLine(Truncate(content.Value, request.MaxInstructionChars))
                          .AppendLine();
                    }
                }
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Repo context unavailable for repo {RepoId} — the caller continues without it",
                request.RepoId);
            return null;
        }
    }

    /// <summary>
    /// Filenames that hold a project's visual tokens. Deliberately narrow: a component's own
    /// CSS tells you nothing about the brand, and pulling every stylesheet in a large repo
    /// would bury the few files that matter.
    /// </summary>
    private static readonly string[] StyleFileNames =
    [
        "globals.css", "global.css", "app.css", "index.css", "styles.css", "theme.css",
        "tailwind.config.js", "tailwind.config.ts", "tailwind.config.cjs", "tailwind.config.mjs",
        "theme.ts", "theme.js", "tokens.css", "variables.css", "colors.ts",
    ];

    private const int MaxStyleFiles = 4;
    private const int MaxStyleFileChars = 8000;

    public async Task<string?> BuildStyleContextAsync(
        RepoContextRequest request, CancellationToken ct = default)
    {
        if (ReaderFor(request) is not { } repoReader)
            return null;
        try
        {
            var tree = await repoReader.GetFileTreeAsync(
                request.OrgId, request.RepoId, request.DefaultBranch, ct);

            if (!tree.IsSuccess || tree.Value is not { Count: > 0 } nodes)
                return null;

            // Shallowest first: a root globals.css is the app's theme, one nested six levels
            // down is some component's local styling.
            var candidates = nodes
                .Select(n => n.Path)
                .Where(p => StyleFileNames.Contains(
                    p.Split('/').Last(), StringComparer.OrdinalIgnoreCase))
                .OrderBy(p => p.Count(c => c == '/'))
                .ThenBy(p => p.Length)
                .Take(MaxStyleFiles)
                .ToList();

            if (candidates.Count == 0)
                return null;

            var sb = new StringBuilder();
            foreach (var path in candidates)
            {
                var content = await repoReader.GetFileContentAsync(
                    request.OrgId, request.RepoId, request.DefaultBranch, path, ct);

                if (!content.IsSuccess || string.IsNullOrWhiteSpace(content.Value))
                    continue;

                sb.AppendLine($"### {path}").AppendLine()
                  .AppendLine(Truncate(content.Value, MaxStyleFileChars))
                  .AppendLine();
            }

            return sb.Length == 0 ? null : sb.ToString();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Style context unavailable for repo {RepoId} — the caller falls back to deriving "
                + "a palette instead of reading one", request.RepoId);
            return null;
        }
    }

    public async Task<bool> HasMeaningfulCodeAsync(
        RepoContextRequest request, CancellationToken ct = default)
    {
        if (ReaderFor(request) is not { } repoReader)
            return false;
        try
        {
            var tree = await repoReader.GetFileTreeAsync(
                request.OrgId, request.RepoId, request.DefaultBranch, ct);

            return tree.IsSuccess
                && tree.Value is { Count: > 0 } nodes
                && nodes.Any(n => !IsBoilerplatePath(n.Path));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Could not classify repo {RepoId} contents — treating it as code-less",
                request.RepoId);
            return false;
        }
    }

    /// <summary>
    /// Files a fresh repo carries before anyone writes code: dotfiles and dot-directories
    /// (.gitignore, .github/, .claude/), prose (markdown, txt), and the license family.
    /// Anything else — a package.json included — counts as a real start: it means a stack
    /// was chosen, even if no source exists yet.
    /// </summary>
    internal static bool IsBoilerplatePath(string path)
    {
        var segments = path.Split('/');
        if (segments.Any(s => s.StartsWith('.')))
            return true;

        var fileName = segments[^1];
        var extension = Path.GetExtension(fileName);
        if (extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
            return true;

        string[] proseNames = ["LICENSE", "LICENCE", "NOTICE", "AUTHORS", "COPYING", "CODEOWNERS"];
        var stem = Path.GetFileNameWithoutExtension(fileName);
        return proseNames.Contains(stem, StringComparer.OrdinalIgnoreCase)
            || proseNames.Contains(fileName, StringComparer.OrdinalIgnoreCase);
    }

    private static string Truncate(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[..maxChars] + "\n... (truncated)";
}
