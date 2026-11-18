namespace Orkka.Modules.Tasks.Features.TaskPlans;

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

using Orkka.Modules.Tasks.Features.TaskPlans;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;

public sealed class ListTaskPlansEndpoint(TasksDbContext db, ICurrentUser currentUser)
    : EndpointWithoutRequest<List<TaskPlanResponse>>
{
    public override void Configure()
    {
        Get("/api/tasks/{taskId:ulid}/plans");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var taskId = Ulid.Parse(Route<string>("taskId"));

        var taskExists = await db.Tasks.AsNoTracking().AnyAsync(t => t.Id == taskId && t.OrgId == currentUser.OrgId, ct);
        if (!taskExists)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var plans = await db.TaskPlans
            .AsNoTracking()
            .Where(p => p.OrgId == currentUser.OrgId && p.TaskId == taskId)
            .OrderByDescending(p => p.Version)
            .ToListAsync(ct);

        await Send.OkAsync(plans.Select(p => p.ToResponse()).ToList(), ct);
    }
}
