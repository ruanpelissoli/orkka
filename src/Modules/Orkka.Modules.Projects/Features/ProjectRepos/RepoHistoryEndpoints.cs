namespace Orkka.Modules.Projects.Features.ProjectRepos;

using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Projects.Domain;
using Orkka.Modules.Projects.Infrastructure;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.SharedKernel.Security;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;

public sealed record RepoCommitResponse(
    string Sha, string Author, DateTimeOffset Date, string Subject);

public sealed record RepoBranchResponse(
    string Name, string Sha, string? LastCommitSubject, DateTimeOffset? LastCommitDate, bool IsDefault,
    bool IsRemote);

public sealed record RepoCommitDiffResponse(string Sha, string Diff);

public sealed record RepoTreeEntryResponse(string Name, string Path, bool IsDirectory);

/// <summary>
/// Shared repo resolution + bare-cache refresh for history reads, through the same
/// <see cref="RepoCloneSynchronizer"/> every other read uses.
/// </summary>
internal static class RepoHistoryAccess
{
    public static async Task<(ProjectRepo? Repo, string? Error)> ResolveAndSyncAsync(
        string projectIdRoute, Ulid repoId,
        ProjectsDbContext db, IProjectResolver projectResolver, ICurrentUser currentUser,
        RepoCloneSynchronizer repoClones,
        CancellationToken ct)
    {
        var resolved = await projectResolver.ResolveAsync(projectIdRoute, ct);
        if (resolved is not { } projectId)
            return (null, null); // 404

        var repo = await db.ProjectRepos
            .FirstOrDefaultAsync(r => r.Id == repoId && r.ProjectId == projectId && r.OrgId == currentUser.OrgId, ct);
        if (repo is null)
            return (null, null); // 404

        var error = await repoClones.EnsureAsync(repo, "browse repository history", ct);
        return error is null ? (repo, null) : (null, error);
    }
}

/// <summary>One tree level for lazy file browsing — root when `path` is omitted.</summary>
public sealed class GetRepoTreeEndpoint(
    ProjectsDbContext db,
    ICurrentUser currentUser,
    RepoReaders readers,
    RepoCloneSynchronizer repoClones,
    IProjectResolver projectResolver)
    : EndpointWithoutRequest<List<RepoTreeEntryResponse>>
{
    public override void Configure()
    {
        Get("/api/projects/{projectId}/repos/{repoId:ulid}/tree");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var path = Query<string?>("path", isRequired: false);
        ProjectRepo? repo;

        if (string.IsNullOrEmpty(path))
        {
            // Root load syncs the bare cache once; expanding folders reads what's there.
            var (syncedRepo, error) = await RepoHistoryAccess.ResolveAndSyncAsync(
                Route<string>("projectId")!, Ulid.Parse(Route<string>("repoId")),
                db, projectResolver, currentUser, repoClones, ct);
            if (error is not null) { ThrowError(error); return; }
            repo = syncedRepo;
        }
        else
        {
            var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
            var repoId = Ulid.Parse(Route<string>("repoId"));
            repo = resolved is { } projectId
                ? await db.ProjectRepos.FirstOrDefaultAsync(
                    r => r.Id == repoId && r.ProjectId == projectId && r.OrgId == currentUser.OrgId, ct)
                : null;
        }

        if (repo is null) { await Send.NotFoundAsync(ct); return; }

        var branch = Query<string?>("branch", isRequired: false) ?? repo.DefaultBranch ?? "main";
        var result = await readers.For(repo).ListTreeAsync(repo.OrgId, repo.Id, branch, path, ct);
        if (result.IsFailure) { ThrowError(result.Error!); return; }

        await Send.OkAsync(result.Value!
            .Select(e => new RepoTreeEntryResponse(e.Name, e.Path, e.IsDirectory))
            .ToList(), ct);
    }
}

