namespace Orkka.Modules.Projects.Features.ProjectRepos;
using Orkka.SharedKernel.Utilities;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using Orkka.Modules.Projects.Domain;
using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Security;
using Orkka.Modules.Projects.Infrastructure;
using Orkka.Modules.SourceControl.Contracts;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;

public sealed class GetRepoFileContentEndpoint(
    ProjectsDbContext db,
    ICurrentUser currentUser,
    RepoReaders readers,
    RepoCloneSynchronizer repoClones,
    IProjectResolver projectResolver)
    : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/api/projects/{projectId}/repos/{repoId:ulid}/files/content");
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
        var filePath = Query<string>("path");

        if (string.IsNullOrWhiteSpace(filePath))
        {
            ThrowError("'path' query parameter is required.");
            return;
        }

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
        var result = await readers.For(repo).GetFileContentAsync(repo.OrgId, repo.Id, branch, filePath, ct);

        if (result.IsFailure)
        {
            ThrowError(result.Error!);
            return;
        }

        HttpContext.Response.ContentType = "text/plain";
        await HttpContext.Response.WriteAsync(result.Value!, ct);
    }
}
