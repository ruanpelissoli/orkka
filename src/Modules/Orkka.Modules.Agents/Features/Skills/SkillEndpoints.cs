namespace Orkka.Modules.Agents.Features.Skills;

using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Domain;
using Orkka.Modules.Agents.Infrastructure;
using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Security;

/// <summary>Shared lookups for the skill endpoints.</summary>
internal static class SkillQueries
{
    /// <summary>Which agents carry which skill — one query for the whole project, since the
    /// membership lives on the agent row (a JSON id list), not on the skill.</summary>
    public static async Task<Dictionary<Ulid, List<Ulid>>> AssignmentsAsync(
        AgentsDbContext db, Ulid orgId, Ulid projectId, CancellationToken ct)
    {
        var agents = await db.ProjectAgents.AsNoTracking()
            .Where(a => a.OrgId == orgId && a.ProjectId == projectId)
            .Select(a => new { a.Id, a.SkillIds })
            .ToListAsync(ct);

        var map = new Dictionary<Ulid, List<Ulid>>();
        foreach (var agent in agents)
            foreach (var g in agent.SkillIds)
            {
                var skillId = new Ulid(g);
                if (!map.TryGetValue(skillId, out var list)) map[skillId] = list = [];
                list.Add(agent.Id);
            }
        return map;
    }
}

public sealed class ListSkillsEndpoint(
    AgentsDbContext db, ICurrentUser currentUser, IProjectResolver projectResolver)
    : EndpointWithoutRequest<List<SkillResponse>>
{
    public override void Configure() => Get("/api/projects/{projectId}/skills");

    public override async Task HandleAsync(CancellationToken ct)
    {
        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var skills = await db.AgentSkills.AsNoTracking()
            .Where(s => s.OrgId == currentUser.OrgId && s.ProjectId == projectId)
            .OrderBy(s => s.Name)
            .ToListAsync(ct);
        var assignments = await SkillQueries.AssignmentsAsync(db, currentUser.OrgId, projectId, ct);

        await Send.OkAsync(
            skills.Select(s => s.ToResponse(assignments.GetValueOrDefault(s.Id) ?? [])).ToList(), ct);
    }
}

public sealed class GetSkillEndpoint(
    AgentsDbContext db, ICurrentUser currentUser, IProjectResolver projectResolver)
    : EndpointWithoutRequest<SkillResponse>
{
    public override void Configure() => Get("/api/projects/{projectId}/skills/{skillId:ulid}");

    public override async Task HandleAsync(CancellationToken ct)
    {
        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        var skillId = Ulid.Parse(Route<string>("skillId"));

        var skill = await db.AgentSkills.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == skillId && s.ProjectId == projectId && s.OrgId == currentUser.OrgId, ct);
        if (skill is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var assignments = await SkillQueries.AssignmentsAsync(db, currentUser.OrgId, projectId, ct);
        await Send.OkAsync(skill.ToResponse(assignments.GetValueOrDefault(skill.Id) ?? []), ct);
    }
}

