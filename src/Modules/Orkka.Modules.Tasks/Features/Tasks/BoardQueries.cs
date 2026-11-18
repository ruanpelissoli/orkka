namespace Orkka.Modules.Tasks.Features.Tasks;

using System.Text.Json;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Infrastructure;
using Microsoft.EntityFrameworkCore;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>
/// The columns a board card actually renders. Projecting to this keeps Description (10k chars)
/// and the four JSON columns of <see cref="TaskEntity"/> out of the result set — the board
/// query is unbounded over a project, so those columns are the cost.
/// </summary>
internal sealed record BoardTaskRow(
    Ulid Id,
    int Number,
    string Title,
    Priority Priority,
    TaskType Type,
    Lane CurrentLane,
    TaskStatus Status,
    Ulid? RepoId,
    Ulid? GroupId,
    string? CreatedByUserId,
    int? QueuePosition,
    string? MergeFailureReason,
    DateTimeOffset CreatedAt,
    string? RunnerUserId,
    Ulid? MilestoneId,
    // Board cards badge a live interactive session, so the board never silently omits work
    // somebody is actively doing.
    bool IsInteractive,
    DateTimeOffset? InteractiveSessionStartedAt,
    bool IsArchived,
    string? BranchName,
    string? PrUrl,
    DateTimeOffset? CompletedAt,
    string? ExternalKey,
    string? ExternalUrl,
    string? ExternalEpic,
    string? ExternalSyncError,
    // A Story member: its place, the Story's key and how many sub-tasks the story has (the card's
    // "SHOP-10 · 2/3" chip).
    int? StoryOrder = null,
    string? StoryKey = null,
    int? StoryCount = null);

/// <summary>Shared auxiliary queries for the board and board-search endpoints.</summary>
internal static class BoardQueries
{
    /// <summary>Narrows a task query to exactly what <c>ToSummary</c> consumes.</summary>
    public static IQueryable<BoardTaskRow> SelectBoardRows(this IQueryable<TaskEntity> query)
        => query.Select(t => new BoardTaskRow(
            t.Id,
            t.Number,
            t.Title,
            t.Priority,
            t.Type,
            t.CurrentLane,
            t.Status,
            t.RepoId,
            t.GroupId,
            t.CreatedByUserId,
            t.QueuePosition,
            t.MergeFailureReason,
            t.CreatedAt,
            t.RunnerUserId,
            t.MilestoneId,
            t.IsInteractive,
            t.InteractiveSessionStartedAt,
            t.IsArchived,
            t.BranchName,
            t.PrUrl,
            t.CompletedAt,
            t.ExternalKey,
            t.ExternalUrl,
            t.ExternalEpic,
            t.ExternalSyncError,
            t.StoryOrder,
            t.Group != null && t.Group.Kind == TaskGroupKind.Story ? t.Group.ExternalKey : null,
            t.Group != null && t.Group.Kind == TaskGroupKind.Story
                ? t.Group.Tasks.Count(x => !x.IsArchived)
                : (int?)null));

    /// <summary>
    /// Latest agent-authored message per task, truncated for card previews. "Latest" is an
    /// agent message with no later one on the same task (NOT EXISTS over the (TaskId,
    /// CreatedAt) index) — SQLite has no lateral join for a per-row FirstOrDefault.
    /// </summary>
    public static async Task<Dictionary<Ulid, TaskLastAgentMessageResponse>> GetLastAgentMessagesAsync(
        TasksDbContext db, List<Ulid> taskIds, CancellationToken ct)
    {
        if (taskIds.Count == 0) return [];

        var rows = await db.TaskMessages
            .AsNoTracking()
            .Where(m => taskIds.Contains(m.TaskId) && m.Role == "agent")
            .Where(m => !db.TaskMessages.Any(later => later.TaskId == m.TaskId
                && later.Role == "agent"
                && (later.CreatedAt > m.CreatedAt
                    // ULIDs are time-sortable — disambiguates same-timestamp streamed chunks.
                    || (later.CreatedAt == m.CreatedAt && later.Id.CompareTo(m.Id) > 0))))
            .Select(m => new { Id = m.TaskId, m.Content, m.MessageType, m.Metadata, m.CreatedAt })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.Id)
            .Select(g => g.First())
            .ToDictionary(
                r => r.Id,
                r => new TaskLastAgentMessageResponse(
                    TaskLastMessageChangedEvent.BuildPreview(r.Content),
                    r.MessageType.ToString(),
                    ExtractAgentName(r.Metadata),
                    r.CreatedAt));
    }

    /// <summary>Reads "agentName" out of a message's metadata JSON; null on any malformed input.</summary>
    public static string? ExtractAgentName(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            return doc.RootElement.TryGetProperty("agentName", out var name) && name.ValueKind == JsonValueKind.String
                ? name.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
