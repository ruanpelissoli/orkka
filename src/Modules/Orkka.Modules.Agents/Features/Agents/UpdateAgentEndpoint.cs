namespace Orkka.Modules.Agents.Features.Agents;

using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Security;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Features.Models;
using Orkka.Modules.Agents.Infrastructure;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;

public sealed class UpdateAgentEndpoint(
    AgentsDbContext db,
    ICurrentUser currentUser,
    IProjectResolver projectResolver,
    IModelCatalog modelCatalog,
    Orkka.Modules.Usage.Contracts.IUsageQuery usageQuery,
    IAgentAuditLog audit)
    : Endpoint<UpdateAgentRequest, AgentResponse>
{
    public override void Configure()
    {
        Put("/api/projects/{projectId}/agents/{agentId:ulid}");
    }

    public override async Task HandleAsync(UpdateAgentRequest req, CancellationToken ct)
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

        var catalog = await modelCatalog.GetAsync(ct);

        // SupportsMcp resolves against disabled rows too, so an agent already pinned to a
        // retired Claude model stays savable — only NEW picks are gated by IsSelectable.
        if (req.Model is not null && entity.Role == AgentRole.QA
            && !catalog.SupportsMcp(req.Model))
        {
            ThrowError("QA agents drive a browser through MCP tools — pick a model with MCP support.");
            return;
        }

        if (req.Name is not null) entity.Name = req.Name;
        if (req.Avatar is not null) entity.Avatar = req.Avatar;
        if (req.Model is not null) entity.Model = req.Model;
        if (req.UserInstructions is not null) entity.UserInstructions = req.UserInstructions;
        // Repo scope: null = unchanged, [] = all repos, ids = that subset.
        if (req.RepoIds is not null)
        {
            var repoIds = new List<Guid>(req.RepoIds.Count);
            foreach (var raw in req.RepoIds)
            {
                if (!Ulid.TryParse(raw, out var repoId))
                {
                    ThrowError("RepoIds must contain valid repository ids.");
                    return;
                }
                repoIds.Add(repoId.ToGuid());
            }
            entity.RepoIds = repoIds;
        }

        // Skills: null = unchanged, [] = none, ids = exactly that set. Ids must be skills of
        // this project (any state — assigning a disabled skill is allowed; it simply won't
        // inject until re-enabled).
        if (req.SkillIds is not null)
        {
            var wanted = new HashSet<Ulid>();
            foreach (var raw in req.SkillIds)
            {
                if (!Ulid.TryParse(raw, out var skillId))
                {
                    ThrowError("SkillIds must contain valid skill ids.");
                    return;
                }
                wanted.Add(skillId);
            }
            if (wanted.Count > 0)
            {
                var known = await db.AgentSkills.AsNoTracking()
                    .Where(s => s.OrgId == currentUser.OrgId && s.ProjectId == projectId)
                    .Select(s => s.Id)
                    .ToListAsync(ct);
                if (wanted.Except(known).Any())
                {
                    ThrowError("SkillIds must belong to this project.");
                    return;
                }
            }
            entity.SkillIds = wanted.Select(i => i.ToGuid()).ToList();
        }

        // Snapshot the diff from the change tracker before it is flushed.
        var changed = db.Entry(entity).Properties
            .Where(p => p.IsModified && p.Metadata.Name is not ("UpdatedAt" or "CreatedAt"))
            .ToDictionary(p => p.Metadata.Name, p => (object?)new { from = Trim(p.OriginalValue), to = Trim(p.CurrentValue) });

        await db.SaveChangesAsync(ct);

        if (changed.Count > 0)
            await audit.RecordAsync(AgentAuditRecord.ByUser(
                currentUser.OrgId, currentUser.UserId, AgentAuditEventType.AgentConfigChanged,
                $"Agent \"{entity.Name}\" ({entity.Role}) reconfigured by {currentUser.UserId}: {string.Join(", ", changed.Keys)}",
                projectId: projectId, agentId: entity.Id,
                metadata: new { changed }), ct);

        // Config edits don't touch usage, but the response is the same shape the list returns and
        // the client writes it straight back into its cache — omitting Billed here would blank
        // the agent's token figures until the next full refresh.
        var billed = await usageQuery.GetAgentTokenTotalsAsync(
            currentUser.OrgId, projectId, [entity.Id], ct);

        await Send.OkAsync(entity.ToResponse(billed.GetValueOrDefault(entity.Id)), ct);
    }

    /// <summary>Instructions can be pages long — the trail keeps a preview, not the essay.</summary>
    private static object? Trim(object? value) => value switch
    {
        string s when s.Length > 300 => s[..300] + "…",
        List<Guid> ids => ids.Select(i => new Ulid(i).ToString()).ToList(),
        _ => value,
    };
}
