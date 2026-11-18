namespace Orkka.Modules.Projects.Features.ProjectRepos;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.SharedKernel.Utilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using Orkka.SharedKernel.Security;
using Orkka.Modules.Projects.Infrastructure;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;

public sealed class ListProjectReposEndpoint(ProjectsDbContext db, ICurrentUser currentUser, IProjectResolver projectResolver)
    : EndpointWithoutRequest<List<ProjectRepoResponse>>
{
    public override void Configure()
    {
        Get("/api/projects/{projectId}/repos");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var repos = await db.ProjectRepos
            .Where(r => r.OrgId == currentUser.OrgId && r.ProjectId == projectId)
            .OrderBy(r => r.Name)
            .ToListAsync(ct);

        var response = repos.Select(r => r.ToResponse()).ToList();
        await Send.OkAsync(response, ct);
    }
}
