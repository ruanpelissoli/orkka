namespace Orkka.Modules.Agents.Features.Agents;

using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Security;
using Orkka.Modules.Agents.Infrastructure;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;

public sealed class GetAgentEndpoint(
    AgentsDbContext db,
    ICurrentUser currentUser,
    IProjectResolver projectResolver,
    Orkka.Modules.Usage.Contracts.IUsageQuery usageQuery)
    : EndpointWithoutRequest<AgentResponse>
{
    public override void Configure()
    {
        Get("/api/projects/{projectId}/agents/{agentId:ulid}");
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

        var entity = await db.ProjectAgents
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == agentId && a.ProjectId == projectId && a.OrgId == currentUser.OrgId, ct);

        if (entity is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var billed = await usageQuery.GetAgentTokenTotalsAsync(
            currentUser.OrgId, projectId, [agentId], ct);

        await Send.OkAsync(entity.ToResponse(billed.GetValueOrDefault(agentId)), ct);
    }
}
