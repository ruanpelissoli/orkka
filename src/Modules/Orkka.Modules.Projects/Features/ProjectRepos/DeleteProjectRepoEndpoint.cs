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

public sealed class DeleteProjectRepoEndpoint(ProjectsDbContext db, ICurrentUser currentUser)
    : EndpointWithoutRequest
{
    public override void Configure()
    {
        Delete("/api/repos/{repoId:ulid}");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        // Removing a repo disconnects agent access to the codebase.
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.ReposManage))
            return;

        var repoId = Ulid.Parse(Route<string>("repoId"));

        var entity = await db.ProjectRepos.FirstOrDefaultAsync(r => r.Id == repoId && r.OrgId == currentUser.OrgId, ct);

        if (entity is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        db.ProjectRepos.Remove(entity);
        await db.SaveChangesAsync(ct);
        await Send.NoContentAsync(ct);
    }
}
