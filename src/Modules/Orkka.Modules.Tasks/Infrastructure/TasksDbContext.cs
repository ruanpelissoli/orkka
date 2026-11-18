namespace Orkka.Modules.Tasks.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Tasks.Domain;
using Orkka.SharedKernel.Events;
using Orkka.SharedKernel.Events.Catalog;
using Orkka.SharedKernel.Persistence;
using Orkka.SharedKernel.Security;

public sealed class TasksDbContext : ModuleDbContext
{
    private readonly ICurrentUser? _currentUser;
    private readonly IEventBus? _eventBus;

    public TasksDbContext(
        DbContextOptions<TasksDbContext> options,
        ICurrentUser? currentUser = null,
        IEventBus? eventBus = null) : base(options, currentUser)
    {
        _currentUser = currentUser;
        _eventBus = eventBus;
    }

    public const string Module = "tasks";
    public override string ModuleName => Module;

    public DbSet<TaskEntity> Tasks => Set<TaskEntity>();
    public DbSet<Milestone> Milestones => Set<Milestone>();
    public DbSet<TaskGroup> TaskGroups => Set<TaskGroup>();
    public DbSet<GroupBranch> GroupBranches => Set<GroupBranch>();
    public DbSet<TaskAttachment> TaskAttachments => Set<TaskAttachment>();
    public DbSet<TaskPlan> TaskPlans => Set<TaskPlan>();
    public DbSet<TaskMessage> TaskMessages => Set<TaskMessage>();
    public DbSet<TaskExecution> TaskExecutions => Set<TaskExecution>();
    public DbSet<TaskDependency> TaskDependencies => Set<TaskDependency>();
    public DbSet<MilestoneDependency> MilestoneDependencies => Set<MilestoneDependency>();
    public DbSet<TaskForecast> TaskForecasts => Set<TaskForecast>();
    public DbSet<AutopilotSession> AutopilotSessions => Set<AutopilotSession>();
    public DbSet<TaskApprovalRequest> TaskApprovalRequests => Set<TaskApprovalRequest>();
    public DbSet<TaskCodeChangeSnapshot> CodeChangeSnapshots => Set<TaskCodeChangeSnapshot>();
    public DbSet<MilestonePlanJob> MilestonePlanJobs => Set<MilestonePlanJob>();

    // Statuses that mean a human should look at the task — every tracked save that moves a
    // task INTO one of these publishes TaskStatusChanged (reactions: in-app notifications).
    // Shared with the Attention Inbox so the two can never drift apart.
    private static readonly IReadOnlySet<Contracts.TaskStatus> AttentionStatuses =
        Contracts.AttentionStatuses.Notifiable;

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        // Capture before save (entry states reset after), publish only after the save succeeds
        // so a failed transaction never produces phantom notifications. Bulk ExecuteUpdate calls
        // bypass the change tracker and must publish manually (see MarkTasksOutOfTokensAsync).
        var pending = _eventBus is null ? null : CaptureAttentionEvents();

        var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);

        if (pending is { Count: > 0 })
        {
            foreach (var evt in pending)
                await _eventBus!.PublishAsync(evt, cancellationToken);
        }

        return result;
    }

    private List<IntegrationEvent>? CaptureAttentionEvents()
    {
        List<IntegrationEvent>? events = null;
        var actor = _currentUser is { IsAuthenticated: true } ? _currentUser.UserId : null;

        foreach (var entry in ChangeTracker.Entries<TaskEntity>())
        {
            if (entry.State != EntityState.Modified)
                continue;

            var task = entry.Entity;

            var statusProp = entry.Property(t => t.Status);
            if (statusProp.IsModified
                && statusProp.OriginalValue != statusProp.CurrentValue
                && AttentionStatuses.Contains(statusProp.CurrentValue))
            {
                (events ??= []).Add(new TaskStatusChanged(
                    task.Id, task.OrgId, task.ProjectId, task.Number, task.Title,
                    task.CreatedByUserId, statusProp.CurrentValue.ToString(), actor));
            }

            var mergeFailureProp = entry.Property(t => t.MergeFailureReason);
            if (mergeFailureProp.IsModified
                && mergeFailureProp.CurrentValue is { } reason
                && mergeFailureProp.OriginalValue != mergeFailureProp.CurrentValue)
            {
                (events ??= []).Add(new TaskMergeAttemptFailed(
                    task.Id, task.OrgId, task.ProjectId, task.Number, task.Title,
                    task.CreatedByUserId, reason, actor));
            }
        }

        return events;
    }
}
