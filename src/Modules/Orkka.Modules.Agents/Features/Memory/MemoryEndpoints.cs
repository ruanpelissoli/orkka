namespace Orkka.Modules.Agents.Features.Memory;

using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Domain;
using Orkka.Modules.Agents.Infrastructure;
using Orkka.Modules.Agents.Infrastructure.Services;
using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Security;

public sealed record MemoryResponse(
    Ulid Id,
    Ulid ProjectId,
    Ulid? AgentId,
    string? AgentName,
    string Type,
    string Title,
    string Content,
    string Source,
    string? CreatedByUserId,
    Ulid? TaskId,
    bool IsPinned,
    bool IsArchived,
    int UseCount,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record MemoryPage(IReadOnlyList<MemoryResponse> Items, string? NextCursor, int Total);

public sealed record MemoryListRequest
{
    /// <summary>Free text — full-text ranked when present.</summary>
    public string? Q { get; init; }
    /// <summary>Comma-separated MemoryType names.</summary>
    public string? Type { get; init; }
    /// <summary>"team" = shared only, or an agent id = that agent's private notes (+ team when IncludeTeam).</summary>
    public string? Scope { get; init; }
    public bool? Pinned { get; init; }
    public bool Archived { get; init; }
    public string? Cursor { get; init; }
    public int? Limit { get; init; }
}

public sealed record UpsertMemoryRequest(
    string Title,
    string Content,
    string Type = "Learning",
    /// <summary>Null = shared with the team; an agent id = private to that agent.</summary>
    string? AgentId = null,
    bool IsPinned = false,
    bool IsArchived = false);

public sealed record RetrievePreviewRequest(string? Q, string? AgentId, int? Take);

internal static class MemoryMappers
{
    public static MemoryResponse ToResponse(this ProjectMemory m, IReadOnlyDictionary<Ulid, string> agentNames) => new(
        m.Id, m.ProjectId, m.AgentId,
        m.AgentId is { } a && agentNames.TryGetValue(a, out var name) ? name : null,
        m.Type.ToString(), m.Title, m.Content, m.Source.ToString(), m.CreatedByUserId, m.TaskId,
        m.IsPinned, m.IsArchived, m.UseCount, m.LastUsedAt, m.CreatedAt, m.UpdatedAt);

    public static async Task<IReadOnlyDictionary<Ulid, string>> AgentNamesAsync(AgentsDbContext db, Ulid orgId, Ulid projectId, CancellationToken ct)
        => await db.ProjectAgents.AsNoTracking()
            .Where(a => a.OrgId == orgId && a.ProjectId == projectId)
            .ToDictionaryAsync(a => a.Id, a => a.Name, ct);
}

/// <summary>The project's memory, newest first (or by relevance when <c>q</c> is given).</summary>
public sealed class ListMemoriesEndpoint(AgentsDbContext db, ICurrentUser currentUser, IProjectResolver projectResolver)
    : Endpoint<MemoryListRequest, MemoryPage>
{
    public override void Configure() => Get("/api/projects/{projectId}/memories");

    public override async Task HandleAsync(MemoryListRequest req, CancellationToken ct)
    {
        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId) { await Send.NotFoundAsync(ct); return; }

        var take = Math.Clamp(req.Limit ?? 50, 1, 200);
        var query = db.ProjectMemories.AsNoTracking()
            .Where(m => m.OrgId == currentUser.OrgId && m.ProjectId == projectId && m.IsArchived == req.Archived);

        if (!string.IsNullOrWhiteSpace(req.Type))
        {
            var types = new List<MemoryType>();
            foreach (var raw in req.Type.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!Enum.TryParse<MemoryType>(raw, true, out var t)) { ThrowError($"Unknown memory type \"{raw}\"."); return; }
                types.Add(t);
            }
            query = query.Where(m => types.Contains(m.Type));
        }
        if (string.Equals(req.Scope, "team", StringComparison.OrdinalIgnoreCase))
            query = query.Where(m => m.AgentId == null);
        else if (Ulid.TryParse(req.Scope, out var agentId))
            query = query.Where(m => m.AgentId == agentId);
        if (req.Pinned is { } pinned)
            query = query.Where(m => m.IsPinned == pinned);

        var search = MemoryRules.BuildSearchQuery(req.Q);
        List<ProjectMemory> rows;
        string? next = null;
        int total;
        if (search is not null)
        {
            // Relevance mode: ranked, no cursor (a search result page is small by construction).
            rows = await MemorySearch.RankedAsync(db, query, currentUser.OrgId, projectId, search, take, ct);
            total = rows.Count;
        }
        else
        {
            total = await query.CountAsync(ct);
            if (Ulid.TryParse(req.Cursor, out var cursor))
                query = query.Where(m => m.Id.CompareTo(cursor) < 0);
            rows = await query.OrderByDescending(m => m.IsPinned).ThenByDescending(m => m.Id).Take(take + 1).ToListAsync(ct);
            if (rows.Count > take) { next = rows[take - 1].Id.ToString(); rows.RemoveAt(rows.Count - 1); }
        }

        var names = await MemoryMappers.AgentNamesAsync(db, currentUser.OrgId, projectId, ct);
        await Send.OkAsync(new MemoryPage(rows.Select(r => r.ToResponse(names)).ToList(), next, total), ct);
    }
}

