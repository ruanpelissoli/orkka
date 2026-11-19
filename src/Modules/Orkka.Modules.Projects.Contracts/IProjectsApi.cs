namespace Orkka.Modules.Projects.Contracts;

using Orkka.SharedKernel.Utilities;

/// <summary>Cross-module read model for a project. Settings is null when the project has no stored settings JSON.</summary>
public sealed record ProjectSummary(
    Ulid Id,
    Ulid OrgId,
    string Name,
    string Slug,
    string Key,
    ProjectSettingsDto? Settings,
    // The folder on the developer's machine the project was created from, and its shape.
    // Null for projects whose repos were linked one by one.
    string? BaseFolder = null,
    BaseFolderShape? BaseFolderShape = null);

/// <summary>Cross-module read model for a project repository.</summary>
public sealed record RepoSummary(
    Ulid Id,
    Ulid ProjectId,
    Ulid OrgId,
    string Name,
    string GitUrl,
    string DefaultBranch,
    string CardColor,
    WorkspaceStatus WorkspaceStatus,
    // Shell command that prepares a fresh worktree (dependencies, env) so the app can start.
    // Trailing default keeps older positional constructions compiling.
    string? SetupCommand = null,
    // Per-repo QA run command; null falls back to the project-level setting.
    string? QaRunCommand = null,
    // Compile check run after an automatic merge resolution, before the result is pushed.
    // Null skips it.
    string? BuildCommand = null,
    // Lint/typecheck/test command run in the task worktree after every Developing run; a
    // non-zero exit sends the task back to the dev agent. Null means no gate.
    string? CheckCommand = null,
    // The forge a remote lives on (which CLI publishes its pull requests) and the CLI account
    // to act as. Meaningless for a folder.
    ForgeKind ForgeKind = ForgeKind.Other,
    string? CliAccount = null,
    // Base-folder projects: where the repo sits in the base folder ("" = the base folder is
    // the repo), its routing description, when a rescan last missed it, and the absolute path
    // of the developer's own checkout (base folder + folder path) — the runner seeds the
    // mirror from it. All null for a repo that was not discovered from a base folder.
    string? FolderPath = null,
    string? Description = null,
    DateTimeOffset? MissingSince = null,
    string? LocalCheckoutPath = null,
    // The repo's detected or declared stack (RepoStacks name) — what the tracker repo router
    // tells a model about it, with Description.
    string? Stack = null);

/// <summary>A repository discovered in a base folder, to register on a project.</summary>
/// <param name="GitUrl">The origin URL for a repo with an origin (a Remote repo), else the
/// absolute folder path (a local-folder repo).</param>
public sealed record DiscoveredRepoRegistration(
    string Name,
    string GitUrl,
    string DefaultBranch,
    string FolderPath,
    string? Description);

/// <summary>A new project, created on behalf of <paramref name="CreatedByUserId"/>.</summary>
public sealed record NewProject(
    Ulid OrgId,
    string? CreatedByUserId,
    string? Locale,
    string Name,
    string? BaseFolder = null,
    BaseFolderShape? BaseFolderShape = null);

/// <summary>Joins a project's base folder with a repo's folder path into the absolute path
/// of the developer's checkout, in the base folder's own separator style.</summary>
public static class BaseFolders
{
    public static string? CheckoutPath(string? baseFolder, string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(baseFolder) || folderPath is null)
            return null;

        var separator = baseFolder.Contains('\\') ? '\\' : '/';
        var root = baseFolder.TrimEnd('\\', '/');
        if (root.Length == 0)
            root = baseFolder;
        var relative = folderPath.Trim().Trim('/');
        return relative.Length == 0
            ? root
            : root + separator + relative.Replace('/', separator);
    }

    /// <summary>Normalizes a repo's folder path inside a base folder: forward slashes, no
    /// leading/trailing slash, "" for the base folder itself. Null when it is absolute or
    /// climbs out with <c>..</c>.</summary>
    public static string? NormalizeFolderPath(string? folderPath)
    {
        if (folderPath is null)
            return null;
        var value = folderPath.Trim().Replace('\\', '/').Trim('/');
        if (value.Length == 0)
            return "";
        if (value.Contains('\0') || value.StartsWith('~')
            || (value.Length >= 2 && char.IsLetter(value[0]) && value[1] == ':')
            || folderPath.Trim().StartsWith('/') || folderPath.Trim().StartsWith('\\'))
            return null;
        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s is ".." or "."))
            return null;
        return string.Join('/', segments);
    }
}

