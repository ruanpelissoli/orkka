namespace Orkka.Modules.Agents.Features.AgentChat;

using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Security;
using Orkka.Modules.Agents.Infrastructure;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;

public sealed class ListConversationsEndpoint(AgentsDbContext db, ICurrentUser currentUser, IProjectResolver projectResolver)
    : EndpointWithoutRequest<List<ConversationResponse>>
{
    public override void Configure()
    {
        Get("/api/projects/{projectId}/agents/{agentId:ulid}/conversations");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        var agentId = Ulid.Parse(Route<string>("agentId"));

        var conversations = await db.AgentConversations
            .AsNoTracking()
            .Include(c => c.Agent)
            .Include(c => c.Messages)
            // A task's Discuss conversations live on the task, not in the Team page's list.
            .Where(c => c.OrgId == currentUser.OrgId && c.ProjectId == projectId && c.AgentId == agentId && c.TaskId == null)
            .ToListAsync(ct);

        // Order by last activity, not UpdatedAt — rows created before UpdatedAt was bumped
        // on new messages would otherwise sort by creation time and the UI would auto-open
        // a stale (possibly empty) conversation instead of the one last used.
        await Send.OkAsync(conversations
            .OrderByDescending(c => c.Messages.Count > 0 ? c.Messages.Max(m => m.CreatedAt) : c.CreatedAt)
            .Select(c => c.ToResponse())
            .ToList(), ct);
    }
}
