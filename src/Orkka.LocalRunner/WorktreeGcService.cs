namespace Orkka.LocalRunner;

using Orkka.Modules.SourceControl.Contracts;

/// <summary>
/// Age-based worktree GC for the runner's own disk.
///
/// The host cannot do this for us: <c>RunnerGitWorkspaceManager.CleanupStaleWorktreesAsync</c>
/// returns 0 without a round trip, because enumerating a filesystem the host does not have
/// is impossible. That made the runner the only place a stale worktree could be reclaimed —
/// and nothing here swept them, so a task whose per-merge cleanup never landed (offline
/// runner, abandoned/escalated task, a PR closed without merging) left its worktree on the
/// user's machine forever. They pile up as branch rows in the editor's source-control view.
///
/// Same retention and interval keys as the worker's sweep, so both sides age worktrees out
/// on one policy.
/// </summary>
public sealed class WorktreeGcService(
    IGitWorkspaceManager git,
    IConfiguration configuration,
    ILogger<WorktreeGcService> logger) : BackgroundService
{
    private readonly TimeSpan _retention = TimeSpan.FromHours(
        configuration.GetValue<int>("Worker:WorktreeRetentionHours", 72));
    private readonly TimeSpan _interval = TimeSpan.FromMinutes(
        configuration.GetValue<int>("Worker:WorktreeGcIntervalMinutes", 30));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A worktree is only stale by last-write time, so an in-flight run is never at
        // risk — but the very first sweep still waits out one interval, which keeps GC
        // off the startup path where the user is watching the runner connect.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_interval, stoppingToken);
                var removed = await git.CleanupStaleWorktreesAsync(_retention, stoppingToken);
                if (removed > 0)
                    logger.LogInformation(
                        "Worktree GC removed {Count} worktree(s) older than {Hours}h",
                        removed, _retention.TotalHours);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let a bad sweep take the runner down — the next one retries.
                logger.LogWarning(ex, "Worktree GC sweep failed");
            }
        }
    }
}
