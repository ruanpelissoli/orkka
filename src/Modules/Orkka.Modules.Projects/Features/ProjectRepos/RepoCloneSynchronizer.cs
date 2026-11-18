namespace Orkka.Modules.Projects.Features.ProjectRepos;

using Microsoft.Extensions.Logging;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Projects.Domain;
using Orkka.Modules.Projects.Infrastructure;

/// <summary>
/// Ensures a repo's bare clone exists and is current before anything tries to read it.
///
/// The runner's reader deliberately does not clone on demand — it fails outright on a missing
/// bare path — so every read path has to come through here first. Forgetting this is a
/// quiet failure, not a loud one: the read returns "not cloned yet" and the caller degrades to
/// whatever its empty case is.
///
/// The clone happens on the developer's runner (<see cref="RepoReaders"/>), with their own git
/// credentials — Orkka holds none for anyone's repositories.
/// </summary>
public sealed class RepoCloneSynchronizer(
    ProjectsDbContext db,
    RepoReaders readers,
    ILogger<RepoCloneSynchronizer> logger)
{
    /// <param name="purpose">What the caller is about to do, e.g. "browse repository files" —
    /// so each caller keeps its own wording when the runner cannot be reached.</param>
    /// <returns>Null on success, else a user-facing message.</returns>
    public async Task<string?> EnsureAsync(ProjectRepo repo, string purpose, CancellationToken ct)
    {
        if (!readers.CanReachRunner)
            return $"Start Orkka on your machine (`./orkka start`, which runs the runner) to {purpose} — repositories are read on your machine, with your own git access.";

        var cloneResult = await readers.For(repo).EnsureBareCloneAsync(
            repo.OrgId, repo.Id, repo.GitUrl, ct);

        if (cloneResult.IsFailure)
        {
            logger.LogWarning("Failed to sync repo {RepoName} ({RepoId}) from {GitUrl}: {Error}",
                repo.Name, repo.Id, repo.GitUrl, cloneResult.Error);
            return $"Failed to sync repository '{repo.Name}': {cloneResult.Error}";
        }

        if (repo.WorkspaceStatus != WorkspaceStatus.Ready)
        {
            repo.WorkspaceStatus = WorkspaceStatus.Ready;
            await db.SaveChangesAsync(ct);
        }

        return null;
    }
}
