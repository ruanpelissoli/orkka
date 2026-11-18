namespace Orkka.Modules.SourceControl.Contracts;

/// <summary>
/// Assembles a markdown description of a repository — its file tree and its agent
/// instructions file — for prompts that need to plan against real code instead of against
/// a description of it.
///
/// Read on the developer's runner (<see cref="RepoContextRequest.RunnerUserId"/>), with their own
/// git access. Best-effort by contract: no runner to ask, an offline runner, a repo it cannot
/// clone, or a missing branch degrades the caller's prompt rather than failing it. Every caller can
/// still do something useful without the context, so returning null beats throwing.
/// </summary>
public interface IRepoContextBuilder
{
    /// <summary>Returns the markdown block, or null when the repo could not be read at all.</summary>
    Task<string?> BuildAsync(RepoContextRequest request, CancellationToken ct = default);

    /// <summary>
    /// The repo's stylesheets and theme config — where an existing project's colours and fonts
    /// actually live. Null when the repo has none, which is the honest signal that there is
    /// nothing to extract and a palette has to be invented instead.
    /// </summary>
    Task<string?> BuildStyleContextAsync(RepoContextRequest request, CancellationToken ct = default);

    /// <summary>
    /// Whether the repo holds anything beyond scaffolding boilerplate (dotfiles, README,
    /// LICENSE and kin). A freshly created GitHub repo with only a .gitignore is READABLE —
    /// it produces a context — but it has no code and no look, and callers deciding "does
    /// this project already have a design?" must not treat it as one that does. False when
    /// the repo cannot be read.
    /// </summary>
    Task<bool> HasMeaningfulCodeAsync(RepoContextRequest request, CancellationToken ct = default);
}

/// <param name="MaxTreePaths">Enough of the tree to infer stack and conventions, small
/// enough not to dominate the prompt. A repo with 4000 files does not become 10x more
/// plannable at 4000 paths.</param>
/// <param name="MaxInstructionChars">Cap on the repo's CLAUDE.md — some are book-length.</param>
/// <param name="RunnerUserId">Whose runner reads the repo — the person the prompt is built
/// for. Null means there is nobody's machine to ask, and the context is null.</param>
public sealed record RepoContextRequest(
    Ulid OrgId,
    Ulid RepoId,
    string RepoName,
    string DefaultBranch,
    int MaxTreePaths = 300,
    int MaxInstructionChars = 6000,
    string? RunnerUserId = null);
