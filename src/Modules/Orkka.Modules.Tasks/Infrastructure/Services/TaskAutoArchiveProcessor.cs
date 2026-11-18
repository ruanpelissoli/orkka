namespace Orkka.Modules.Tasks.Infrastructure.Services;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;

/// <summary>
/// Archives tasks that have stood in Done past the configured window, for projects whose
/// <see cref="ProjectSettingsDto.AutoArchiveDoneTasks"/> is on.
///
/// Driven from the TASK side rather than the project side on purpose. Sweeping projects would
/// mean loading every project in every org each tick to read a flag that is off for nearly all
/// of them; sweeping tasks starts from a set that is naturally almost always empty — only work
/// crossing the two-week line since the last tick — and asks about projects only for the ones
/// it found.
///
/// Cheap when idle and safe to run on several replicas at once: the archive write re-applies
/// the eligibility predicate, so a task another replica took is skipped rather than archived
/// twice. That is also why this needs no leader election.
/// </summary>
public sealed class TaskAutoArchiveProcessor(
    IServiceScopeFactory scopeFactory,
    ITaskNotifier taskNotifier,
    IConfiguration configuration,
    ILogger<TaskAutoArchiveProcessor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 0 or less disables the sweep outright (same convention as the audit retention sweep),
        // which is the only safe reading: a zero-day window would archive work the moment it
        // reached Done, and silently treating that as "two weeks" would hide the misconfiguration.
        var afterDays = configuration.GetValue("Worker:AutoArchiveDoneAfterDays", 14);
        if (afterDays <= 0)
        {
            logger.LogInformation(
                "Auto-archive disabled (Worker:AutoArchiveDoneAfterDays={Days})", afterDays);
            return;
        }

        var interval = TimeSpan.FromMinutes(
            configuration.GetValue("Worker:AutoArchiveIntervalMinutes", 60));
        var batchSize = configuration.GetValue("Worker:AutoArchiveBatchSize", 500);

        logger.LogInformation(
            "TaskAutoArchiveProcessor started: archiving Done tasks older than {Days}d every {Interval}m",
            afterDays, interval.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(afterDays, batchSize, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Auto-archive sweep failed — retrying next interval");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("TaskAutoArchiveProcessor stopped");
    }

    /// <summary>One pass. Internal so the project-setting gate — the whole point of the
    /// feature — is testable without driving the hosted-service loop.</summary>
    internal async Task SweepAsync(int afterDays, int batchSize, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var tasksApi = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var projectsApi = scope.ServiceProvider.GetRequiredService<IProjectsApi>();

        var cutoff = DateTimeOffset.UtcNow.AddDays(-afterDays);
        var candidates = await tasksApi.ListTasksAwaitingAutoArchiveAsync(cutoff, batchSize, ct);
        if (candidates.Count == 0) return;

        // One batched read for every project involved, then the flag decides. A project that
        // has since been deleted is simply absent from the result and its tasks are skipped.
        var projectIds = candidates.Select(c => c.ProjectId).Distinct().ToList();
        var projects = await projectsApi.GetProjectSummariesAsync(projectIds, ct);

        var eligible = candidates
            .Where(c => projects.TryGetValue(c.ProjectId, out var project)
                && project.Settings is { AutoArchiveDoneTasks: true })
            .Select(c => c.Id)
            .ToList();

        if (eligible.Count == 0) return;

        var archived = await tasksApi.ArchiveTasksAsync(eligible, ct);
        if (archived.Count == 0) return;

        logger.LogInformation(
            "Auto-archived {Count} task(s) that had been Done since before {Cutoff:u}",
            archived.Count, cutoff);

        // Board pages hold their cards over SignalR; without this the archived task stays on
        // screen until the next reload. Best-effort — the row is already archived either way.
        foreach (var task in archived)
        {
            // Nobody clicked anything here, so "where did my task go" has to be answerable
            // from the log. Debug rather than Information: a first sweep over an old project
            // can carry hundreds of these, and the count above is the line worth keeping.
            logger.LogDebug(
                "Auto-archived task #{Number} '{Title}' (Done since {CompletedAt:u})",
                task.Number, task.Title, task.CompletedAt);

            try
            {
                await taskNotifier.SendTaskUpdatedAsync(task.ProjectId, new TaskUpdatedEvent(
                    task.Id, null, null, Contracts.TaskStatus.Archived.ToString(),
                    Lane.Done.ToString(), true, DateTimeOffset.UtcNow));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Auto-archived task {TaskId} but could not notify its board", task.Id);
            }
        }
    }
}
