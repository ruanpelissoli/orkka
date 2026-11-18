namespace Orkka.Modules.Projects.Features.Projects;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.SharedKernel.Utilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using Orkka.Modules.Projects.Infrastructure;
using Orkka.Modules.Tasks.Contracts;
using Orkka.SharedKernel.Security;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;

public sealed class ListProjectsEndpoint(
    ProjectsDbContext db,
    ICurrentUser currentUser,
    ITasksApi tasksApi)
    : EndpointWithoutRequest<List<ProjectResponse>>
{
    public override void Configure()
    {
        Get("/api/projects");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var query = db.Projects.Where(p => p.OrgId == currentUser.OrgId);

        var projects = await query
            .OrderBy(p => p.Name)
            .ToListAsync(ct);

        var taskCounts = await tasksApi.GetTaskCountsAsync(projects.Select(p => p.Id).ToList(), ct);

        var response = projects
            .Select(p => p.ToResponse(taskCount: taskCounts.GetValueOrDefault(p.Id)))
            .ToList();

        await Send.OkAsync(response, ct);
    }
}
