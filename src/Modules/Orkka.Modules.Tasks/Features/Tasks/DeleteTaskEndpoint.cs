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

using FastEndpoints;
using Microsoft.EntityFrameworkCore;

public sealed class DeleteTaskEndpoint(
    TasksDbContext db,
    ICurrentUser currentUser,
    ITaskNotifier taskNotifier,
    ILogger<DeleteTaskEndpoint> logger)
    : EndpointWithoutRequest
{
    public override void Configure()
    {
        Delete("/api/tasks/{id:ulid}");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.TasksDelete))
            return;

        var id = Ulid.Parse(Route<string>("id"));

        var entity = await db.Tasks
            .FirstOrDefaultAsync(t => t.Id == id && t.OrgId == currentUser.OrgId, ct);

        if (entity is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // Only allow deletion of Draft tasks; others should be archived
        if (entity.CurrentLane != Lane.Drafting)
        {
            ThrowError("Only tasks in Drafting can be deleted. Use archive instead.");
            return;
        }

        db.Tasks.Remove(entity);
        await db.SaveChangesAsync(ct);

        // Its Discuss chat goes with it (the Agents module owns those rows).
        if (HttpContext.RequestServices.GetService(typeof(IAgentsApi)) is IAgentsApi agents)
            await agents.DeleteTaskConversationsAsync(entity.OrgId, entity.Id, ct);

        logger.LogInformation("Task {TaskId} '{Title}' deleted", id, entity.Title);

        await taskNotifier.SendTaskDeletedAsync(entity.ProjectId, new TaskDeletedEvent(
            entity.Id, DateTimeOffset.UtcNow));

        await Send.NoContentAsync(ct);
    }
}
