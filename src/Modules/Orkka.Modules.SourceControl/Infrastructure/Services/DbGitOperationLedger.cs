namespace Orkka.Modules.SourceControl.Infrastructure.Services;

using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orkka.Git;
using Orkka.Modules.SourceControl.Domain;

/// <summary>
/// The ledger a host with a database uses: every finished operation becomes a row in
/// <c>sourcecontrol.git_operations</c>, written by a background writer so recording never
/// slows or fails the operation it describes. Successful read-only browsing is not kept —
/// it is the one kind frequent enough to matter for volume and uninteresting when it works.
/// </summary>
public sealed class DbGitOperationLedger(GitTopology topology, ILogger<DbGitOperationLedger> logger)
    : IGitOperationLedger
{
    private readonly Channel<GitOperationRecord> _queue = Channel.CreateBounded<GitOperationRecord>(
        new BoundedChannelOptions(4096) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    public string Topology { get; } = topology.Name;

    internal ChannelReader<GitOperationRecord> Reader => _queue.Reader;

    public void Record(GitOperationRecord record)
    {
        if (record.Succeeded && record.Kind == GitOperationKind.Read)
            return;

        if (!_queue.Writer.TryWrite(record))
            logger.LogDebug("Git operation ledger queue full — dropped a {Kind} record", record.Kind);

        // The log line stays: the ledger is for counting, the log is for the person reading
        // a pod's output right now.
        if (!record.Succeeded)
        {
            logger.LogWarning(
                "[GitOp] {Kind} FAILED {Code} in {Ms}ms repo={RepoId} task={TaskId}: {Error} (last: git {Command})",
                record.Kind, record.Code, (int)record.Duration.TotalMilliseconds,
                record.RepoId, record.TaskId, record.Error, record.LastCommand);
        }
    }
}

/// <summary>Drains the ledger queue into the database in small batches.</summary>
public sealed class GitOperationLedgerWriter(
    IGitOperationLedger ledger,
    IServiceScopeFactory scopeFactory,
    ILogger<GitOperationLedgerWriter> logger) : BackgroundService
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (ledger is not DbGitOperationLedger db)
            return;

        var batch = new List<GitOperationRecord>(64);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                wait.CancelAfter(FlushInterval);
                try
                {
                    while (batch.Count < 64 && await db.Reader.WaitToReadAsync(wait.Token))
                        while (batch.Count < 64 && db.Reader.TryRead(out var record))
                            batch.Add(record);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // Flush interval elapsed — write what we have.
                }

                if (batch.Count > 0)
                {
                    await WriteAsync(batch, stoppingToken);
                    batch.Clear();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Git operation ledger write failed — {Count} record(s) dropped", batch.Count);
                batch.Clear();
                try { await Task.Delay(FlushInterval, stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task WriteAsync(List<GitOperationRecord> batch, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<SourceControlDbContext>();
        context.GitOperations.AddRange(batch.Select(GitOperation.From));
        await context.SaveChangesAsync(ct);
    }
}