/// <summary>Public surface of the Projects module.</summary>
public interface IProjectsApi
{
    /// <summary>Batch name lookup for cross-module display (e.g. usage breakdowns).</summary>
    Task<IReadOnlyDictionary<Ulid, string>> GetProjectNamesAsync(
        IReadOnlyCollection<Ulid> projectIds, CancellationToken ct = default);

    /// <summary>Project read model with parsed settings, or null when not found (or not visible to the caller's org).</summary>
    Task<ProjectSummary?> GetProjectSummaryAsync(Ulid projectId, CancellationToken ct = default);

    /// <summary>
    /// Batch form of <see cref="GetProjectSummaryAsync"/>, keyed by id. Cross-project surfaces
    /// (the Attention Inbox) need every project's Key to render task codes without issuing one
    /// query per project. Missing/invisible ids are simply absent from the result.
    /// </summary>
    Task<IReadOnlyDictionary<Ulid, ProjectSummary>> GetProjectSummariesAsync(
        IReadOnlyCollection<Ulid> projectIds, CancellationToken ct = default);

    /// <summary>Every project id in an org. Used when switching an org to project-scoped
    /// membership, which seeds the existing members onto the projects they can already
    /// reach so nobody loses access at the moment the switch is flipped.</summary>
    Task<IReadOnlyList<Ulid>> ListProjectIdsAsync(Ulid orgId, CancellationToken ct = default);

    /// <summary>Repo read model, or null when not found (or not visible to the caller's org).</summary>
    Task<RepoSummary?> GetRepoAsync(Ulid repoId, CancellationToken ct = default);

    /// <summary>
    /// Registers a directory on a user's own machine as a project repository
    /// (Provider=LocalPath, GitUrl = the absolute path). The path is opaque here: only
    /// the runner can see that filesystem, so the CALLER is responsible for having
    /// validated it there first.
    ///
    /// Returns null when the project does not belong to <paramref name="orgId"/>, and the
    /// EXISTING repo when that path is already linked — re-linking a folder is how a user
    /// retries after a failed clone, not an error worth surfacing.
    /// </summary>
    Task<RepoSummary?> AddLocalRepoAsync(
        Ulid orgId, Ulid projectId, string path, string name, string defaultBranch,
        CancellationToken ct = default);

    /// <summary>
    /// Creates a project exactly as <c>POST /api/projects</c> does (slug and key, default
    /// settings in the creator's locale, <c>ProjectCreated</c> so the agent roster is seeded),
    /// optionally recording the base folder it came from.
    /// </summary>
    Task<ProjectSummary> CreateProjectAsync(NewProject project, CancellationToken ct = default);

    /// <summary>
    /// Registers a repository discovered in the project's base folder. A remote (origin URL)
    /// is added like <c>POST /api/projects/{id}/repos</c> (forge detected from the host); a
    /// folder like <see cref="AddLocalRepoAsync"/>. Idempotent on the folder path: an existing
    /// repo at that path is returned unchanged. Null when the project is not in the org.
    /// </summary>
    Task<RepoSummary?> AddDiscoveredRepoAsync(
        Ulid orgId, Ulid projectId, DiscoveredRepoRegistration repo, CancellationToken ct = default);

    /// <summary>Sets or clears a repo's <see cref="RepoSummary.MissingSince"/>.</summary>
    Task SetRepoMissingAsync(Ulid repoId, DateTimeOffset? missingSince, CancellationToken ct = default);

