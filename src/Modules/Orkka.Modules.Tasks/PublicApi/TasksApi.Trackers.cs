namespace Orkka.Modules.Tasks.PublicApi;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Features.Tasks;
using Orkka.Modules.Tasks.Infrastructure.Services;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>Tasks imported from tracker tickets (the Trackers module's ingestion).</summary>
public sealed partial class TasksApi
{
    public async Task<ExternalTaskResult> CreateTrackerTaskAsync(TrackerTaskSpec spec, CancellationToken ct = default)
    {
        var existing = await _db.Tasks.AsNoTracking().IgnoreQueryFilters()
            .Where(t => t.OrgId == spec.OrgId && t.ProjectId == spec.ProjectId && t.ExternalRef == spec.ExternalRef)
            .Select(t => new { t.Id, t.Number, t.Title })
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
            return new ExternalTaskResult(existing.Id, existing.Number, existing.Title, Created: false);

        var number = await taskNumbers.ReserveRangeAsync(spec.ProjectId, 1, ct);

        // A ticket that spans repositories becomes the same group a human's "spans repositories"
        // task does: empty and Splitting until submitted, then split by FireCrossRepoSplit.
        var spans = (spec.SpansRepoIds ?? []).Distinct().ToList();
        var repoId = spec.RepoId ?? (spans.Count > 0 ? spans[0] : null);
        TaskGroup? group = null;
        if (spans.Count >= 2 && repoId is { } primary)
        {
            group = new TaskGroup
            {
                Id = Ulid.NewUlid(),
                OrgId = spec.OrgId,
                ProjectId = spec.ProjectId,
                Title = TrimTitle(spec.Title),
                Request = spec.Description,
                SpansRepoIdsJson = SpannedRepoIds.Serialize([primary, .. spans.Where(r => r != primary)]),
                Status = TaskGroupStatus.Splitting,
                CreatedByUserId = spec.OwnerUserId,
            };
            _db.TaskGroups.Add(group);
        }

        var task = new TaskEntity
        {
            Id = Ulid.NewUlid(),
            OrgId = spec.OrgId,
            ProjectId = spec.ProjectId,
            Number = number,
            RepoId = repoId,
            GroupId = group?.Id,
            Title = TrimTitle(spec.Title),
            Description = spec.Description,
            Priority = spec.Priority,
            Type = spec.Type,
            CurrentLane = Lane.Drafting,
            Status = TaskStatus.Draft,
            Tags = JsonSerializer.Serialize(spec.Tags),
            ExternalRef = spec.ExternalRef,
            ExternalKey = spec.ExternalKey,
            ExternalUrl = spec.ExternalUrl,
            ExternalEpic = spec.Epic,
            WorkingSubpath = spec.Subpath,
            // The developer the ticket is assigned to: their machine polled it, and their runner
            // works it with their git credentials and their AI subscription.
            CreatedByUserId = spec.OwnerUserId,
            RunnerUserId = spec.OwnerUserId,
        };
        _db.Tasks.Add(task);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Lost a race with a concurrent report of the same ticket — return the winner.
            _db.Entry(task).State = EntityState.Detached;
            if (group is not null)
                _db.Entry(group).State = EntityState.Detached;
            var winner = await _db.Tasks.AsNoTracking().IgnoreQueryFilters()
                .Where(t => t.OrgId == spec.OrgId && t.ProjectId == spec.ProjectId && t.ExternalRef == spec.ExternalRef)
                .Select(t => new { t.Id, t.Number, t.Title })
                .FirstAsync(ct);
            return new ExternalTaskResult(winner.Id, winner.Number, winner.Title, Created: false);
        }

        try
        {
            var project = await projectsApi.GetProjectSummaryAsync(spec.ProjectId, ct);
            var repo = repoId is { } rid ? await projectsApi.GetRepoAsync(rid, ct) : null;
            await taskNotifier.SendTaskCreatedAsync(spec.ProjectId, new TaskCreatedEvent(
                task.Id, task.Number, $"{project!.Key}-{task.Number}", task.Title,
                task.Priority.ToString(), task.Type.ToString(), task.CurrentLane.ToString(), task.Status.ToString(),
                repoId, repo?.Name, repo?.CardColor,
                [.. spec.Tags], task.CreatedAt));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Board broadcast failed for tracker task {TaskId}", task.Id);
        }

        return new ExternalTaskResult(task.Id, task.Number, task.Title, Created: true);
    }

    public async Task<bool> TryRewriteTrackerTaskAsync(
        Ulid taskId, string title, string description, CancellationToken ct = default)
    {
        var task = await _db.Tasks.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null || !NotStarted(task))
            return false;

        task.Title = TrimTitle(title);
        task.Description = description;
        await _db.SaveChangesAsync(ct);

        await taskNotifier.SendTaskUpdatedAsync(task.ProjectId, new TaskUpdatedEvent(
            task.Id, Title: task.Title, Priority: null, Status: null, CurrentLane: null,
            IsArchived: null, DateTimeOffset.UtcNow));
        return true;
    }

    public async Task<TrackerTaskRetirement> RetireTrackerTaskAsync(
        Ulid taskId, string reason, CancellationToken ct = default)
    {
        var task = await _db.Tasks.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null || task.IsArchived)
            return TrackerTaskRetirement.Gone;

        if (NotStarted(task))
        {
            task.IsArchived = true;
            task.ArchivedAt = DateTimeOffset.UtcNow;
            task.Status = TaskStatus.Archived;
            await RewireStoryDependentsAsync(task, ct);
            await _db.SaveChangesAsync(ct);
            await AddTaskMessageAsync(taskId, "system",
                $"{reason} Nothing had started, so the task was archived — it comes back if the ticket does.",
                MessageType.SystemEvent, ct: ct);
            await taskNotifier.SendTaskUpdatedAsync(task.ProjectId, new TaskUpdatedEvent(
                task.Id, Title: null, Priority: null, Status: task.Status.ToString(), CurrentLane: null,
                IsArchived: true, DateTimeOffset.UtcNow));
            return TrackerTaskRetirement.Archived;
        }

        await AddTaskMessageAsync(taskId, "system",
            $"{reason} Work had already started, so it keeps going — finish it or archive it.",
            MessageType.SystemEvent, ct: ct);
        return TrackerTaskRetirement.Held;
    }

    public async Task ReviveTrackerTaskAsync(Ulid taskId, string note, CancellationToken ct = default)
    {
        var task = await _db.Tasks.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null)
            return;

        if (task.IsArchived)
        {
            task.IsArchived = false;
            task.ArchivedAt = null;
            task.CurrentLane = Lane.Drafting;
            task.Status = TaskStatus.Draft;
            await _db.SaveChangesAsync(ct);
            await taskNotifier.SendTaskUpdatedAsync(task.ProjectId, new TaskUpdatedEvent(
                task.Id, Title: null, Priority: null, Status: task.Status.ToString(),
                CurrentLane: task.CurrentLane.ToString(), IsArchived: false, DateTimeOffset.UtcNow));
        }

        await AddTaskMessageAsync(taskId, "system", note, MessageType.SystemEvent, ct: ct);
    }

    public async Task SetTrackerSyncErrorAsync(Ulid taskId, string? error, CancellationToken ct = default)
    {
        var task = await _db.Tasks.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null || task.ExternalSyncError == error)
            return;

        task.ExternalSyncError = error is { Length: > 500 } ? error[..500] : error;
        await _db.SaveChangesAsync(ct);
        await taskNotifier.SendTaskUpdatedAsync(task.ProjectId, new TaskUpdatedEvent(
            task.Id, Title: null, Priority: null, Status: null, CurrentLane: null,
            IsArchived: null, DateTimeOffset.UtcNow));
    }

    public async Task<IReadOnlyList<TrackerTaskState>> GetTrackerTaskStatesAsync(
        IReadOnlyCollection<Ulid> taskIds, CancellationToken ct = default)
    {
        if (taskIds.Count == 0)
            return [];

        return await _db.Tasks.IgnoreQueryFilters().AsNoTracking()
            .Where(t => taskIds.Contains(t.Id))
            .Select(t => new TrackerTaskState(
                t.Id,
                t.StartedAt != null || t.HasAgentWorking
                    || (t.CurrentLane != Lane.Drafting && t.CurrentLane != Lane.Ready),
                t.PublishedPrUrl,
                t.IsMerged && t.PublishedPrUrl == null,
                t.IsArchived,
                t.RepoId,
                t.CurrentLane == Lane.Developing || t.CurrentLane == Lane.Testing
                    || t.CurrentLane == Lane.CodeReview || t.CurrentLane == Lane.Done
                    || t.Executions.Any(e => e.Lane == Lane.Developing),
                t.CurrentLane == Lane.CodeReview || t.CurrentLane == Lane.Done
                    || t.Executions.Any(e => e.Lane == Lane.CodeReview),
                t.GroupId))
            .ToListAsync(ct);
    }

    public async Task AskTrackerRepoAsync(
        Ulid taskId, IReadOnlyList<string> repoNames, string reason, bool submitOnAnswer, CancellationToken ct = default)
    {
        var task = await _db.Tasks.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.Id == taskId)
            .Select(t => new { t.ProjectId, t.Title, t.RepoId, t.CurrentLane })
            .FirstOrDefaultAsync(ct);
        if (task is null || task.RepoId is not null || task.CurrentLane != Lane.Drafting)
            return;

        // The questions block every agent question uses, so the board renders the repositories as
        // options; the metadata marks it as the routing question BoardOperations answers itself.
        var question = new
        {
            questions = new[]
            {
                new
                {
                    question = $"Which repository should this ticket be worked in? {reason}".Trim(),
                    options = repoNames,
                    allowCustom = false,
                    multiSelect = true,
                    recommended = (string?)null,
                },
            },
        };
        var content = "```questions\n" + JsonSerializer.Serialize(question, TrackerJson) + "\n```";
        var metadata = JsonSerializer.Serialize(new TrackerRepoQuestionMetadata(true, submitOnAnswer), TrackerJson);
        await AddTaskMessageAsync(taskId, "agent", content, MessageType.Question, metadata, ct);

        // Source lane Drafting: no agent question ever comes from Drafting, so this is how the
        // answer is recognised as the routing question (BoardOperations.AnswerTaskAsync).
        var fromLane = await MoveTaskToPendingQuestionsAsync(taskId, Lane.Drafting, ct);
        if (fromLane is not null)
        {
            await taskNotifier.SendLaneChangedAsync(task.ProjectId, new LaneChangedEvent(
                taskId, task.Title, fromLane.Value.ToString(), Lane.PendingQuestions.ToString(),
                TaskStatus.WaitingAnswer.ToString(), DateTimeOffset.UtcNow));
        }
    }

    private static readonly JsonSerializerOptions TrackerJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// A Story member leaving the chain (archived, retired): whatever waited on it now waits on
    /// ITS predecessor instead, so the order still holds — an archived blocker counts as met, and
    /// without the rewire the next member would jump the queue. Tracked; the caller saves.
    /// </summary>
    internal Task RewireStoryDependentsAsync(TaskEntity member, CancellationToken ct)
        => Infrastructure.Services.StoryChain.RewireDependentsAsync(_db, member, ct);

    /// <summary>
    /// No agent has touched it: still in Drafting, or queued for its first PM review with no
    /// run ever started. Anything later has a plan, a branch or a run built on the old text.
    /// </summary>
    private static bool NotStarted(TaskEntity task)
        => task.StartedAt is null
            && !task.HasAgentWorking
            && task.CurrentLane is Lane.Drafting or Lane.Ready;

    private static string TrimTitle(string title) => title.Length > 200 ? title[..200] : title;
}
