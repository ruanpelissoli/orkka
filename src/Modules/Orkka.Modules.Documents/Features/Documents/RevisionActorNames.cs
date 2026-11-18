namespace Orkka.Modules.Documents.Features.Documents;

using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.SharedKernel.Security;

/// <summary>Batch-resolves actor display names for revision rows: agent names from the
/// Agents module, user names from the host's user directory,
/// and titles for the tasks whose completion wrote a revision.</summary>
public static class RevisionActorNames
{
    public readonly record struct Resolved(
        IReadOnlyDictionary<Ulid, string> AgentNames,
        IReadOnlyDictionary<string, UserInfo> Users,
        IReadOnlyDictionary<Ulid, string> TaskTitles);

    public static async Task<Resolved> ResolveAsync(
        IAgentsApi agentsApi,
        ITasksApi tasksApi,
        IUserDirectory userDirectory,
        IEnumerable<Ulid?> agentIds,
        IEnumerable<string?> userIds,
        IEnumerable<Ulid?> taskIds,
        CancellationToken ct)
    {
        var distinctAgentIds = agentIds.OfType<Ulid>().Distinct().ToList();
        var distinctUserIds = userIds.OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var distinctTaskIds = taskIds.OfType<Ulid>().Distinct().ToList();

        var agentNames = distinctAgentIds.Count > 0
            ? await agentsApi.GetAgentNamesAsync(distinctAgentIds, ct)
            : new Dictionary<Ulid, string>();
        var users = distinctUserIds.Count > 0
            ? await userDirectory.GetUsersAsync(distinctUserIds, ct)
            : new Dictionary<string, UserInfo>(StringComparer.Ordinal);
        var taskTitles = distinctTaskIds.Count > 0
            ? await tasksApi.GetTaskTitlesAsync(distinctTaskIds, ct)
            : new Dictionary<Ulid, string>();

        return new Resolved(agentNames, users, taskTitles);
    }

    public static string? NameFor(this IReadOnlyDictionary<string, UserInfo> users, string? userId)
        => userId is not null && users.TryGetValue(userId, out var info) ? info.Name ?? info.Email : null;

    public static string? NameFor(this IReadOnlyDictionary<Ulid, string> agentNames, Ulid? agentId)
        => agentId is not null && agentNames.TryGetValue(agentId.Value, out var name) ? name : null;
}
