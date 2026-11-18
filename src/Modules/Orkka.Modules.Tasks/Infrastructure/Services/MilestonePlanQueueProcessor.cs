namespace Orkka.Modules.Tasks.Infrastructure.Services;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orkka.Modules.Runners.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Infrastructure;

/// <summary>
/// Runs queued milestone plans, off any request thread.
///
/// Modelled on <c>ProvisioningQueueProcessor</c> deliberately — poll, claim with optimistic
/// concurrency, execute in a per-job scope, recover rows a dead replica left Running — because
/// that is the shape the rest of the platform already coordinates API-to-worker work in, and a
/// second mechanism would be one more thing to reason about at 3am.
///
/// The cancellation token handed to the decomposer is this service's stopping token, never a
/// request's. That single fact is the fix: a browser that navigates away, sleeps, or closes no
/// longer destroys a generation the org has already paid for. It is also why this lives in the
/// Worker rather than the API host — the Worker drains in-flight work on SIGTERM for as long as
/// <c>Worker:DrainTimeoutMinutes</c> allows, while an API pod's shutdown budget is sized for
/// SSE turns measured in seconds and would cut a three-minute plan on every rolling deploy.
/// </summary>
public sealed class MilestonePlanQueueProcessor(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<MilestonePlanQueueProcessor> logger) : BackgroundService
{
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(
        configuration.GetValue<int>("Worker:MilestonePlanPollIntervalSeconds", 5));

    /// <summary>A run that has not moved in this long is presumed dead and re-queued.
    /// Comfortably above the slowest observed plan (210s against a user's own runner), because
    /// re-queuing a healthy run means paying the model for it twice.</summary>
    private readonly TimeSpan _staleTimeout = TimeSpan.FromMinutes(
        configuration.GetValue<int>("Worker:MilestonePlanStaleTimeoutMinutes", 20));

    /// <summary>How long a job whose requester prefers their own runner waits for the replica
    /// holding that socket before any replica may take it. Short — a user watching a spinner is
    /// the failure this whole change exists to prevent — but long enough that a healthy runner
    /// on another pod always wins the race.</summary>
    private readonly TimeSpan _affinityGrace = TimeSpan.FromSeconds(
        configuration.GetValue<int>("Worker:MilestonePlanAffinityGraceSeconds", 20));

    /// <summary>Jobs this process is already executing. The database claim is what makes the
    /// decision correct across replicas; this only stops one replica from re-entering a job of
    /// its own between the claim and the next poll.</summary>
    private readonly HashSet<Ulid> _running = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "MilestonePlanQueueProcessor started with {Interval}s poll interval",
            _pollInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RecoverStaleJobsAsync(stoppingToken);
                await ProcessQueueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing the milestone plan queue");
            }

            try
            {
                await Task.Delay(_pollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("MilestonePlanQueueProcessor stopped");
    }

    private async Task RecoverStaleJobsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();

        var recovered = await RecoverStaleJobsAsync(db, _staleTimeout, ct);
        if (recovered > 0)
        {
            logger.LogWarning(
                "Re-queued {Count} milestone plan job(s) stuck Running for > {Timeout}m",
                recovered, _staleTimeout.TotalMinutes);
        }
    }

    /// <summary>
    /// Returns jobs a crashed replica left Running to Pending.
    ///
    /// Measured from <c>UpdatedAt</c>, which every step transition refreshes, rather than from
    /// <c>StartedAt</c>: a live run that is merely slow keeps touching the row, so only a job
    /// nobody has moved for the whole window is genuinely orphaned.
    /// </summary>
    internal static Task<int> RecoverStaleJobsAsync(
        TasksDbContext db, TimeSpan staleTimeout, CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow - staleTimeout;

        return db.MilestonePlanJobs
            .Where(j => j.Status == MilestonePlanStatus.Running && j.UpdatedAt < cutoff)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, MilestonePlanStatus.Pending)
                .SetProperty(j => j.StartedAt, (DateTimeOffset?)null)
                .SetProperty(j => j.CurrentStep, MilestonePlanSteps.Queued)
                .SetProperty(j => j.UpdatedAt, DateTimeOffset.UtcNow),
                ct);
    }

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();

        var pending = await db.MilestonePlanJobs
            .AsNoTracking()
            .Where(j => j.Status == MilestonePlanStatus.Pending)
            .OrderBy(j => j.CreatedAt)
            .Take(20)
            .ToListAsync(ct);

        if (pending.Count == 0)
            return;

        foreach (var job in pending)
        {
            lock (_running)
            {
                if (!_running.Add(job.Id))
                    continue;
            }

            if (!ShouldClaimHere(scope.ServiceProvider, job)
                || !await TryClaimJobAsync(db, job.Id, ct))
            {
                lock (_running) { _running.Remove(job.Id); }
                continue;
            }

            logger.LogInformation(
                "Claimed milestone plan {JobId} for project {ProjectId}", job.Id, job.ProjectId);

            // Fire and forget in its own scope, exactly like the provisioning queue: one slow
            // plan must not hold the poll loop, and a per-job scope keeps its DbContext off the
            // one the sweep is using.
            _ = Task.Run(async () =>
            {
                try
                {
                    using var jobScope = scopeFactory.CreateScope();
                    await jobScope.ServiceProvider
                        .GetRequiredService<MilestonePlanRunner>()
                        .RunAsync(job.Id, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // The host is draining. The runner deliberately left the row Running so
                    // the stale sweep re-queues it — not an error, and logging it as one would
                    // make every rolling deploy look like a fault.
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "Unhandled exception running milestone plan {JobId}", job.Id);
                }
                finally
                {
                    lock (_running) { _running.Remove(job.Id); }
                }
            }, CancellationToken.None);
        }
    }

    /// <summary>
    /// Whether THIS process should take the job.
    ///
    /// The plan runs on the requester's own runner — there is no platform-key fallback — and
    /// only the replica holding their WebSocket can reach it; anywhere else the runner looks
    /// absent and the plan fails as "runner offline". So the socket's holder gets first
    /// refusal, whatever the requester's execution preference. This is the same rule
    /// <c>TaskQueueProcessor</c> applies to UserRunner tasks.
    ///
    /// Bounded by <see cref="_affinityGrace"/>: when the runner is offline everywhere no
    /// replica holds the socket, and deferring forever would leave the job pending until the
    /// stale sweep gave up on it. Past the window any replica takes it and fails it with the
    /// actionable "run `./orkka start`" message — an honest answer beats a spinner.
    /// </summary>
    private bool ShouldClaimHere(IServiceProvider services, MilestonePlanJob job)
    {
        // Hosts that compose no claim affinity have one process — there is no other replica
        // to defer to.
        var affinity = services.GetService<IRunnerClaimAffinity>();
        if (affinity is null)
            return true;

        return MilestonePlanClaim.ShouldClaimHere(
            affinity.CanExecuteHere(job.OrgId, job.RequesterUserId),
            DateTimeOffset.UtcNow - job.CreatedAt, _affinityGrace);
    }

    private static async Task<bool> TryClaimJobAsync(
        TasksDbContext db, Ulid jobId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        // The status IS the claim: two replicas racing for one row, and only the UPDATE that
        // still matches Pending comes back with a non-zero rowcount.
        var rows = await db.MilestonePlanJobs
            .Where(j => j.Id == jobId && j.Status == MilestonePlanStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, MilestonePlanStatus.Running)
                .SetProperty(j => j.CurrentStep, MilestonePlanSteps.ReadingProject)
                .SetProperty(j => j.StartedAt, now)
                .SetProperty(j => j.UpdatedAt, now),
                ct);

        return rows > 0;
    }
}
