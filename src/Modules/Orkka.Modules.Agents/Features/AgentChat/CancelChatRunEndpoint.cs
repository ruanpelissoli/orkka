namespace Orkka.Modules.Agents.Features.AgentChat;

using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Agents.Infrastructure;
using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Security;

public sealed record CancelChatRunResponse(bool Cancelled);

/// <summary>
/// Explicitly stops the conversation's in-flight agent reply. Chat runs are detached from
/// the SSE connection (closing the panel no longer cancels anything), so this endpoint is
/// the only user-facing way to stop a run. Idempotent: cancelled=false means no run was
/// active in this process — either it already finished or the host restarted.
/// </summary>
public sealed class CancelChatRunEndpoint(
    AgentsDbContext db,
    ChatRunCoordinator coordinator,
    ICurrentUser currentUser,
    IProjectResolver projectResolver)
    : EndpointWithoutRequest<CancelChatRunResponse>
{
    public override void Configure()
    {
        Post("/api/projects/{projectId}/agents/{agentId:ulid}/conversations/{conversationId:ulid}/cancel-run");
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

        var conversationExists = await db.AgentConversations
            .AsNoTracking()
            .AnyAsync(c => c.Id == conversationId && c.ProjectId == projectId && c.AgentId == agentId && c.OrgId == currentUser.OrgId, ct);

        if (!conversationExists)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var cancelled = coordinator.Cancel(conversationId);
        await Send.OkAsync(new CancelChatRunResponse(cancelled), ct);
    }
}