/// <summary>"What would an agent see for this work?" — the exact retrieval a run performs,
/// without bumping use counters. Lets a human check that a decision actually surfaces.</summary>
public sealed class PreviewMemoryRetrievalEndpoint(AgentsDbContext db, ICurrentUser currentUser, IProjectResolver projectResolver)
    : Endpoint<RetrievePreviewRequest, List<MemoryResponse>>
{
    public override void Configure() => Get("/api/projects/{projectId}/memories/retrieve");

    public override async Task HandleAsync(RetrievePreviewRequest req, CancellationToken ct)
    {
        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId) { await Send.NotFoundAsync(ct); return; }
        Ulid? agentId = Ulid.TryParse(req.AgentId, out var a) ? a : null;

        var items = await new MemoryRetriever(db).RetrieveAsync(
            new MemoryQuery(currentUser.OrgId, projectId, agentId, req.Q, Take: Math.Clamp(req.Take ?? 20, 1, 50)), ct);
        var ids = items.Select(i => i.Id).ToList();
        var rows = await db.ProjectMemories.AsNoTracking().Where(m => ids.Contains(m.Id)).ToListAsync(ct);
        var byId = rows.ToDictionary(r => r.Id);
        var names = await MemoryMappers.AgentNamesAsync(db, currentUser.OrgId, projectId, ct);
        // Preserve retrieval order; undo the use-count bump this preview caused.
        var ordered = items.Where(i => byId.ContainsKey(i.Id)).Select(i => byId[i.Id].ToResponse(names)).ToList();
        if (ids.Count > 0)
            await db.ProjectMemories.Where(m => ids.Contains(m.Id) && m.UseCount > 0)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.UseCount, m => m.UseCount - 1), ct);
        await Send.OkAsync(ordered, ct);
    }
}

public sealed class CreateMemoryEndpoint(AgentsDbContext db, ICurrentUser currentUser, IProjectResolver projectResolver, IAgentsApi agentsApi, IAgentAuditLog audit)
    : Endpoint<UpsertMemoryRequest, MemoryResponse>
{
    public override void Configure() => Post("/api/projects/{projectId}/memories");

    public override async Task HandleAsync(UpsertMemoryRequest req, CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.AgentsConfigure))
            return;
        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId) { await Send.NotFoundAsync(ct); return; }

        var (type, agentId, error) = await MemoryValidation.ParseAsync(db, currentUser.OrgId, projectId, req, ct);
        if (error is not null) { ThrowError(error); return; }

        // The cap lives in AddMemoryAsync, on the path every writer shares; an empty id back
        // means it refused. No pre-count here — it was a second query that could still lose
        // the race with a concurrent agent write.
        var id = await agentsApi.AddMemoryAsync(new NewMemory(
            currentUser.OrgId, projectId, agentId, type, req.Title, req.Content, MemorySource.User,
            CreatedByUserId: currentUser.UserId, IsPinned: req.IsPinned), ct);
        if (id == Ulid.Empty) { ThrowError($"A project can hold at most {MemoryRules.MaxPerProject} active memories — archive some first.", 409); return; }
        var row = await db.ProjectMemories.AsNoTracking().FirstAsync(m => m.Id == id, ct);

        await audit.RecordAsync(AgentAuditRecord.ByUser(
            currentUser.OrgId, currentUser.UserId, AgentAuditEventType.MemoryChanged,
            $"Memory \"{row.Title}\" ({row.Type}) added by {currentUser.UserId}",
            projectId: projectId, agentId: agentId,
            metadata: new { memoryId = id.ToString(), change = "created", type = row.Type.ToString(), scope = agentId is null ? "team" : "agent", row.IsPinned }), ct);

        var names = await MemoryMappers.AgentNamesAsync(db, currentUser.OrgId, projectId, ct);
        await Send.CreatedAtAsync<ListMemoriesEndpoint>(new { projectId = Route<string>("projectId") }, row.ToResponse(names), cancellation: ct);
    }
}

