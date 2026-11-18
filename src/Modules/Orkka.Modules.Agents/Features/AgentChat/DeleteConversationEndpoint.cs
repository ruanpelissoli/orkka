namespace Orkka.Modules.Agents.Features.AgentChat;

using Orkka.SharedKernel.Security;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Agents.Infrastructure;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;

public sealed class DeleteConversationEndpoint(AgentsDbContext db, ICurrentUser currentUser, IProjectResolver projectResolver)
    : EndpointWithoutRequest
{
    public override void Configure()
    {
        Delete("/api/projects/{projectId}/agents/{agentId:ulid}/conversations/{conversationId:ulid}");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.AgentsChat))
            return;

        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        var agentId = Ulid.Parse(Route<string>("agentId"));
        var conversationId = Ulid.Parse(Route<string>("conversationId"));

        var conversation = await db.AgentConversations
            .FirstOrDefaultAsync(c => c.Id == conversationId && c.ProjectId == projectId && c.AgentId == agentId && c.OrgId == currentUser.OrgId, ct);

        if (conversation is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        db.AgentConversations.Remove(conversation);
        await db.SaveChangesAsync(ct);

        await Send.NoContentAsync(ct);
    }
}
