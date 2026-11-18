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

public sealed class GetLatestPlanEndpoint(TasksDbContext db, ICurrentUser currentUser)
    : EndpointWithoutRequest<TaskPlanResponse>
{
    public override void Configure()
    {
        Get("/api/tasks/{taskId:ulid}/plans/latest");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var taskId = Ulid.Parse(Route<string>("taskId"));

        var plan = await db.TaskPlans
            .AsNoTracking()
            .Where(p => p.OrgId == currentUser.OrgId && p.TaskId == taskId)
            .OrderByDescending(p => p.Version)
            .FirstOrDefaultAsync(ct);

        if (plan is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(plan.ToResponse(), ct);
    }
}
