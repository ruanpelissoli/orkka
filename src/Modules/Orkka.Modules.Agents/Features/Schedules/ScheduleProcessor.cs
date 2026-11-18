namespace Orkka.Modules.Agents.Features.Schedules;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orkka.Modules.Agents.Infrastructure;

/// <summary>
/// The schedule tick: every ~30 s claim the due, enabled schedules (set-based lease update —
/// losing a race is an ordinary outcome) and run each through <see cref="ScheduleRunner"/>.
/// Playbooks are chat turns; a runner-routed turn relays from here exactly like a typed one.
///
/// Firing is bounded per tick (a stuck host must not wake up to a burst of a hundred
/// overdue playbooks) and each firing runs on its own scope so one failure can't poison the
/// sweep.
/// </summary>
internal sealed class ScheduleProcessor(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<ScheduleProcessor> logger) : BackgroundService
{
    private const int MaxFiringsPerTick = 10;
    private const int MaxConcurrentFirings = 3;
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(45);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(configuration.GetValue("Automation:TickSeconds", 30));
        var owner = $"{Environment.MachineName}:{Environment.ProcessId}";

        // Let the host finish starting (migrations, seeders) before the first sweep.
        try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(owner, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Schedule sweep failed; retrying next tick");
            }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SweepAsync(string owner, CancellationToken ct)
    {
        List<Ulid> claimed;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
            var now = DateTimeOffset.UtcNow;

            var due = await db.AgentSchedules.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.IsEnabled && s.NextRunAt != null && s.NextRunAt <= now
                            && (s.LeaseExpiresAt == null || s.LeaseExpiresAt < now))
                .OrderBy(s => s.NextRunAt)
                .Take(MaxFiringsPerTick)
                .Select(s => s.Id)
                .ToListAsync(ct);
            if (due.Count == 0) return;

            claimed = [];
            var leaseUntil = now + LeaseDuration;
            foreach (var id in due)
            {
                // Set-based claim: the WHERE re-checks the lease so two hosts can't both win.
                var won = await db.AgentSchedules.IgnoreQueryFilters()
                    .Where(s => s.Id == id && s.IsEnabled && s.NextRunAt != null && s.NextRunAt <= now
                                && (s.LeaseExpiresAt == null || s.LeaseExpiresAt < now))
                    .ExecuteUpdateAsync(u => u
                        .SetProperty(s => s.LeaseOwner, owner)
                        .SetProperty(s => s.LeaseExpiresAt, leaseUntil), ct);
                if (won == 1) claimed.Add(id);
            }
        }

        if (claimed.Count == 0) return;
        logger.LogInformation("Schedule sweep claimed {Count} due schedule(s)", claimed.Count);

        // Playbook turns can run for minutes; a small degree of parallelism keeps a slow
        // one from delaying the rest without turning the host into a fan-out engine.
        using var gate = new SemaphoreSlim(MaxConcurrentFirings);
        var firings = claimed.Select(async id =>
        {
            await gate.WaitAsync(ct);
            try
            {
                using var scope = scopeFactory.CreateScope();
                var runner = scope.ServiceProvider.GetRequiredService<ScheduleRunner>();
                await runner.RunAsync(id, "cron", ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "Schedule {ScheduleId} firing crashed", id);
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(firings);
    }
}