public sealed class UpdateMemoryEndpoint(AgentsDbContext db, ICurrentUser currentUser, IProjectResolver projectResolver, IAgentAuditLog audit)
    : Endpoint<UpsertMemoryRequest, MemoryResponse>
{
    public override void Configure() => Put("/api/projects/{projectId}/memories/{memoryId:ulid}");

    public override async Task HandleAsync(UpsertMemoryRequest req, CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.AgentsConfigure))
            return;
        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId) { await Send.NotFoundAsync(ct); return; }
        var memoryId = Ulid.Parse(Route<string>("memoryId"));
        var row = await db.ProjectMemories.FirstOrDefaultAsync(m => m.Id == memoryId && m.ProjectId == projectId && m.OrgId == currentUser.OrgId, ct);
        if (row is null) { await Send.NotFoundAsync(ct); return; }

        var (type, agentId, error) = await MemoryValidation.ParseAsync(db, currentUser.OrgId, projectId, req, ct);
        if (error is not null) { ThrowError(error); return; }

        row.Title = req.Title.Trim()[..Math.Min(req.Title.Trim().Length, MemoryRules.TitleMax)];
        row.Content = req.Content.Trim()[..Math.Min(req.Content.Trim().Length, MemoryRules.ContentMax)];
        row.Type = type;
        row.AgentId = agentId;
        row.IsPinned = req.IsPinned;
        row.IsArchived = req.IsArchived;
        var changed = db.Entry(row).Properties.Where(p => p.IsModified && p.Metadata.Name != "UpdatedAt").Select(p => p.Metadata.Name).ToList();
        await db.SaveChangesAsync(ct);

        if (changed.Count > 0)
            await audit.RecordAsync(AgentAuditRecord.ByUser(
                currentUser.OrgId, currentUser.UserId, AgentAuditEventType.MemoryChanged,
                $"Memory \"{row.Title}\" updated by {currentUser.UserId}: {string.Join(", ", changed)}",
                projectId: projectId, agentId: agentId,
                metadata: new { memoryId = row.Id.ToString(), change = "updated", fields = changed, row.IsPinned, row.IsArchived }), ct);

        var names = await MemoryMappers.AgentNamesAsync(db, currentUser.OrgId, projectId, ct);
        await Send.OkAsync(row.ToResponse(names), ct);
    }
}

public sealed class DeleteMemoryEndpoint(AgentsDbContext db, ICurrentUser currentUser, IProjectResolver projectResolver, IAgentAuditLog audit)
    : EndpointWithoutRequest
{
    public override void Configure() => Delete("/api/projects/{projectId}/memories/{memoryId:ulid}");

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.AgentsConfigure))
            return;
        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId) { await Send.NotFoundAsync(ct); return; }
        var memoryId = Ulid.Parse(Route<string>("memoryId"));
        var row = await db.ProjectMemories.FirstOrDefaultAsync(m => m.Id == memoryId && m.ProjectId == projectId && m.OrgId == currentUser.OrgId, ct);
        if (row is null) { await Send.NotFoundAsync(ct); return; }

        db.ProjectMemories.Remove(row);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(AgentAuditRecord.ByUser(
            currentUser.OrgId, currentUser.UserId, AgentAuditEventType.MemoryChanged,
            $"Memory \"{row.Title}\" deleted by {currentUser.UserId}",
            projectId: projectId, agentId: row.AgentId,
            metadata: new { memoryId = row.Id.ToString(), change = "deleted", type = row.Type.ToString() }), ct);
        await Send.NoContentAsync(ct);
    }
}

internal static class MemoryValidation
{
    public static async Task<(MemoryType Type, Ulid? AgentId, string? Error)> ParseAsync(
        AgentsDbContext db, Ulid orgId, Ulid projectId, UpsertMemoryRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Title)) return (default, null, "Title is required.");
        if (string.IsNullOrWhiteSpace(req.Content)) return (default, null, "Content is required.");
        if (req.Title.Trim().Length > MemoryRules.TitleMax) return (default, null, $"Title must be at most {MemoryRules.TitleMax} characters.");
        if (req.Content.Trim().Length > MemoryRules.ContentMax) return (default, null, $"Content must be at most {MemoryRules.ContentMax} characters.");
        if (!Enum.TryParse<MemoryType>(req.Type, true, out var type)) return (default, null, "Unknown memory type.");
        Ulid? agentId = null;
        if (!string.IsNullOrWhiteSpace(req.AgentId))
        {
            if (!Ulid.TryParse(req.AgentId, out var parsed)) return (default, null, "AgentId must be a valid agent id.");
            var known = await db.ProjectAgents.AsNoTracking().AnyAsync(a => a.Id == parsed && a.OrgId == orgId && a.ProjectId == projectId, ct);
            if (!known) return (default, null, "AgentId must belong to this project.");
            agentId = parsed;
        }
        return (type, agentId, null);
    }
}
