namespace Orkka.Modules.Agents.Features.Workspace;

using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Features.Agents;
using Orkka.Modules.Agents.Infrastructure;
using Orkka.Modules.Organizations.Contracts;
using Orkka.SharedKernel.Security;

/// <param name="Provider">The workspace's choice: a <see cref="WorkspaceAiProviders.Choices"/> key.</param>
/// <param name="Choices">Every accepted value, Automatic first.</param>
/// <param name="EffectiveProvider">The runner CLI key new agents are seeded on right now.</param>
/// <param name="Blocker">Why the chosen provider is not what new agents get (it cannot run on the
/// developer's runner); null when it is, and on Automatic.</param>
public sealed record WorkspaceAiProviderResponse(
    string Provider, IReadOnlyList<string> Choices, string EffectiveProvider, string? Blocker);

/// <param name="Provider">A <see cref="WorkspaceAiProviders.Choices"/> key ("automatic", "copilot", ...).</param>
/// <param name="ApplyToExistingProjects">Also move the agents of the workspace's existing projects
/// onto the provider's models (default true).</param>
public sealed record UpdateWorkspaceAiProviderRequest(string Provider, bool ApplyToExistingProjects = true);

/// <param name="ProjectCount">Projects whose agents were considered (0 when not applied).</param>
public sealed record UpdateWorkspaceAiProviderResponse(
    string Provider,
    string EffectiveProvider,
    string? Blocker,
    bool Applied,
    int ProjectCount,
    int UpdatedCount,
    int UnchangedCount,
    int SkippedQaCount);

/// <summary>The workspace's AI provider (Settings → Workspace): the choice, and what new
/// agents get with it on this developer's runner right now.</summary>
public sealed class GetWorkspaceAiProviderEndpoint(
    ICurrentUser currentUser,
    IWorkspaceAiProviderResolver resolver)
    : EndpointWithoutRequest<WorkspaceAiProviderResponse>
{
    public override void Configure()
    {
        Get("/api/org/ai-provider");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var resolution = await resolver.ResolveAsync(currentUser.OrgId, currentUser.UserId, ct);
        await Send.OkAsync(new WorkspaceAiProviderResponse(
            resolution.Choice, WorkspaceAiProviders.Choices,
            WorkspaceAiProviders.CliKey(resolution.Provider), resolution.Blocker), ct);
    }
}

/// <summary>
/// Sets the workspace's AI provider. Owner-only, like the workspace's name: it decides which
/// subscription every new team of the workspace runs on. With
/// <see cref="UpdateWorkspaceAiProviderRequest.ApplyToExistingProjects"/> the agents of every
/// project in the workspace move too, through the same bulk write as "Set every agent's model"
/// (<see cref="AgentModelBulkUpdate"/>: QA skipped for a model without browser tools, one audit
/// event per changed agent): the provider's tier models per role — Copilot is
/// <c>copilot/auto</c> on every tier — and, for Automatic, the provider Automatic resolves to.
/// </summary>
public sealed class UpdateWorkspaceAiProviderEndpoint(
    AgentsDbContext db,
    ICurrentUser currentUser,
    IWorkspaceAiSettings settings,
    IWorkspaceAiProviderResolver resolver,
    IModelCatalog modelCatalog,
    IAgentAuditLog audit)
    : Endpoint<UpdateWorkspaceAiProviderRequest, UpdateWorkspaceAiProviderResponse>
{
    public override void Configure()
    {
        Put("/api/org/ai-provider");
    }

    public override async Task HandleAsync(UpdateWorkspaceAiProviderRequest req, CancellationToken ct)
    {
        if (!await OwnerGuard.AuthorizeOwnerAsync(currentUser, this))
            return;

        var key = WorkspaceAiProviders.Normalize(req.Provider);
        if (key is null)
        {
            AddError(r => r.Provider,
                $"'{req.Provider}' is not an AI provider choice. Valid: {string.Join(", ", WorkspaceAiProviders.Choices)}.");
            await Send.ErrorsAsync(400, ct);
            return;
        }

        var orgId = currentUser.OrgId;
        var previous = await settings.GetAiProviderAsync(orgId, ct);
        if (!await settings.SetAiProviderAsync(orgId, WorkspaceAiProviders.ToStored(key), ct))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var from = WorkspaceAiProviders.ToProvider(previous) is null
            ? WorkspaceAiProviders.Automatic
            : previous!.Trim().ToLowerInvariant();
        if (from != key)
            await audit.RecordAsync(AgentAuditRecord.ByUser(
                orgId, currentUser.UserId, AgentAuditEventType.WorkspaceSettingsChanged,
                $"Workspace AI provider changed by {currentUser.UserId}: {from} → {key}",
                metadata: new { setting = "aiProvider", from, to = key }), ct);

        var resolution = await resolver.ResolveAsync(orgId, currentUser.UserId, ct);

        AgentModelBulkResult result = new(0, 0, 0);
        var projectCount = 0;
        if (req.ApplyToExistingProjects)
        {
            // An explicit choice is applied as chosen, even when it is not signed in yet — the
            // developer asked for it, the page showed the blocker, and runs say so until it is.
            var provider = WorkspaceAiProviders.ToProvider(key) ?? resolution.Provider;
            var catalog = await modelCatalog.GetAsync(ct);
            var agents = await db.ProjectAgents
                .Where(a => a.OrgId == orgId)
                .OrderBy(a => a.ProjectId)
                .ToListAsync(ct);
            projectCount = agents.Select(a => a.ProjectId).Distinct().Count();
            result = await AgentModelBulkUpdate.ApplyAsync(
                db, audit, catalog, orgId, currentUser.UserId, agents,
                a => WorkspaceAiProviders.ModelForRole(a.Role, provider, catalog),
                $"workspace AI provider set to {key}", ct);
        }

        await Send.OkAsync(new UpdateWorkspaceAiProviderResponse(
            key, WorkspaceAiProviders.CliKey(resolution.Provider), resolution.Blocker,
            req.ApplyToExistingProjects, projectCount,
            result.UpdatedCount, result.UnchangedCount, result.SkippedQaCount), ct);
    }
}