    /// <summary>Points a remote repo at a new origin (the developer re-pointed their checkout),
    /// re-detecting its forge.</summary>
    Task SetRepoGitUrlAsync(Ulid repoId, string gitUrl, CancellationToken ct = default);

    /// <summary>Records the outcome of the runner priming its bare mirror, so the repo
    /// card stops showing "preparing".</summary>
    Task SetRepoWorkspaceStatusAsync(
        Ulid repoId, WorkspaceStatus status, CancellationToken ct = default);

    /// <summary>All repos of a project, ordered by name.</summary>
    Task<IReadOnlyList<RepoSummary>> ListReposAsync(Ulid projectId, CancellationToken ct = default);

    /// <summary>
    /// Ensures the repo's bare clone exists and is current, promoting it to
    /// <see cref="WorkspaceStatus.Ready"/> on success. Returns null on success, otherwise a
    /// user-facing message.
    /// </summary>
    /// <remarks>Cross-module entry point to the same synchronizer the in-module read paths
    /// use. Every path that reads a repo must call this first: a repo is created
    /// <see cref="WorkspaceStatus.Pending"/> and nothing else promotes it, and the underlying
    /// reader fails on a missing bare clone rather than cloning on demand. Filtering on
    /// <see cref="WorkspaceStatus"/> instead of calling this is the bug it exists to prevent —
    /// it silently drops every repo that has never been read.</remarks>
    /// <param name="purpose">What the caller was trying to do, e.g. "browse repository files" —
    /// the tail of the failure message, so each caller keeps its own wording.</param>
    Task<string?> EnsureRepoWorkspaceAsync(Ulid repoId, string purpose, CancellationToken ct = default);

    /// <summary>
    /// Batch form of <see cref="ListReposAsync"/>, keyed by repo id. Cross-project surfaces
    /// (the Attention Inbox) need repo names for tasks spanning many projects; looping
    /// <see cref="ListReposAsync"/> issues one query per project. Missing/invisible ids are
    /// simply absent from the result.
    /// </summary>
    Task<IReadOnlyDictionary<Ulid, RepoSummary>> ListReposForProjectsAsync(
        IReadOnlyCollection<Ulid> projectIds, CancellationToken ct = default);

    /// <summary>Ids of repos whose project still exists — used by the worker's workspace GC.</summary>
    Task<IReadOnlyList<Ulid>> ListLiveRepoIdsAsync(CancellationToken ct = default);

    /// <summary>
    /// Reserves a contiguous range of task numbers on the project counter and returns the last
    /// number of the range ([result - count + 1, result]). The row lock serializes concurrent
    /// reservations per project.
    /// </summary>
    Task<int> ReserveTaskNumbersAsync(Ulid projectId, int count, CancellationToken ct = default);

    /// <summary>Repo id → recorded toolchain (<see cref="RepoStacks"/>), null when not yet
    /// classified. Cross-org by design: the worker's queue routes tasks of every org.</summary>
    Task<IReadOnlyDictionary<Ulid, string?>> GetRepoStacksAsync(
        IReadOnlyCollection<Ulid> repoIds, CancellationToken ct = default);

    /// <summary>Records the repo's toolchain (normalized to a <see cref="RepoStacks"/> value).</summary>
    Task SetRepoStackAsync(Ulid repoId, string stack, CancellationToken ct = default);

    /// <summary>
    /// Of <paramref name="projectIds"/>, the projects whose guided setup has not been started yet
    /// (no <c>SetupCompletedAt</c>) — their ticket sources are not polled and nothing is imported
    /// for them until the developer presses Start. Cross-org by design (the runner hub has no
    /// signed-in user; the ids come from configurations already scoped to the caller).
    /// </summary>
    Task<IReadOnlySet<Ulid>> GetSetupPendingProjectIdsAsync(
        IReadOnlyCollection<Ulid> projectIds, CancellationToken ct = default);

}
