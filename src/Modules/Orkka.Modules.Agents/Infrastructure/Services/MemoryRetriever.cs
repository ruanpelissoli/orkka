namespace Orkka.Modules.Agents.Infrastructure.Services;

using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Domain;

/// <summary>Limits shared by the tools, endpoints and extractor.</summary>
public static class MemoryRules
{
    public const int TitleMax = 200;
    public const int ContentMax = 4_000;
    public const int MaxPerProject = 2_000;
    /// <summary>Tokens taken from the query text for the OR-search.</summary>
    public const int MaxQueryTokens = 24;

    private static readonly Regex Token = new(@"[\p{L}\p{N}][\p{L}\p{N}_\-\.]{2,}", RegexOptions.Compiled);
    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the","and","for","with","that","this","from","into","when","then","than","have","has","are","was","were",
        "will","should","would","could","must","not","but","you","your","our","all","any","can","use","using","used",
        "add","make","new","one","two","also","its","it's","via","per","each","only","just","like","more","most",
        "some","such","them","they","their","there","here","what","which","who","how","why","does","did","done",
        "task","tasks","please","need","needs","want","wants","user","users","should","implement","create","update",
    };

    /// <summary>Distinct content words of the work description as an SQLite FTS5 MATCH
    /// expression: each word an FTS5 string, OR-joined — <c>"login" OR "button" OR "oauth"</c>.
    /// Quoting means no user text is ever read as FTS5 syntax (a stray <c>-</c>, <c>*</c>,
    /// <c>NEAR</c> or column filter), so any input is a valid query. Null when nothing usable —
    /// the caller then skips the ranked step.</summary>
    public static string? BuildSearchQuery(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tokens = new List<string>();
        foreach (Match m in Token.Matches(text))
        {
            var t = m.Value.Trim('.', '-', '_');
            if (t.Length < 3 || Stop.Contains(t) || !seen.Add(t)) continue;
            // An FTS5 string: a double quote inside is doubled (the token regex admits none,
            // but the quoting must hold whatever the regex becomes).
            tokens.Add("\"" + t.Replace("\"", "\"\"") + "\"");
            if (tokens.Count >= MaxQueryTokens) break;
        }
        return tokens.Count == 0 ? null : string.Join(" OR ", tokens);
    }
}

/// <summary>
/// Picks the memories worth an agent's attention for a piece of work. Three tiers, all
/// scoped to the project and to (team ∪ this agent), never archived:
///   1. standing rules — pinned rows plus every Decision / Convention / Preference (newest first);
///   2. relevance — SQLite FTS5 (bm25) rank of title+content against the work's content words;
///   3. recency — the agent's newest Learning / Mistake / Pattern rows.
/// Deduplicated, capped by <see cref="MemoryQuery.Take"/> and the caller's char budget
/// (rendered by <see cref="MemoryPromptSection"/>). Rows returned get their use counters bumped
/// so the Memory page can show what actually influences runs.
/// </summary>
public sealed class MemoryRetriever(AgentsDbContext db)
{
    public async Task<IReadOnlyList<MemoryItem>> RetrieveAsync(MemoryQuery query, CancellationToken ct = default)
    {
        var take = Math.Clamp(query.Take, 1, 100);
        var scope = db.ProjectMemories.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.OrgId == query.OrgId && m.ProjectId == query.ProjectId && !m.IsArchived
                        && (m.AgentId == null || m.AgentId == query.AgentId));

        var picked = new List<ProjectMemory>();
        var seen = new HashSet<Ulid>();
        void Add(IEnumerable<ProjectMemory> rows)
        {
            foreach (var r in rows)
            {
                if (picked.Count >= take) return;
                if (seen.Add(r.Id)) picked.Add(r);
            }
        }

        // Tier 1: rules. Bounded so a project that "pins everything" still leaves room.
        var rulesTake = Math.Max(3, take / 2);
        Add(await scope
            .Where(m => m.IsPinned || m.Type == MemoryType.Decision || m.Type == MemoryType.Convention || m.Type == MemoryType.Preference)
            .OrderByDescending(m => m.IsPinned).ThenByDescending(m => m.CreatedAt)
            .Take(rulesTake)
            .ToListAsync(ct));

        // Tier 2: relevance (only on a relational provider — the in-memory test provider has no FTS).
        var search = MemoryRules.BuildSearchQuery(query.QueryText);
        if (search is not null && picked.Count < take && db.Database.IsRelational())
        {
            Add(await MemorySearch.RankedAsync(db, scope, query.OrgId, query.ProjectId, search, take, ct));
        }

        // Tier 3: the agent's own recent experience.
        if (picked.Count < take && query.AgentId is { } agentId)
        {
            Add(await scope
                .Where(m => m.AgentId == agentId)
                .OrderByDescending(m => m.CreatedAt)
                .Take(take - picked.Count)
                .ToListAsync(ct));
        }
        // …and team learnings if there is still room (small projects: everything fits).
        if (picked.Count < take)
        {
            Add(await scope
                .OrderByDescending(m => m.CreatedAt)
                .Take(take - picked.Count + seen.Count)
                .ToListAsync(ct));
        }

        if (picked.Count > 0 && db.Database.IsRelational())
        {
            var ids = picked.Select(p => p.Id).ToList();
            var now = DateTimeOffset.UtcNow;
            // Fire-and-forget bookkeeping — never let it fail the prompt build.
            try
            {
                await db.ProjectMemories.IgnoreQueryFilters()
                    .Where(m => ids.Contains(m.Id))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(m => m.UseCount, m => m.UseCount + 1)
                        .SetProperty(m => m.LastUsedAt, now), ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested) { }
        }

        return picked.Select(ToItem).ToList();
    }

    public static MemoryItem ToItem(ProjectMemory m) =>
        new(m.Id, m.Type, m.Title, m.Content, m.AgentId, m.IsPinned, m.CreatedAt);
}
