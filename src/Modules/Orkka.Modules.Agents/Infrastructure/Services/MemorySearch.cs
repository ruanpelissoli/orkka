namespace Orkka.Modules.Agents.Infrastructure.Services;

using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Agents.Domain;

/// <summary>
/// Full-text relevance over project memories: SQLite FTS5 on <c>project_memories_fts</c>
/// (Title, Content; porter stemming, diacritics folded), ranked by <c>bm25()</c>. The FTS table
/// keeps its own copy of the text keyed by the memory's <c>Id</c> — not an external-content
/// table over rowids, which VACUUM may renumber on a table without an INTEGER PRIMARY KEY — and
/// AFTER INSERT/UPDATE/DELETE triggers on <c>project_memories</c> keep it in step (Agents
/// Initial migration).
/// </summary>
public static class MemorySearch
{
    public const string FtsTable = "project_memories_fts";

    /// <summary>
    /// The rows of <paramref name="scope"/> that match <paramref name="match"/> (an FTS5
    /// expression from <see cref="MemoryRules.BuildSearchQuery"/>), best first, ties to the
    /// newest. The FTS hit list is narrowed to the project in SQL; every other filter the
    /// caller composed on <paramref name="scope"/> still applies.
    /// </summary>
    public static async Task<List<ProjectMemory>> RankedAsync(
        AgentsDbContext db, IQueryable<ProjectMemory> scope,
        Ulid orgId, Ulid projectId, string match, int take, CancellationToken ct)
    {
        // Ids as SQLite stores them (EF writes a Guid as upper-case TEXT).
        var org = orgId.ToGuid().ToString().ToUpperInvariant();
        var project = projectId.ToGuid().ToString().ToUpperInvariant();

        var hits = await db.Database.SqlQuery<FtsHit>($"""
            SELECT f."Id" AS "Id", bm25(project_memories_fts) AS "Rank"
            FROM project_memories_fts AS f
            INNER JOIN "project_memories" AS m ON m."Id" = f."Id"
            WHERE project_memories_fts MATCH {match}
              AND m."OrgId" = {org} AND m."ProjectId" = {project}
            """).ToListAsync(ct);
        if (hits.Count == 0) return [];

        // bm25: lower is better.
        var rank = hits.ToDictionary(h => new Ulid(Guid.Parse(h.Id)), h => h.Rank);
        var ids = rank.Keys.ToList();
        var rows = await scope.Where(m => ids.Contains(m.Id)).ToListAsync(ct);

        return rows
            .OrderBy(m => rank[m.Id])
            .ThenByDescending(m => m.CreatedAt)
            .Take(take)
            .ToList();
    }

    internal sealed class FtsHit
    {
        public string Id { get; init; } = "";
        public double Rank { get; init; }
    }
}
