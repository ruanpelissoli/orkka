namespace Orkka.Modules.Agents.Features.Agents;

using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Infrastructure;
using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Security;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;

public sealed record SetAgentsModelRequest(string Model);

/// <param name="UpdatedCount">Agents moved onto the model.</param>
/// <param name="UnchangedCount">Agents already on it.</param>
/// <param name="SkippedQaCount">QA agents left on their model because the target cannot drive
/// browser (MCP) tools — the same rule the per-agent update enforces.</param>
public sealed record SetAgentsModelResponse(int UpdatedCount, int UnchangedCount, int SkippedQaCount);

/// <summary>
/// "Set every agent's model" on the Team page: pins one model on every agent of the project
/// in a single step (e.g. moving a whole team onto Copilot Auto for the discounted premium
/// requests). Mirrors <see cref="UpdateAgentEndpoint"/> agent by agent — same permission, same
/// QA browser-tools rule, one AgentConfigChanged audit event per agent that actually changed —
/// and additionally requires the model to be one a user may pick (an enabled catalog chat row,
/// or a runner-discovered Ollama/OpenCode id), since a bulk write must never spread a typo or a
/// retired id across the team.
/// </summary>
public sealed class SetAgentsModelEndpoint(
    AgentsDbContext db,
    ICurrentUser currentUser,
    IProjectResolver projectResolver,
    IModelCatalog modelCatalog,
    IAgentAuditLog audit)
    : Endpoint<SetAgentsModelRequest, SetAgentsModelResponse>
{
    public override void Configure()
    {
        // Literal segment: never collides with PUT .../agents/{agentId:ulid}.
        Put("/api/projects/{projectId}/agents/model");
    }

    public override async Task HandleAsync(SetAgentsModelRequest req, CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.AgentsConfigure))
            return;

        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var model = req.Model?.Trim();
        if (string.IsNullOrEmpty(model))
        {
            ThrowError("Pick a model.");
            return;
        }

        var catalog = await modelCatalog.GetAsync(ct);
        if (!catalog.IsSelectable(model))
        {
            ThrowError($"'{model}' is not a model you can pick.");
            return;
        }
        // Store the catalog's own spelling, so ids compare equal everywhere downstream.
        model = catalog.Find(model)?.ModelId ?? model;
        var supportsMcp = catalog.SupportsMcp(model);

        var agents = await db.ProjectAgents
            .Where(a => a.ProjectId == projectId && a.OrgId == currentUser.OrgId)
            .ToListAsync(ct);

        var changed = new List<(ProjectAgentSnapshot Agent, string From)>();
        var unchanged = 0;
        var skippedQa = 0;
        foreach (var agent in agents)
        {
            if (agent.Role == AgentRole.QA && !supportsMcp)
            {
                skippedQa++;
                continue;
            }
            if (string.Equals(agent.Model, model, StringComparison.Ordinal))
            {
                unchanged++;
                continue;
            }

            changed.Add((new ProjectAgentSnapshot(agent.Id, agent.Name, agent.Role), agent.Model));
            agent.Model = model;
        }

        if (changed.Count > 0)
            await db.SaveChangesAsync(ct);

        foreach (var (agent, from) in changed)
            await audit.RecordAsync(AgentAuditRecord.ByUser(
                currentUser.OrgId, currentUser.UserId, AgentAuditEventType.AgentConfigChanged,
                $"Agent \"{agent.Name}\" ({agent.Role}) reconfigured by {currentUser.UserId}: Model (team-wide model change)",
                projectId: projectId, agentId: agent.Id,
                metadata: new { changed = new { Model = new { from, to = model } }, bulk = true }), ct);

        await Send.OkAsync(new SetAgentsModelResponse(changed.Count, unchanged, skippedQa), ct);
    }

    private sealed record ProjectAgentSnapshot(Ulid Id, string Name, AgentRole Role);
}