public sealed class CreateSkillEndpoint(
    AgentsDbContext db, ICurrentUser currentUser, IProjectResolver projectResolver, IAgentAuditLog audit)
    : Endpoint<CreateSkillRequest, SkillResponse>
{
    public override void Configure() => Post("/api/projects/{projectId}/skills");

    public override async Task HandleAsync(CreateSkillRequest req, CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.AgentsConfigure))
            return;

        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var name = req.Name?.Trim() ?? "";
        if (name.Length is 0 or > SkillRules.NameMax)
        {
            AddError(r => r.Name, $"Name is required (max {SkillRules.NameMax} characters).");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }
        var description = req.Description?.Trim() ?? "";
        if (description.Length > SkillRules.DescriptionMax)
        {
            AddError(r => r.Description, $"Description must be at most {SkillRules.DescriptionMax} characters.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }
        var content = req.Content ?? "";
        if (content.Length > SkillRules.ContentMax)
        {
            AddError(r => r.Content, $"Content must be at most {SkillRules.ContentMax} characters.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        var slug = string.IsNullOrWhiteSpace(req.Slug) ? SkillRules.SlugFromName(name) : req.Slug.Trim().ToLowerInvariant();
        if (!SkillRules.IsValidSlug(slug))
        {
            AddError(r => r.Slug, "Slug must be lowercase letters, digits and dashes (max 64).");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }
        if (await db.AgentSkills.AnyAsync(s => s.OrgId == currentUser.OrgId && s.ProjectId == projectId && s.Slug == slug, ct))
        {
            AddError(r => r.Slug, $"A skill with slug \"{slug}\" already exists in this project.");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        var skill = new AgentSkill
        {
            // Explicit: the id goes into the agents' id lists below, before SaveChanges would
            // otherwise assign it.
            Id = Ulid.NewUlid(),
            OrgId = currentUser.OrgId,
            ProjectId = projectId,
            Slug = slug,
            Name = name,
            Description = description,
            Content = content,
            IsEnabled = req.IsEnabled,
            IsBuiltIn = false,
            CreatedByUserId = currentUser.UserId,
            UpdatedByUserId = currentUser.UserId,
        };
        db.AgentSkills.Add(skill);

        var assigned = new List<Ulid>();
        if (req.AgentIds is { Count: > 0 })
        {
            var ids = new List<Ulid>();
            foreach (var raw in req.AgentIds)
            {
                if (!Ulid.TryParse(raw, out var id))
                {
                    AddError(r => r.AgentIds, "AgentIds must contain valid agent ids.");
                    await Send.ErrorsAsync(cancellation: ct);
                    return;
                }
                ids.Add(id);
            }
            var agents = await db.ProjectAgents
                .Where(a => a.OrgId == currentUser.OrgId && a.ProjectId == projectId && ids.Contains(a.Id))
                .ToListAsync(ct);
            var guid = skill.Id.ToGuid();
            foreach (var agent in agents)
            {
                if (agent.SkillIds.Contains(guid)) continue;
                agent.SkillIds = [.. agent.SkillIds, guid];
                assigned.Add(agent.Id);
            }
        }

        await db.SaveChangesAsync(ct);

        await audit.RecordAsync(AgentAuditRecord.ByUser(
            currentUser.OrgId, currentUser.UserId, AgentAuditEventType.SkillChanged,
            $"Skill \"{skill.Name}\" (/{skill.Slug}) created by {currentUser.UserId}",
            projectId: projectId,
            metadata: new { skillId = skill.Id.ToString(), skill.Slug, change = "created", skill.IsEnabled, assignedAgents = assigned.Select(a => a.ToString()).ToList() }), ct);

        await Send.CreatedAtAsync<GetSkillEndpoint>(
            new { projectId = Route<string>("projectId"), skillId = skill.Id },
            skill.ToResponse(assigned), cancellation: ct);
    }
}

public sealed class UpdateSkillEndpoint(
    AgentsDbContext db, ICurrentUser currentUser, IProjectResolver projectResolver, IAgentAuditLog audit)
    : Endpoint<UpdateSkillRequest, SkillResponse>
{
    public override void Configure() => Put("/api/projects/{projectId}/skills/{skillId:ulid}");

    public override async Task HandleAsync(UpdateSkillRequest req, CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.AgentsConfigure))
            return;

        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        var skillId = Ulid.Parse(Route<string>("skillId"));

        var skill = await db.AgentSkills
            .FirstOrDefaultAsync(s => s.Id == skillId && s.ProjectId == projectId && s.OrgId == currentUser.OrgId, ct);
        if (skill is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (req.Name is not null)
        {
            var name = req.Name.Trim();
            if (name.Length is 0 or > SkillRules.NameMax)
            {
                AddError(r => r.Name, $"Name is required (max {SkillRules.NameMax} characters).");
                await Send.ErrorsAsync(cancellation: ct);
                return;
            }
            skill.Name = name;
        }
        if (req.Description is not null)
        {
            var description = req.Description.Trim();
            if (description.Length > SkillRules.DescriptionMax)
            {
                AddError(r => r.Description, $"Description must be at most {SkillRules.DescriptionMax} characters.");
                await Send.ErrorsAsync(cancellation: ct);
                return;
            }
            skill.Description = description;
        }
        if (req.Content is not null)
        {
            if (req.Content.Length > SkillRules.ContentMax)
            {
                AddError(r => r.Content, $"Content must be at most {SkillRules.ContentMax} characters.");
                await Send.ErrorsAsync(cancellation: ct);
                return;
            }
            skill.Content = req.Content;
        }
        if (req.Slug is not null)
        {
            var slug = req.Slug.Trim().ToLowerInvariant();
            if (!SkillRules.IsValidSlug(slug))
            {
                AddError(r => r.Slug, "Slug must be lowercase letters, digits and dashes (max 64).");
                await Send.ErrorsAsync(cancellation: ct);
                return;
            }
            if (slug != skill.Slug && await db.AgentSkills.AnyAsync(
                    s => s.OrgId == currentUser.OrgId && s.ProjectId == projectId && s.Slug == slug, ct))
            {
                AddError(r => r.Slug, $"A skill with slug \"{slug}\" already exists in this project.");
                await Send.ErrorsAsync(409, ct);
                return;
            }
            skill.Slug = slug;
        }
        if (req.IsEnabled is { } enabled)
            skill.IsEnabled = enabled;

        skill.UpdatedByUserId = currentUser.UserId;
        var changedFields = db.Entry(skill).Properties
            .Where(p => p.IsModified && p.Metadata.Name is not ("UpdatedAt" or "UpdatedByUserId"))
            .Select(p => p.Metadata.Name)
            .ToList();
        await db.SaveChangesAsync(ct);

        if (changedFields.Count > 0)
            await audit.RecordAsync(AgentAuditRecord.ByUser(
                currentUser.OrgId, currentUser.UserId, AgentAuditEventType.SkillChanged,
                $"Skill \"{skill.Name}\" (/{skill.Slug}) updated by {currentUser.UserId}: {string.Join(", ", changedFields)}",
                projectId: projectId,
                metadata: new { skillId = skill.Id.ToString(), skill.Slug, change = "updated", fields = changedFields, skill.IsEnabled }), ct);

        var assignments = await SkillQueries.AssignmentsAsync(db, currentUser.OrgId, projectId, ct);
        await Send.OkAsync(skill.ToResponse(assignments.GetValueOrDefault(skill.Id) ?? []), ct);
    }
}

public sealed class DeleteSkillEndpoint(
    AgentsDbContext db, ICurrentUser currentUser, IProjectResolver projectResolver, IAgentAuditLog audit)
    : EndpointWithoutRequest
{
    public override void Configure() => Delete("/api/projects/{projectId}/skills/{skillId:ulid}");

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
        var skillId = Ulid.Parse(Route<string>("skillId"));

        var skill = await db.AgentSkills
            .FirstOrDefaultAsync(s => s.Id == skillId && s.ProjectId == projectId && s.OrgId == currentUser.OrgId, ct);
        if (skill is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        if (skill.IsBuiltIn)
        {
            // Like seeded agents: built-ins are permanent. Disable instead.
            ThrowError("Built-in skills can be disabled but not deleted.", 409);
            return;
        }

        // Scrub the id from every agent in the same transaction so no agent references a
        // skill that no longer exists.
        var guid = skill.Id.ToGuid();
        var agents = await db.ProjectAgents
            .Where(a => a.OrgId == currentUser.OrgId && a.ProjectId == projectId)
            .ToListAsync(ct);
        foreach (var agent in agents.Where(a => a.SkillIds.Contains(guid)))
            agent.SkillIds = agent.SkillIds.Where(g => g != guid).ToList();

        db.AgentSkills.Remove(skill);
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync(AgentAuditRecord.ByUser(
            currentUser.OrgId, currentUser.UserId, AgentAuditEventType.SkillChanged,
            $"Skill \"{skill.Name}\" (/{skill.Slug}) deleted by {currentUser.UserId}",
            projectId: projectId,
            metadata: new { skillId = skill.Id.ToString(), skill.Slug, change = "deleted" }), ct);

        await Send.NoContentAsync(ct);
    }
}

/// <summary>Replaces an agent's skill set. Ids must be skills of the same project.</summary>
public sealed class SetAgentSkillsEndpoint(
    AgentsDbContext db, ICurrentUser currentUser, IProjectResolver projectResolver, IAgentAuditLog audit)
    : Endpoint<SetAgentSkillsRequest, List<Ulid>>
{
    public override void Configure() => Put("/api/projects/{projectId}/agents/{agentId:ulid}/skills");

    public override async Task HandleAsync(SetAgentSkillsRequest req, CancellationToken ct)
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

        var agent = await db.ProjectAgents
            .FirstOrDefaultAsync(a => a.Id == agentId && a.ProjectId == projectId && a.OrgId == currentUser.OrgId, ct);
        if (agent is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var ids = new HashSet<Ulid>();
        foreach (var raw in req.SkillIds ?? [])
        {
            if (!Ulid.TryParse(raw, out var id))
            {
                AddError(r => r.SkillIds, "SkillIds must contain valid skill ids.");
                await Send.ErrorsAsync(cancellation: ct);
                return;
            }
            ids.Add(id);
        }

        if (ids.Count > 0)
        {
            var known = await db.AgentSkills.AsNoTracking()
                .Where(s => s.OrgId == currentUser.OrgId && s.ProjectId == projectId)
                .Select(s => s.Id)
                .ToListAsync(ct);
            var unknown = ids.Except(known).ToList();
            if (unknown.Count > 0)
            {
                AddError(r => r.SkillIds, "SkillIds must belong to this project.");
                await Send.ErrorsAsync(cancellation: ct);
                return;
            }
        }

        var previousSkillIds = agent.SkillIds.Select(g => new Ulid(g).ToString()).ToList();
        agent.SkillIds = ids.Select(i => i.ToGuid()).ToList();
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync(AgentAuditRecord.ByUser(
            currentUser.OrgId, currentUser.UserId, AgentAuditEventType.AgentConfigChanged,
            $"Skills of agent \"{agent.Name}\" set by {currentUser.UserId} ({ids.Count} skills)",
            projectId: projectId, agentId: agent.Id,
            metadata: new { changed = new { SkillIds = new { from = previousSkillIds, to = ids.Select(i => i.ToString()).ToList() } } }), ct);

        await Send.OkAsync(ids.ToList(), ct);
    }
}
