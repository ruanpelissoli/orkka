namespace Orkka.Modules.Agents.Features.Diagrams;

using Orkka.Modules.Agents.Infrastructure;
using Orkka.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;

/// <summary>Batch-resolves actor display names for diagram version rows: agent names from
/// this module's store, user names from the host's user directory.</summary>
internal static class DiagramVersionActorNames
{
    public static async Task<(IReadOnlyDictionary<Ulid, string> AgentNames, IReadOnlyDictionary<string, UserInfo> Users)>
        ResolveAsync(
            AgentsDbContext db,
            IUserDirectory userDirectory,
            IEnumerable<Ulid?> agentIds,
            IEnumerable<string?> userIds,
            CancellationToken ct)
    {
        var distinctAgentIds = agentIds.OfType<Ulid>().Distinct().ToList();
        var distinctUserIds = userIds.OfType<string>().Distinct(StringComparer.Ordinal).ToList();

        var agentNames = distinctAgentIds.Count > 0
            ? await db.ProjectAgents.AsNoTracking()
                .Where(a => distinctAgentIds.Contains(a.Id))
                .ToDictionaryAsync(a => a.Id, a => a.Name, ct)
            : new Dictionary<Ulid, string>();
        var users = distinctUserIds.Count > 0
            ? await userDirectory.GetUsersAsync(distinctUserIds, ct)
            : new Dictionary<string, UserInfo>(StringComparer.Ordinal);

        return (agentNames, users);
    }

    public static string? NameFor(this IReadOnlyDictionary<string, UserInfo> users, string? userId)
        => userId is not null && users.TryGetValue(userId, out var info) ? info.Name ?? info.Email : null;

    public static string? NameFor(this IReadOnlyDictionary<Ulid, string> agentNames, Ulid? agentId)
        => agentId is not null && agentNames.TryGetValue(agentId.Value, out var name) ? name : null;
}
