namespace Orkka.Modules.Projects.Features.ProjectRepos;
using Orkka.SharedKernel.Utilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using Orkka.Modules.Projects.Domain;
using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Security;
using Orkka.Modules.Projects.Infrastructure;
using Orkka.Modules.SourceControl.Contracts;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;

public sealed class GetRepoFileTreeEndpoint(
    ProjectsDbContext db,
    ICurrentUser currentUser,
    RepoReaders readers,
    RepoCloneSynchronizer repoClones,
    IProjectResolver projectResolver)
    : EndpointWithoutRequest<List<FileTreeNode>>
{
    public override void Configure()
    {
        Get("/api/projects/{projectId}/repos/{repoId:ulid}/files");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        var repoId = Ulid.Parse(Route<string>("repoId"));

        var repo = await db.ProjectRepos
            .Include(r => r.Project)
            .FirstOrDefaultAsync(r => r.Id == repoId && r.ProjectId == projectId && r.OrgId == currentUser.OrgId, ct);

        if (repo is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // Ensure bare clone exists and is up-to-date
        var ensureResult = await repoClones.EnsureAsync(repo, "browse repository files", ct);
        if (ensureResult is not null)
        {
            ThrowError(ensureResult);
            return;
        }

        var branch = repo.DefaultBranch ?? "main";
        var result = await readers.For(repo).GetFileTreeAsync(repo.OrgId, repo.Id, branch, ct);

        if (result.IsFailure)
        {
            ThrowError(result.Error!);
            return;
        }

        await Send.OkAsync(result.Value!, ct);
    }
}
