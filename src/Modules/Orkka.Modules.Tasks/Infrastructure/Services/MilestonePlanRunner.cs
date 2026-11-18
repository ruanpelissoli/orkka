namespace Orkka.Modules.Tasks.Infrastructure.Services;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Features.Milestones;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.SharedKernel.Ai;

/// <summary>
/// Executes one claimed <see cref="MilestonePlanJob"/>: generates the plan, persists it, and
/// tells whoever is watching.
///
/// Split from the queue processor so the interesting half is a scoped service that can be
/// driven directly by a test, rather than only reachable through a five-second poll loop.
/// </summary>
public sealed class MilestonePlanRunner(
    TasksDbContext db,
    ITaskDecomposer decomposer,
    ITaskNotifier notifier,
    IServiceScopeFactory scopeFactory,
    ILogger<MilestonePlanRunner> logger)
{
    public async Task RunAsync(Ulid jobId, CancellationToken ct)
    {
        var job = await db.MilestonePlanJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null)
        {
            // The project was deleted between the claim and here. Nothing to do and nothing
            // to report — there is no longer anyone to report it to.
            logger.LogWarning("Milestone plan {JobId} vanished before it could run", jobId);
            return;
        }

        // Every step transition is persisted before it is broadcast. A client that reconnects
        // mid-run reads the row, so a step that only ever existed as a SignalR frame would be
        // invisible to it — and refreshing the page is exactly what these users do.
        var progress = new Progress<string>(step => _ = ReportStepAsync(job.Id, job.ProjectId, step));

        try
        {
            var proposal = await decomposer.ProposeAsync(
                new DecompositionRequest(
                    job.OrgId, job.ProjectId, job.Goal,
                    job.RepoId, job.MilestoneId,
                    job.RequesterLocale, job.RequesterUserId),
                progress,
                ct);

            var plan = new MilestonePlanResponse(
                proposal.MilestoneTitle,
                proposal.Outcome,
                // Key is the task's position as planned; DependsOn references those keys, not
                // array positions, so pruning the list client-side can't corrupt the ordering.
                proposal.Tasks
                    .Select((t, i) => new PlannedTaskResponse(
                        t.Title, t.Description, t.Priority, t.Type, i, t.DependsOn, t.Repo,
                        t.ContractKey))
                    .ToList(),
                job.RepoId,
                proposal.Contracts?
                    .Select(c => new PlannedContractResponse(c.Key, c.Contract))
                    .ToList());

            await CompleteAsync(job, plan);

            logger.LogInformation(
                "Milestone plan {JobId} completed with {Count} task(s) for project {ProjectId}",
                job.Id, plan.Tasks.Count, job.ProjectId);
        }
        catch (RunnerUtilityUnavailableException ex)
        {
            // Planning runs only on the requester's runner — there is no platform-key
            // fallback. Its own code so the client says "connect your runner" instead of
            // "rephrase the goal", which would blame the user for something they did not cause.
            await FailAsync(job, MilestonePlanFailureCodes.RunnerOffline, ex.Message);
        }
        catch (ModelOutputException ex)
        {
            // The model replied with something unusable — truncated, fenced prose, no tasks.
            // Retrying is the right move, and saying so beats "rephrase the goal", which
            // blames the user for something they did not cause.
            logger.LogWarning(ex, "Unusable plan for project {ProjectId}", job.ProjectId);
            await FailAsync(job, MilestonePlanFailureCodes.Unusable,
                $"{ex.Message} Try again, or narrow the goal so the plan is shorter.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The host is shutting down, not the user leaving — that distinction is the whole
            // point of this class. Leave the row Running: the stale sweep re-queues it and
            // another replica plans it, rather than the user being told their plan failed
            // because a pod was recycled.
            logger.LogInformation(
                "Milestone plan {JobId} interrupted by shutdown — leaving it for re-queue",
                job.Id);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Milestone planning failed for project {ProjectId}", job.ProjectId);
            // The root cause, not a guess: EF and HttpClient both bury the useful message in a
            // wrapper, and a blanket "try rephrasing" made a broken dependency look like bad
            // input. Truncated because these can run long.
            var cause = ex.GetBaseException().Message;
            await FailAsync(job, MilestonePlanFailureCodes.Error,
                $"Planning failed: {(cause.Length > 300 ? cause[..300] : cause)}");
        }
    }

    /// <summary>
    /// Records the phase the run has reached.
    ///
    /// Deliberately untracked and fire-and-forget: progress is a courtesy to whoever is
    /// watching, and a failed UPDATE on the step column must never be able to take down the
    /// generation it is describing. Uses <see cref="CancellationToken.None"/> for the same
    /// reason — a cancelled step write would surface as a cancelled plan.
    ///
    /// Its own scope, and therefore its own DbContext: <see cref="Progress{T}"/> raises the
    /// callback on the thread pool, so writing through this instance's context would have it
    /// used concurrently with the decomposer's own reads on the very same context.
    /// </summary>
    private async Task ReportStepAsync(Ulid jobId, Ulid projectId, string step)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;

            using var scope = scopeFactory.CreateScope();
            var stepDb = scope.ServiceProvider.GetRequiredService<TasksDbContext>();

            // Only while Running: a late report racing a completion must not put a progress
            // label back on a finished plan.
            var rows = await stepDb.MilestonePlanJobs
                .Where(j => j.Id == jobId && j.Status == MilestonePlanStatus.Running)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.CurrentStep, step)
                    .SetProperty(j => j.UpdatedAt, now),
                    CancellationToken.None);

            if (rows == 0)
                return;

            await notifier.SendMilestonePlanChangedAsync(projectId, new MilestonePlanChangedEvent(
                jobId, projectId, null, nameof(MilestonePlanStatus.Running), step,
                null, null, null, now));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not report step {Step} for milestone plan {JobId}", step, jobId);
        }
    }

    private async Task CompleteAsync(MilestonePlanJob job, MilestonePlanResponse plan)
    {
        job.Status = MilestonePlanStatus.Completed;
        job.CurrentStep = null;
        job.ResultJson = MilestonePlanSerialization.Serialize(plan);
        job.CompletedAt = DateTimeOffset.UtcNow;

        // CancellationToken.None: the plan is generated and paid for. Losing it here because
        // the host started draining a second later would be the same defect this change exists
        // to remove, just moved further down the method.
        await db.SaveChangesAsync(CancellationToken.None);

        await notifier.SendMilestonePlanChangedAsync(
            job.ProjectId, MilestonePlanNotifications.From(job));
    }

    private async Task FailAsync(MilestonePlanJob job, string code, string message)
    {
        job.Status = MilestonePlanStatus.Failed;
        job.CurrentStep = null;
        job.FailureCode = code;
        job.ErrorMessage = message;
        job.CompletedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(CancellationToken.None);

        await notifier.SendMilestonePlanChangedAsync(
            job.ProjectId, MilestonePlanNotifications.From(job));
    }
}
