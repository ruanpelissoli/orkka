namespace Orkka.Modules.Projects.Features.Projects;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.SharedKernel.Utilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using IEventBus = Orkka.SharedKernel.Events.IEventBus;

using Orkka.Modules.Projects.Infrastructure;
using Orkka.SharedKernel.Events;
using Orkka.SharedKernel.Events.Catalog;
using Orkka.SharedKernel.Security;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;

public sealed class DeleteProjectEndpoint(ProjectsDbContext db, ICurrentUser currentUser, IEventBus eventBus)
    : EndpointWithoutRequest
{
    private static readonly System.Text.Json.JsonSerializerOptions RequirementsJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public override void Configure()
    {
        Delete("/api/projects/{id:ulid}");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        // Deleting a project wipes all its tasks, repos, and history.
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.ProjectsDelete))
            return;

        var id = Ulid.Parse(Route<string>("id"));

        var entity = await db.Projects.FirstOrDefaultAsync(p => p.Id == id && p.OrgId == currentUser.OrgId, ct);

        if (entity is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        db.Projects.Remove(entity);
        await db.SaveChangesAsync(ct);

        // Cross-module FKs no longer exist — other modules delete their project-scoped
        // rows (tasks, agents, conversations, diagrams) in response to this event.
        await eventBus.PublishAsync(new ProjectDeleted(entity.Id, entity.OrgId), ct);

        await Send.NoContentAsync(ct);
    }
}
