namespace Orkka.Modules.Agents.Features.AgentChat;

using Orkka.Modules.Agents.Domain;
using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Security;
using Orkka.Modules.Agents.Infrastructure;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;

public sealed class CreateConversationEndpoint(AgentsDbContext db, ICurrentUser currentUser, IProjectResolver projectResolver)
    : Endpoint<CreateConversationRequest, ConversationResponse>
{
    public override void Configure()
    {
        Post("/api/projects/{projectId}/agents/{agentId:ulid}/conversations");
    }

    public override async Task HandleAsync(CreateConversationRequest req, CancellationToken ct)
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

        var agent = await db.ProjectAgents
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == agentId && a.ProjectId == projectId && a.OrgId == currentUser.OrgId, ct);

        if (agent is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var entity = new AgentConversation
        {
            OrgId = currentUser.OrgId,
            ProjectId = projectId,
            AgentId = agentId,
            Title = req.Title
        };

        db.AgentConversations.Add(entity);
        await db.SaveChangesAsync(ct);

        var created = await db.AgentConversations
            .Include(c => c.Agent)
            .Include(c => c.Messages)
            .FirstAsync(c => c.Id == entity.Id, ct);

        await Send.CreatedAtAsync<ListConversationsEndpoint>(
            new { projectId, agentId },
            created.ToResponse(),
            cancellation: ct);
    }
}
