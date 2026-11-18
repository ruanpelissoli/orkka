namespace Orkka.Modules.Projects.Features.ProjectRepos;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.SharedKernel.Utilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using Orkka.Modules.Projects.Domain;
using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Security;
using Orkka.Modules.Projects.Infrastructure;
using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

public sealed class AddProjectRepoEndpoint(
    ProjectsDbContext db,
    ICurrentUser currentUser,
    IProjectResolver projectResolver)
    : Endpoint<AddProjectRepoRequest, ProjectRepoResponse>
{
    public override void Configure()
    {
        Post("/api/projects/{projectId}/repos");
    }

    public override async Task HandleAsync(AddProjectRepoRequest req, CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.ReposManage))
            return;

        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var projectExists = await db.Projects.AnyAsync(p => p.Id == projectId && p.OrgId == currentUser.OrgId, ct);

        if (!projectExists)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var entity = new ProjectRepo
        {
            OrgId = currentUser.OrgId,
            ProjectId = projectId,
            Name = req.Name,
            GitUrl = req.GitUrl,
            DefaultBranch = req.DefaultBranch,
            ForgeKind = Mappers.ParseForge(req.Provider, req.GitUrl),
            CliAccount = string.IsNullOrWhiteSpace(req.CliAccount) ? null : req.CliAccount.Trim(),
            WorkspaceStatus = WorkspaceStatus.Pending,
            CardColor = Mappers.RandomCardColor(),
        };

        db.ProjectRepos.Add(entity);
        await db.SaveChangesAsync(ct);

        await Send.CreatedAtAsync<ListProjectReposEndpoint>(
            new { projectId = entity.ProjectId },
            entity.ToResponse(),
            cancellation: ct);
    }
}
