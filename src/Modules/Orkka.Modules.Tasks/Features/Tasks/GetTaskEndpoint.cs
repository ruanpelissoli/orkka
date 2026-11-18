namespace Orkka.Modules.Tasks.Features.Tasks;

using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.SharedKernel.Security;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.Modules.Agents.Contracts;
using Orkka.SharedKernel.Storage;
using Orkka.SharedKernel.Utilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using Orkka.Modules.Tasks.Features.Tasks;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;

public sealed class GetTaskEndpoint(TasksDbContext db, Orkka.Modules.Projects.Contracts.IProjectsApi projectsApi, ICurrentUser currentUser)
    : EndpointWithoutRequest<TaskResponse>
{
    public override void Configure()
    {
        Get("/api/tasks/{id:ulid}");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var id = Ulid.Parse(Route<string>("id"));

        var entity = await db.Tasks
            .AsNoTracking()
            .Include(t => t.Dependencies).ThenInclude(d => d.DependsOnTask)
            .Include(t => t.Dependents).ThenInclude(d => d.Task)
            .FirstOrDefaultAsync(t => t.Id == id && t.OrgId == currentUser.OrgId, ct);

        if (entity is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var project = await projectsApi.GetProjectSummaryAsync(entity.ProjectId, ct);
        var repo = entity.RepoId is { } repoId ? await projectsApi.GetRepoAsync(repoId, ct) : null;
        await Send.OkAsync(entity.ToResponse(project!.Key, repo), ct);
    }
}
