namespace Orkka.Modules.Agents.Features.Agents;

using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Infrastructure;
using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Security;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

public sealed record SwitchAgentsProviderRequest(string Provider);

public sealed record SwitchAgentsProviderResponse(int SwitchedCount, int SkippedQaCount);

/// <summary>
/// One-click "switch the team to provider X": remaps every agent's model to the
/// target provider's tier defaults (PM → fast, everyone else → capable). Rescues a pod
/// seeded for a provider the user no longer has connected (e.g. claude agents on a
/// codex-only machine). QA agents are skipped unless the target is Anthropic — browser
/// (MCP) testing only runs on Claude models.
/// </summary>
public sealed class SwitchAgentsProviderEndpoint(
    AgentsDbContext db,
    ICurrentUser currentUser,
    IProjectResolver projectResolver,
    IModelCatalog modelCatalog,
    ILogger<SwitchAgentsProviderEndpoint> logger)
    : Endpoint<SwitchAgentsProviderRequest, SwitchAgentsProviderResponse>
{
    public override void Configure()
    {
        Post("/api/projects/{projectId}/agents/switch-provider");
    }

    public override async Task HandleAsync(SwitchAgentsProviderRequest req, CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.AgentsConfigure))
            return;

        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // PixelLab is a retired image-only vendor — it has no chat models to switch a pod onto.
        if (!Enum.TryParse<AiProvider>(req.Provider, ignoreCase: true, out var provider)
            || provider == AiProvider.PixelLab)
        {
            var valid = string.Join(", ",
                Enum.GetValues<AiProvider>().Where(p => p != AiProvider.PixelLab));
            ThrowError($"'{req.Provider}' is not a valid provider. Valid providers: {valid}.");
            return;
        }

        var catalog = await modelCatalog.GetAsync(ct);

        var agents = await db.ProjectAgents
            .Where(a => a.ProjectId == projectId && a.OrgId == currentUser.OrgId)
            .ToListAsync(ct);

        var switched = 0;
        var skippedQa = 0;
        foreach (var agent in agents)
        {
            if (agent.Role == AgentRole.QA && provider != AiProvider.Anthropic)
            {
                skippedQa++;
                continue;
            }

            agent.Model = agent.Role == AgentRole.PM
                ? catalog.FastModel(provider)
                : agent.Role == AgentRole.QA
                    ? catalog.QaModel
                    : catalog.CapableModel(provider);
            switched++;
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Switched {Switched} agent(s) in project {ProjectId} to {Provider} ({SkippedQa} QA agent(s) kept on Claude)",
            switched, projectId, provider, skippedQa);

        await Send.OkAsync(new SwitchAgentsProviderResponse(switched, skippedQa), ct);
    }
}
