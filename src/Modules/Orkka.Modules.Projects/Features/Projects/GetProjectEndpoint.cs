namespace Orkka.Modules.Projects.Features.Projects;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.SharedKernel.Utilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Projects.Infrastructure;
using Orkka.Modules.Tasks.Contracts;
using Orkka.SharedKernel.Security;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;

public sealed class GetProjectEndpoint(
    ProjectsDbContext db,
    ICurrentUser currentUser,
    IProjectResolver projectResolver,
    ITasksApi tasksApi)
    : EndpointWithoutRequest<ProjectResponse>
{
    public override void Configure()
    {
        Get("/api/projects/{id}");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var resolved = await projectResolver.ResolveAsync(Route<string>("id")!, ct);
        if (resolved is not { } id)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var project = await db.Projects
            .Where(p => p.Id == id && p.OrgId == currentUser.OrgId)
            .FirstOrDefaultAsync(ct);

        if (project is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var taskCounts = await tasksApi.GetTaskCountsAsync([project.Id], ct);

        await Send.OkAsync(project.ToResponse(taskCount: taskCounts.GetValueOrDefault(project.Id)), ct);
    }
}
