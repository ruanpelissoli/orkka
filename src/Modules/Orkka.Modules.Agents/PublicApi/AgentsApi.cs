namespace Orkka.Modules.Agents.PublicApi;

using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Domain;
using Orkka.Modules.Agents.Infrastructure;

/// <summary>Module implementation of <see cref="IAgentsApi"/>. Tenant filtering follows the
/// ambient <c>ICurrentUser</c> of the scope: org-scoped in the API host, unfiltered in the Worker.</summary>
internal sealed class AgentsApi(AgentsDbContext db) : IAgentsApi
{
    public async Task<IReadOnlyDictionary<Ulid, string>> GetAgentNamesAsync(
        IReadOnlyCollection<Ulid> agentIds, CancellationToken ct = default)
        => await db.ProjectAgents.AsNoTracking()
            .Where(a => agentIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => a.Name, ct);

    public async Task<AgentSummary?> GetAgentAsync(Ulid agentId, CancellationToken ct = default)
    {
        var agent = await db.ProjectAgents.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == agentId, ct);

        return agent is null ? null : ToSummary(agent);
    }

    public async Task<IReadOnlyList<AgentSummary>> GetAgentsAsync(
        IReadOnlyCollection<Ulid> agentIds, CancellationToken ct = default)
        => (await db.ProjectAgents.AsNoTracking()
            .Where(a => agentIds.Contains(a.Id))
            .ToListAsync(ct))
            .Select(ToSummary).ToList();

    public async Task<IReadOnlyList<AgentSummary>> ListAgentsAsync(
        Ulid projectId, CancellationToken ct = default)
        => (await db.ProjectAgents.AsNoTracking()
            .Where(a => a.ProjectId == projectId)
            .ToListAsync(ct))
            .Select(ToSummary).ToList();

    public async Task RecordAgentRunResultAsync(
        Ulid agentId, long tokensInDelta, long tokensOutDelta,
        int completedTasksDelta, int failedTasksDelta, CancellationToken ct = default)
    {
        await db.ProjectAgents
            .Where(a => a.Id == agentId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.TotalTokensIn, a => a.TotalTokensIn + tokensInDelta)
                .SetProperty(a => a.TotalTokensOut, a => a.TotalTokensOut + tokensOutDelta)
                .SetProperty(a => a.TasksCompleted, a => a.TasksCompleted + completedTasksDelta)
                .SetProperty(a => a.TasksFailed, a => a.TasksFailed + failedTasksDelta), ct);
    }

    public Task<IReadOnlyList<MemoryItem>> RetrieveMemoriesAsync(MemoryQuery query, CancellationToken ct = default)
        => new Infrastructure.Services.MemoryRetriever(db).RetrieveAsync(query, ct);

    public async Task<Ulid> AddMemoryAsync(NewMemory memory, CancellationToken ct = default)
    {
        var title = Clip(memory.Title.Trim(), Infrastructure.Services.MemoryRules.TitleMax);
        var content = Clip(memory.Content.Trim(), Infrastructure.Services.MemoryRules.ContentMax);
        if (title.Length == 0) title = Clip(content.ReplaceLineEndings(" "), 120);

        ProjectMemory? existing = null;
        if (memory.DedupeKey is not null)
            existing = await db.ProjectMemories.IgnoreQueryFilters()
                .FirstOrDefaultAsync(m => m.ProjectId == memory.ProjectId && m.DedupeKey == memory.DedupeKey, ct);

        if (existing is null)
        {
            // The cap belongs here, on the one path every writer shares — the agent-side
            // writers (pipeline `remember`, the post-run extractor) never had a check of
            // their own, so the callers most likely to flood a project were the ones the
            // ceiling didn't apply to. Only a genuinely new row counts: replacing a row
            // through its dedupe key doesn't grow the project.
            var active = await db.ProjectMemories.IgnoreQueryFilters()
                .CountAsync(m => m.OrgId == memory.OrgId && m.ProjectId == memory.ProjectId && !m.IsArchived, ct);
            if (active >= Infrastructure.Services.MemoryRules.MaxPerProject)
                return Ulid.Empty;

            existing = new ProjectMemory
            {
                Id = Ulid.NewUlid(),
                OrgId = memory.OrgId,
                ProjectId = memory.ProjectId,
                Title = title,
                Content = content,
                Source = memory.Source,
                CreatedByUserId = memory.CreatedByUserId,
                DedupeKey = memory.DedupeKey,
            };
            db.ProjectMemories.Add(existing);
        }
        existing.AgentId = memory.AgentId;
        existing.TaskId = memory.TaskId;
        existing.ExecutionId = memory.ExecutionId;
        existing.Type = memory.Type;
        existing.Title = title;
        existing.Content = content;
        existing.IsPinned = existing.IsPinned || memory.IsPinned;
        existing.IsArchived = false;

        await db.SaveChangesAsync(ct);
        return existing.Id;
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];

    public async Task<IReadOnlyList<AgentSkillSummary>> ListAgentSkillsAsync(
        Ulid agentId, CancellationToken ct = default)
    {
        // SkillIds is a JSON id list; the id match happens in memory because the Guid→Ulid element
        // conversion isn't SQL-translatable (same reason ToSummary maps RepoIds in memory).
        var agent = await db.ProjectAgents.AsNoTracking()
            .Where(a => a.Id == agentId)
            .Select(a => new { a.ProjectId, a.SkillIds })
            .FirstOrDefaultAsync(ct);
        if (agent is null || agent.SkillIds.Count == 0)
            return [];

        var ids = agent.SkillIds.Select(g => new Ulid(g)).ToHashSet();
        var skills = await db.AgentSkills.AsNoTracking()
            .Where(s => s.ProjectId == agent.ProjectId && s.IsEnabled)
            .ToListAsync(ct);

        return skills
            .Where(s => ids.Contains(s.Id))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(s => new AgentSkillSummary(s.Id, s.Slug, s.Name, s.Description, s.Content))
            .ToList();
    }

    public async Task<IReadOnlyList<ApprovedDesignAssetInfo>> ListApprovedDesignAssetsAsync(
        Ulid projectId, int take, CancellationToken ct = default)
        => await db.DesignAssets.AsNoTracking()
            .Where(a => a.ProjectId == projectId && a.Status == DesignAssetStatus.Approved)
            .OrderByDescending(a => a.CreatedAt)
            .Take(take)
            .Select(a => new ApprovedDesignAssetInfo(
                a.Id, a.Name, a.FileName, a.Kind, a.MimeType, a.BlobContainer, a.BlobName))
            .ToListAsync(ct);

    public async Task<bool> ConversationBelongsToOrgAsync(
        Ulid conversationId, Ulid orgId, CancellationToken ct = default)
        // Explicit OrgId predicate: hub callers resolve outside an authenticated scope,
        // so the tenant query filter may be off.
        => await db.AgentConversations.AsNoTracking()
            .AnyAsync(c => c.Id == conversationId && c.OrgId == orgId, ct);

    public async Task<int> DeleteTaskConversationsAsync(Ulid orgId, Ulid taskId, CancellationToken ct = default)
    {
        var ids = await db.AgentConversations
            .Where(c => c.OrgId == orgId && c.TaskId == taskId)
            .Select(c => c.Id)
            .ToListAsync(ct);
        if (ids.Count == 0)
            return 0;
        await db.AgentMessages.Where(m => m.OrgId == orgId && ids.Contains(m.ConversationId)).ExecuteDeleteAsync(ct);
        return await db.AgentConversations.Where(c => ids.Contains(c.Id)).ExecuteDeleteAsync(ct);
    }

    public async Task<Ulid?> GetConversationProjectIdAsync(
        Ulid conversationId, Ulid orgId, CancellationToken ct = default)
        => await db.AgentConversations.AsNoTracking()
            .Where(c => c.Id == conversationId && c.OrgId == orgId)
            .Select(c => (Ulid?)c.ProjectId)
            .FirstOrDefaultAsync(ct);

    // In-memory mapping (not an EF projection): RepoIds is a JSON id list and the Guid→Ulid
    // element conversion isn't SQL-translatable.
    private static AgentSummary ToSummary(ProjectAgent a) => new(
        a.Id, a.OrgId, a.ProjectId, a.RepoIds.Select(g => new Ulid(g)).ToList(),
        a.Name, a.Role, a.Model, a.SystemPrompt, a.UserInstructions);
}