public sealed class GetRepoCommitsEndpoint(
    ProjectsDbContext db,
    ICurrentUser currentUser,
    RepoReaders readers,
    RepoCloneSynchronizer repoClones,
    IProjectResolver projectResolver)
    : EndpointWithoutRequest<List<RepoCommitResponse>>
{
    public override void Configure()
    {
        Get("/api/projects/{projectId}/repos/{repoId:ulid}/commits");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var (repo, error) = await RepoHistoryAccess.ResolveAndSyncAsync(
            Route<string>("projectId")!, Ulid.Parse(Route<string>("repoId")),
            db, projectResolver, currentUser, repoClones, ct);
        if (error is not null) { ThrowError(error); return; }
        if (repo is null) { await Send.NotFoundAsync(ct); return; }

        var branch = Query<string?>("branch", isRequired: false) ?? repo.DefaultBranch ?? "main";
        var skip = Query<int?>("skip", isRequired: false) ?? 0;
        var take = Math.Clamp(Query<int?>("take", isRequired: false) ?? 50, 1, 100);
        var path = Query<string?>("path", isRequired: false);

        var result = await readers.For(repo).GetCommitLogAsync(repo.OrgId, repo.Id, branch, skip, take, path, ct);
        if (result.IsFailure) { ThrowError(result.Error!); return; }

        await Send.OkAsync(result.Value!
            .Select(c => new RepoCommitResponse(c.Sha, c.Author, c.Date, c.Subject))
            .ToList(), ct);
    }
}

public sealed class GetRepoBranchesEndpoint(
    ProjectsDbContext db,
    ICurrentUser currentUser,
    RepoReaders readers,
    RepoCloneSynchronizer repoClones,
    IProjectResolver projectResolver)
    : EndpointWithoutRequest<List<RepoBranchResponse>>
{
    public override void Configure()
    {
        Get("/api/projects/{projectId}/repos/{repoId:ulid}/branches");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var (repo, error) = await RepoHistoryAccess.ResolveAndSyncAsync(
            Route<string>("projectId")!, Ulid.Parse(Route<string>("repoId")),
            db, projectResolver, currentUser, repoClones, ct);
        if (error is not null) { ThrowError(error); return; }
        if (repo is null) { await Send.NotFoundAsync(ct); return; }

        var result = await readers.For(repo).ListBranchesAsync(repo.OrgId, repo.Id, ct);
        if (result.IsFailure) { ThrowError(result.Error!); return; }

        var defaultBranch = repo.DefaultBranch ?? "main";
        await Send.OkAsync(result.Value!
            .Select(b => new RepoBranchResponse(
                b.Name, b.Sha, b.LastCommitSubject, b.LastCommitDate, b.Name == defaultBranch, b.IsRemote))
            .ToList(), ct);
    }
}

public sealed class GetRepoCommitDiffEndpoint(
    ProjectsDbContext db,
    ICurrentUser currentUser,
    RepoReaders readers,
    RepoCloneSynchronizer repoClones,
    IProjectResolver projectResolver)
    : EndpointWithoutRequest<RepoCommitDiffResponse>
{
    public override void Configure()
    {
        Get("/api/projects/{projectId}/repos/{repoId:ulid}/commits/{sha}/diff");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var sha = Route<string>("sha")!;
        if (!GitArgs.IsSafeSha(sha)) { await Send.NotFoundAsync(ct); return; }

        var (repo, error) = await RepoHistoryAccess.ResolveAndSyncAsync(
            Route<string>("projectId")!, Ulid.Parse(Route<string>("repoId")),
            db, projectResolver, currentUser, repoClones, ct);
        if (error is not null) { ThrowError(error); return; }
        if (repo is null) { await Send.NotFoundAsync(ct); return; }

        var result = await readers.For(repo).GetCommitDiffAsync(repo.OrgId, repo.Id, sha, ct: ct);
        if (result.IsFailure) { ThrowError(result.Error!); return; }

        await Send.OkAsync(new RepoCommitDiffResponse(sha, result.Value!), ct);
    }
}
