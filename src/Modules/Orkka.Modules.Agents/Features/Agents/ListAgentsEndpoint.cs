namespace Orkka.Modules.Agents.Features.Agents;

using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Security;
using Orkka.Modules.Agents.Infrastructure;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;

public sealed class ListAgentsEndpoint(
    AgentsDbContext db,
    ICurrentUser currentUser,
    IProjectResolver projectResolver,
    Orkka.Modules.Usage.Contracts.IUsageQuery usageQuery)
    : EndpointWithoutRequest<List<AgentResponse>>
{
    public override void Configure()
    {
        Get("/api/projects/{projectId}/agents");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var agents = await db.ProjectAgents
            .AsNoTracking()
            .Where(a => a.OrgId == currentUser.OrgId && a.ProjectId == projectId)
            .OrderBy(a => a.Role)
            .ThenBy(a => a.CreatedAt)
            .ToListAsync(ct);

        // One grouped read for the whole roster — the stored per-agent counters have no cache
        // buckets, so the team page would otherwise show a fraction of the tokens behind the
        // cost printed beside them.
        var billed = await usageQuery.GetAgentTokenTotalsAsync(
            currentUser.OrgId, projectId, agents.Select(a => a.Id).ToList(), ct);

        await Send.OkAsync(
            agents.Select(a => a.ToResponse(billed.GetValueOrDefault(a.Id))).ToList(), ct);
    }
}
