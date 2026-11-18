namespace Orkka.Modules.Tasks.PublicApi;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Features.Tasks;
using Orkka.Modules.Tasks.Infrastructure;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

public sealed partial class TasksApi(
    TasksDbContext db,
    ITaskNumberService taskNumbers,
    IProjectsApi projectsApi,
    ITaskNotifier taskNotifier,
    Orkka.SharedKernel.Events.IEventBus eventBus,
    Infrastructure.Services.TaskSubmissionService submissions,
    ILogger<TasksApi> logger,
    // Trailing + optional so unit tests that construct TasksApi for other surfaces keep
    // compiling; the DI container always supplies them.
    Infrastructure.Services.BoardOperations? board = null,
    Orkka.Modules.Runners.Contracts.IRunnerCoverageResolver? coverageResolver = null,
    Orkka.Modules.Agents.Contracts.IAgentsApi? agentsApi = null) : ITasksApi
{
    private readonly TasksDbContext _db = db;
    private readonly Orkka.SharedKernel.Events.IEventBus _eventBus = eventBus;
    private readonly Infrastructure.Services.TaskSubmissionService _submissions = submissions;

    public async Task<IReadOnlyDictionary<Ulid, int>> GetTaskCountsAsync(
        IReadOnlyCollection<Ulid> projectIds, CancellationToken ct = default)
        => await _db.Tasks.AsNoTracking()
            .Where(t => projectIds.Contains(t.ProjectId))
            .GroupBy(t => t.ProjectId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

    public async Task<ExternalTaskResult> CreateExternalTaskAsync(
        Ulid orgId, Ulid projectId, Ulid repoId, string title, string description,
        IReadOnlyList<string> tags, string externalRef, CancellationToken ct = default)
    {
        var existing = await _db.Tasks.AsNoTracking()
            .Where(t => t.OrgId == orgId && t.ProjectId == projectId && t.ExternalRef == externalRef)
            .Select(t => new { t.Id, t.Number, t.Title })
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
            return new ExternalTaskResult(existing.Id, existing.Number, existing.Title, Created: false);

        var number = await taskNumbers.ReserveRangeAsync(projectId, 1, ct);
        var task = new TaskEntity
        {
            Id = Ulid.NewUlid(),
            OrgId = orgId,
            ProjectId = projectId,
            Number = number,
            RepoId = repoId,
            Title = title.Length > 200 ? title[..200] : title,
            Description = description,
            Priority = Priority.Medium,
            Type = tags.Contains("ci-failure") ? TaskType.BugFix : TaskType.Feature,
            CurrentLane = Lane.Drafting,
            Status = TaskStatus.Draft,
            Tags = JsonSerializer.Serialize(tags),
            ExternalRef = externalRef,
        };
        _db.Tasks.Add(task);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Lost a race with a duplicate delivery of the same event — return the winner.
            _db.Entry(task).State = EntityState.Detached;
            var winner = await _db.Tasks.AsNoTracking()
                .Where(t => t.OrgId == orgId && t.ProjectId == projectId && t.ExternalRef == externalRef)
                .Select(t => new { t.Id, t.Number, t.Title })
                .FirstAsync(ct);
            return new ExternalTaskResult(winner.Id, winner.Number, winner.Title, Created: false);
        }

        // Push to any open boards — best effort.
        try
        {
            var project = await projectsApi.GetProjectSummaryAsync(projectId, ct);
            var repo = await projectsApi.GetRepoAsync(repoId, ct);
            await taskNotifier.SendTaskCreatedAsync(projectId, new TaskCreatedEvent(
                task.Id, task.Number, $"{project!.Key}-{task.Number}", task.Title,
                task.Priority.ToString(), task.Type.ToString(), task.CurrentLane.ToString(), task.Status.ToString(),
                repoId, repo?.Name, repo?.CardColor,
                tags.ToList(), task.CreatedAt));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Board broadcast failed for external task {TaskId}", task.Id);
        }

        return new ExternalTaskResult(task.Id, task.Number, task.Title, Created: true);
    }

    public async Task<IReadOnlyList<ChatMilestoneRef>> ListMilestonesForChatAsync(
        Ulid orgId, Ulid projectId, CancellationToken ct = default) =>
        await _db.Milestones
            .AsNoTracking()
            .Where(m => m.OrgId == orgId && m.ProjectId == projectId)
            .OrderBy(m => m.Position)
            .Select(m => new ChatMilestoneRef(m.Id, m.Title, m.Outcome, m.Status.ToString()))
            .ToListAsync(ct);

    public async Task<ChatMilestoneRef> CreateMilestoneForChatAsync(
        Ulid orgId, Ulid projectId, string title, string? outcome,
        string? createdByUserId, CancellationToken ct = default)
    {
        var trimmed = title.Trim();

        // Idempotent by title: an agent that calls this twice in one conversation should not
        // leave two rows with the same name on the roadmap.
        var existing = await _db.Milestones
            .FirstOrDefaultAsync(m => m.OrgId == orgId && m.ProjectId == projectId
                && m.Title == trimmed, ct);

        if (existing is not null)
            return new ChatMilestoneRef(existing.Id, existing.Title, existing.Outcome,
                existing.Status.ToString());

        var position = (await _db.Milestones
            .Where(m => m.OrgId == orgId && m.ProjectId == projectId)
            .MaxAsync(m => (int?)m.Position, ct) ?? 0) + 1;

        var milestone = new Milestone
        {
            OrgId = orgId,
            ProjectId = projectId,
            Title = trimmed,
            Outcome = string.IsNullOrWhiteSpace(outcome) ? null : outcome.Trim(),
            Position = position,
            CreatedByUserId = createdByUserId,
        };

        _db.Milestones.Add(milestone);
        await _db.SaveChangesAsync(ct);

        return new ChatMilestoneRef(milestone.Id, milestone.Title, milestone.Outcome,
            milestone.Status.ToString());
    }

    public async Task<IReadOnlyList<CreatedChatTask>> CreateChatTasksAsync(
        Ulid orgId, Ulid projectId, Ulid sourceMessageId,
        string? createdByUserId, string? requesterLocale,
        IReadOnlyList<ChatTaskSpec> specs, CancellationToken ct = default)
    {
        if (specs.Count == 0)
            return [];

        var created = new List<TaskEntity>();

        // The PM-authored path stamps the runner exactly like the create endpoint — the human
        // driving the chat is the task creator, and their runner runs it; the dev model per repo
        // decides what they are told about its coverage.
        var resolver = coverageResolver ?? new Infrastructure.Services.UnknownRunnerCoverageResolver();
        var decisionsByRepo = new Dictionary<Ulid, Orkka.Modules.Runners.Contracts.RunnerCoverageDecision>();
        if (createdByUserId is not null)
        {
            var projectDefaultModel = (await projectsApi.GetProjectSummaryAsync(projectId, ct))?.Settings?.DefaultModel;
            foreach (var specRepoId in specs.Select(s => s.RepoId).Distinct())
            {
                var query = agentsApi is null
                    ? new Orkka.Modules.Runners.Contracts.RunnerCoverageQuery(
                        orgId, createdByUserId, null, null, projectDefaultModel)
                    : await Features.Tasks.DevModelLookup.QueryAsync(agentsApi, orgId, createdByUserId,
                        projectId, specRepoId, null, null, projectDefaultModel, ct);
                decisionsByRepo[specRepoId] = await resolver.ResolveAsync(query, ct);
            }
        }

        // The numbers first, on their own: the counter lives on the projects table and SQLite
        // has one writer, so a reservation inside an open write transaction here would wait on
        // it. A failed insert leaves a gap in the numbering, which is fine. The inserts below
        // are one SaveChanges, so they commit or fail together.
        var lastNumber = await taskNumbers.ReserveRangeAsync(projectId, specs.Count, ct);
        var nextNumber = lastNumber - specs.Count + 1;

        foreach (var spec in specs)
        {
            var entity = new TaskEntity
            {
                // Explicit id: the SaveChanges interceptor assigns ids too late — adding a
                // second entity with a default (empty) key throws "same key already tracked"
                Id = Ulid.NewUlid(),
                OrgId = orgId,
                ProjectId = projectId,
                Number = nextNumber++,
                RepoId = spec.RepoId,
                MilestoneId = spec.MilestoneId,
                Title = spec.Title,
                Description = spec.Description,
                Priority = Enum.TryParse<Priority>(spec.Priority, ignoreCase: true, out var priority) ? priority : Priority.Medium,
                Type = Enum.TryParse<TaskType>(spec.Type, ignoreCase: true, out var type) ? type : TaskType.Feature,
                CurrentLane = Lane.Drafting,
                Status = TaskStatus.Draft,
                CreatedByUserId = createdByUserId,
                RequesterLocale = requesterLocale,
                SourceMessageId = sourceMessageId,
                Tags = JsonSerializer.Serialize(spec.Tags),
                ContextFiles = spec.ContextFiles is { Count: > 0 } ? JsonSerializer.Serialize(spec.ContextFiles) : null,
                RunnerUserId = createdByUserId,
            };

            _db.Tasks.Add(entity);
            created.Add(entity);
        }

        await _db.SaveChangesAsync(ct);

        foreach (var task in created)
        {
            if (task.RepoId is { } announcedRepoId && decisionsByRepo.TryGetValue(announcedRepoId, out var announced))
                await Features.Tasks.DevModelLookup.AnnounceAsync(this, task.Id, announced, ct);
        }

        // Push to any open boards — a SignalR failure must not break the caller's SSE response
        try
        {
            var project = await projectsApi.GetProjectSummaryAsync(projectId, ct);
            var repos = (await projectsApi.ListReposAsync(projectId, ct))
                .ToDictionary(r => r.Id);

            foreach (var task in created)
            {
                var repo = repos[task.RepoId!.Value];
                await taskNotifier.SendTaskCreatedAsync(projectId, new TaskCreatedEvent(
                    task.Id,
                    task.Number,
                    $"{project!.Key}-{task.Number}",
                    task.Title,
                    task.Priority.ToString(),
                    task.Type.ToString(),
                    task.CurrentLane.ToString(),
                    task.Status.ToString(),
                    task.RepoId!.Value,
                    repo.Name,
                    repo.CardColor,
                    task.Tags is null ? null : JsonSerializer.Deserialize<List<string>>(task.Tags),
                    task.CreatedAt));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to broadcast TaskCreated events for project {ProjectId}", projectId);
        }

        return created.Select(t => new CreatedChatTask(t.Id, t.Number, t.Title)).ToList();
    }

    public async Task<IReadOnlyList<ChatTaskLink>> GetChatCreatedTasksAsync(
        Ulid projectId, IReadOnlyCollection<Ulid> sourceMessageIds, CancellationToken ct = default)
    {
        if (sourceMessageIds.Count == 0)
            return [];

        // No Contains over the nullable SourceMessageId column (the Ulid?→Guid? converter has
        // broken collection-parameter translation before), so fetch the project's
        // chat-created tasks and match message ids in memory — the set stays small.
        var candidates = await _db.Tasks.AsNoTracking()
            .Where(t => t.ProjectId == projectId && t.SourceMessageId != null)
            .OrderBy(t => t.Number)
            .Select(t => new ChatTaskLink(t.SourceMessageId!.Value, t.Id, t.Number, t.Title))
            .ToListAsync(ct);

        var ids = sourceMessageIds.ToHashSet();
        return candidates.Where(c => ids.Contains(c.SourceMessageId)).ToList();
    }

    public async Task<IReadOnlyDictionary<Ulid, string>> GetTaskTitlesAsync(
        IReadOnlyCollection<Ulid> taskIds, CancellationToken ct = default)
    {
        if (taskIds.Count == 0)
            return new Dictionary<Ulid, string>();

        // Non-nullable Ulid key, so Contains translates fine here (unlike the nullable
        // SourceMessageId columns elsewhere in this file).
        var distinct = taskIds.Distinct().ToList();
        return await _db.Tasks.AsNoTracking()
            .Where(t => distinct.Contains(t.Id))
            .Select(t => new { t.Id, t.Title })
            .ToDictionaryAsync(t => t.Id, t => t.Title, ct);
    }

    public async Task<IReadOnlyDictionary<Ulid, TaskDisplayRef>> GetTaskRefsAsync(
        IReadOnlyCollection<Ulid> taskIds, CancellationToken ct = default)
    {
        if (taskIds.Count == 0)
            return new Dictionary<Ulid, TaskDisplayRef>();

        var distinct = taskIds.Distinct().ToList();
        return await _db.Tasks.AsNoTracking()
            .Where(t => distinct.Contains(t.Id))
            .Select(t => new { t.Id, t.ProjectId, t.Number, t.Title })
            .ToDictionaryAsync(
                t => t.Id, t => new TaskDisplayRef(t.ProjectId, t.Number, t.Title), ct);
    }

    public async Task<IReadOnlyList<ProjectTaskSummary>> ListProjectTaskSummariesAsync(
        Ulid orgId, Ulid projectId, int limit = 200, CancellationToken ct = default)
    {
        // Callable without an ICurrentUser (tenant filter bypassed) — the explicit
        // org/project predicates are the isolation boundary.
        return await _db.Tasks.AsNoTracking()
            .Where(t => t.OrgId == orgId && t.ProjectId == projectId && !t.IsArchived)
            .OrderByDescending(t => t.Number)
            .Take(Math.Clamp(limit, 1, 500))
            .Select(t => new ProjectTaskSummary(
                t.Id, t.Number, t.Title, t.CurrentLane, t.Status, t.Priority, t.Type, t.RepoId))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<AgentTaskSummary>> ListAgentTaskSummariesAsync(
        Ulid orgId, Ulid projectId, Ulid agentId, int limit = 100, CancellationToken ct = default)
    {
        // Callable without an ICurrentUser (tenant filter bypassed) — the explicit
        // org/project predicates are the isolation boundary.
        var rows = await _db.Tasks.AsNoTracking()
            .Where(t => t.OrgId == orgId && t.ProjectId == projectId && !t.IsArchived)
            .Where(t => t.AssignedAgentId == agentId || t.CurrentAgentId == agentId ||
                        t.Executions.Any(e => e.AgentId == agentId))
            .Select(t => new
            {
                t.Id, t.Number, t.Title, t.CurrentLane, t.Status, t.Priority, t.Type,
                t.CreatedAt,
                Lanes = t.Executions.Where(e => e.AgentId == agentId)
                    .Select(e => e.Lane).ToList(),
                LastWorkedAt = t.Executions.Where(e => e.AgentId == agentId)
                    .Max(e => (DateTimeOffset?)(e.CompletedAt ?? e.StartedAt ?? e.CreatedAt)),
            })
            .OrderByDescending(x => x.LastWorkedAt ?? x.CreatedAt)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(ct);

        return rows.Select(x => new AgentTaskSummary(
            x.Id, x.Number, x.Title, x.CurrentLane, x.Status, x.Priority, x.Type,
            x.Lanes.Distinct().ToList(), x.Lanes.Count, x.LastWorkedAt)).ToList();
    }

    public async Task<IReadOnlyList<TaskExecutionSummary>?> ListTaskExecutionSummariesAsync(
        Ulid orgId, Ulid projectId, Ulid taskId, CancellationToken ct = default)
    {
        // Callable without an ICurrentUser (tenant filter bypassed) — the explicit
        // org/project predicates are the isolation boundary.
        var exists = await _db.Tasks.AsNoTracking()
            .AnyAsync(t => t.OrgId == orgId && t.ProjectId == projectId && t.Id == taskId, ct);
        if (!exists)
            return null;

        return await _db.TaskExecutions.AsNoTracking()
            .Where(e => e.TaskId == taskId)
            .OrderBy(e => e.CreatedAt)
            .Select(e => new TaskExecutionSummary(
                e.AgentId, e.Lane, e.Status, e.StartedAt, e.CompletedAt,
                e.TokensUsed, e.ErrorMessage, e.FilesChanged))
            .ToListAsync(ct);
    }
}
