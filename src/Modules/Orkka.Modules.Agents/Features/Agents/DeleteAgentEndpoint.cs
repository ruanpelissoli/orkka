namespace Orkka.Modules.Agents.Features.Agents;

using Orkka.SharedKernel.Security;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Domain;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Agents.Infrastructure;
using FastEndpoints;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;

public sealed class DeleteAgentEndpoint(
    AgentsDbContext db,
    ICurrentUser currentUser,
    IProjectResolver projectResolver,
    ILogger<DeleteAgentEndpoint> logger)
    : EndpointWithoutRequest
{
    private static readonly AgentRole[] DevRoles =
        [AgentRole.FrontendDev, AgentRole.BackendDev, AgentRole.FullStackDev];

    public override void Configure()
    {
        Delete("/api/projects/{projectId}/agents/{agentId:ulid}");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.AgentsConfigure))
            return;

        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        var agentId = Ulid.Parse(Route<string>("agentId"));

        var entity = await db.ProjectAgents
            .FirstOrDefaultAsync(a => a.Id == agentId && a.ProjectId == projectId && a.OrgId == currentUser.OrgId, ct);

        if (entity is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (entity.IsDefault)
        {
            ThrowError("Default agents cannot be deleted. You can rename or reconfigure them instead.");
            return;
        }

        // Prevent deleting the last dev agent
        if (DevRoles.Contains(entity.Role))
        {
            var otherDevCount = await db.ProjectAgents
                .CountAsync(a =>
                    a.OrgId == currentUser.OrgId &&
                    a.ProjectId == projectId &&
                    a.Id != agentId &&
                    DevRoles.Contains(a.Role), ct);

            if (otherDevCount == 0)
            {
                ThrowError("Cannot delete the last dev agent. At least one FrontendDev, BackendDev, or FullStackDev agent is required.");
                return;
            }
        }

        db.ProjectAgents.Remove(entity);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Agent '{AgentName}' ({Role}) deleted from project {ProjectId}", entity.Name, entity.Role, projectId);

        await Send.NoContentAsync(ct);
    }
}
