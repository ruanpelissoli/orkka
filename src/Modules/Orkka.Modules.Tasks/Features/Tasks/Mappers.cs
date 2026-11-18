namespace Orkka.Modules.Tasks.Features.Tasks;

using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.SharedKernel.Security;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.Modules.Agents.Contracts;
using Orkka.SharedKernel.Storage;
using Orkka.SharedKernel.Utilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using System.Text.Json;
using Orkka.Modules.Tasks.Features.Tasks;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

internal static class Mappers
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static TaskResponse ToResponse(this TaskEntity entity, string projectKey, RepoSummary? repo = null)
    {
        // The frontend contract is string[] — never null
        List<string> tags = entity.Tags is not null
            ? JsonSerializer.Deserialize<List<string>>(entity.Tags, JsonOptions) ?? []
            : [];

        List<string> contextFiles = entity.ContextFiles is not null
            ? JsonSerializer.Deserialize<List<string>>(entity.ContextFiles, JsonOptions) ?? []
            : [];

        List<string> contextDocumentIds = entity.ContextDocumentIds is not null
            ? JsonSerializer.Deserialize<List<string>>(entity.ContextDocumentIds, JsonOptions) ?? []
            : [];

        // Only endpoints that Include() the dependency graph populate these — write endpoints
        // return empty lists and the UI refetches the task after mutations.
        var dependsOn = ToDependencyItems(entity.Dependencies, projectKey, entity.GroupId, otherIsDependsOn: true);
        var dependents = ToDependencyItems(entity.Dependents, projectKey, entity.GroupId, otherIsDependsOn: false);

        return new TaskResponse(
            entity.Id,
            entity.ProjectId,
            entity.Number,
            $"{projectKey}-{entity.Number}",
            entity.RepoId,
            repo?.Name,
            repo?.CardColor,
            entity.GroupId,
            entity.Title,
            entity.Description,
            entity.Priority.ToString(),
            entity.Type.ToString(),
            entity.CurrentLane.ToString(),
            entity.Status.ToString(),
            entity.IsArchived,
            entity.CreatedByUserId,
            entity.BranchName,
            entity.PrUrl,
            entity.IsMerged,
            entity.MergeFailureReason,
            entity.QueuePosition,
            tags,
            contextFiles,
            contextDocumentIds,
            entity.TotalTokensUsed,
            dependsOn.Any(TaskDependencyQueries.IsUnmetItem),
            dependsOn,
            dependents,
            entity.SkipPlanning,
            entity.SkipTesting,
            entity.SkipCodeReview,
            entity.StartedAt,
            entity.CompletedAt,
            entity.ArchivedAt,
            entity.CreatedAt,
            entity.UpdatedAt,
            entity.RunnerUserId,
            entity.AssignedAgentId,
            entity.Model,
            entity.SetupFailure,
            entity.MilestoneId,
            entity.IsInteractive,
            entity.InteractiveSessionStartedAt,
            entity.InteractiveFinishRequestedAt,
            entity.RecommendedModel,
            entity.PublishedPrUrl,
            entity.ExternalKey,
            entity.ExternalUrl,
            entity.ExternalEpic,
            entity.ExternalSyncError,
            entity.StoryOrder);
    }

    public static TaskSummaryResponse ToSummary(
        this TaskEntity entity, string projectKey, RepoSummary? repo = null, List<BlockingTaskRef>? blockedBy = null,
        TaskLastAgentMessageResponse? lastAgentMessage = null)
    {
        return new TaskSummaryResponse(
            entity.Id,
            entity.Number,
            $"{projectKey}-{entity.Number}",
            entity.Title,
            entity.Priority.ToString(),
            entity.Type.ToString(),
            entity.CurrentLane.ToString(),
            entity.Status.ToString(),
            entity.RepoId,
            repo?.Name,
            repo?.CardColor,
            entity.GroupId,
            entity.CreatedByUserId,
            entity.QueuePosition,
            blockedBy is { Count: > 0 },
            blockedBy ?? [],
            entity.MergeFailureReason,
            entity.CreatedAt,
            entity.RunnerUserId,
            lastAgentMessage,
            entity.MilestoneId,
            entity.IsInteractive,
            entity.InteractiveSessionStartedAt,
            entity.IsArchived,
            entity.BranchName,
            entity.PrUrl,
            entity.CompletedAt,
            entity.ExternalKey,
            entity.ExternalUrl,
            entity.ExternalEpic,
            entity.ExternalSyncError,
            entity.StoryOrder);
    }

    /// <summary>
    /// Board-card form of <see cref="ToSummary(TaskEntity, string, RepoSummary?, List{BlockingTaskRef}?, TaskLastAgentMessageResponse?)"/>
    /// over the projected row. Kept beside it so the two stay in step — a field added to
    /// <see cref="TaskSummaryResponse"/> must be added to <see cref="BoardTaskRow"/> too.
    /// </summary>
    public static TaskSummaryResponse ToSummary(
        this BoardTaskRow row, string projectKey, RepoSummary? repo = null, List<BlockingTaskRef>? blockedBy = null,
        TaskLastAgentMessageResponse? lastAgentMessage = null)
    {
        return new TaskSummaryResponse(
            row.Id,
            row.Number,
            $"{projectKey}-{row.Number}",
            row.Title,
            row.Priority.ToString(),
            row.Type.ToString(),
            row.CurrentLane.ToString(),
            row.Status.ToString(),
            row.RepoId,
            repo?.Name,
            repo?.CardColor,
            row.GroupId,
            row.CreatedByUserId,
            row.QueuePosition,
            blockedBy is { Count: > 0 },
            blockedBy ?? [],
            row.MergeFailureReason,
            row.CreatedAt,
            row.RunnerUserId,
            lastAgentMessage,
            row.MilestoneId,
            row.IsInteractive,
            row.InteractiveSessionStartedAt,
            row.IsArchived,
            row.BranchName,
            row.PrUrl,
            row.CompletedAt,
            row.ExternalKey,
            row.ExternalUrl,
            row.ExternalEpic,
            row.ExternalSyncError,
            row.StoryOrder,
            row.StoryKey,
            row.StoryCount);
    }

    /// <summary>
    /// Maps loaded dependency edges to DTO items. Edges whose "other task" navigation is not
    /// loaded are skipped — callers without Include() get empty lists, never a crash.
    /// </summary>
    /// <param name="groupId">The viewed task's own group. Taken from the caller rather than from
    /// <c>link.Task</c>, whose navigation is only loaded on one side of each edge.</param>
    private static List<TaskDependencyItem> ToDependencyItems(
        IEnumerable<TaskDependency> links, string projectKey, Ulid? groupId, bool otherIsDependsOn)
    {
        List<TaskDependencyItem> items = [];
        foreach (var link in links)
        {
            var other = otherIsDependsOn ? link.DependsOnTask : link.Task;
            if (other is null)
                continue;

            items.Add(new TaskDependencyItem(
                other.Id,
                other.Number,
                $"{projectKey}-{other.Number}",
                other.Title,
                other.Status.ToString(),
                other.IsMerged,
                groupId is not null && groupId == other.GroupId,
                link.Reason,
                link.Source.ToString()));
        }
        return items;
    }

    public static string? SerializeTags(List<string>? tags) =>
        tags is not null ? JsonSerializer.Serialize(tags, JsonOptions) : null;

    public static string? SerializeContextFiles(List<string>? files) =>
        files is not null ? JsonSerializer.Serialize(files, JsonOptions) : null;

    public static string? SerializeContextDocumentIds(List<string>? documentIds) =>
        documentIds is not null ? JsonSerializer.Serialize(documentIds, JsonOptions) : null;
}
