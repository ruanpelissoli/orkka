namespace Orkka.Modules.Tasks.Features.TaskMessages;

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

using Orkka.Modules.Tasks.Features.TaskMessages;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;

public sealed class ListTaskMessagesEndpoint(TasksDbContext db, ICurrentUser currentUser)
    : EndpointWithoutRequest<List<TaskMessageResponse>>
{
    public override void Configure()
    {
        Get("/api/tasks/{taskId:ulid}/messages");
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

        var messages = await db.TaskMessages
            .AsNoTracking()
            .Where(m => m.OrgId == currentUser.OrgId && m.TaskId == taskId)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);

        await Send.OkAsync(messages.Select(m => m.ToResponse()).ToList(), ct);
    }
}
