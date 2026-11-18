namespace Orkka.Modules.Tasks.Infrastructure.Services;

using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.SharedKernel.Events;
using Orkka.SharedKernel.Events.Catalog;

/// <summary>
/// Raises interactive-session approval prompts, waits for a human, and resolves whoever is
/// blocking on them. See <see cref="IInteractiveApprovalCoordinator"/>.
///
/// Singleton, because the waiter and the answer arrive on different requests: the agent's hook is
/// blocked inside one call while the human's click comes in on another.
/// </summary>
public sealed class InteractiveApprovalCoordinator(
    IServiceScopeFactory scopeFactory,
    ITaskNotifier taskNotifier,
    ILogger<InteractiveApprovalCoordinator> logger) : IInteractiveApprovalCoordinator
{
    /// <summary>Prompts this process is currently blocking on, keyed by approval id.</summary>
    private readonly ConcurrentDictionary<Ulid, PendingWait> _waiting = new();

    private sealed record PendingWait(Ulid TaskId, TaskCompletionSource<ApprovalOutcome> Completion);

    public async Task<ApprovalOutcome> RequestAsync(
        Ulid orgId, Ulid taskId, Ulid? executionId, string toolUseId, string tool,
        string command, string approvalKey, TimeSpan timeout, CancellationToken ct)
    {
        Ulid approvalId;
        PersistedApproval persisted;
        try
        {
            persisted = await PersistAsync(
                orgId, taskId, executionId, toolUseId, tool, command, approvalKey, ct);
            approvalId = persisted.ApprovalId;
        }
        catch (Exception ex)
        {
            // No row means no prompt can reach the browser, so nobody can ever say yes. Refusing
            // is the only honest answer — the alternative is running an ungated command while the
            // UI shows nothing at all.
            logger.LogError(ex,
                "Could not record an approval request for task {TaskId} — denying '{Command}'",
                taskId, command);
            return ApprovalOutcome.Denied;
        }

        // RunContinuationsAsynchronously: the decision arrives on an HTTP request thread, and
        // without this the agent's continuation would run inline on it — the browser's click
        // would appear to hang for as long as the hook takes to return.
        var completion = new TaskCompletionSource<ApprovalOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[approvalId] = new PendingWait(taskId, completion);

        try
        {
            await NotifyRaisedAsync(taskId, approvalId, toolUseId, tool, command, approvalKey);
            // Only a fresh prompt notifies. A retried hub invoke reuses the row (see PersistAsync),
            // and re-notifying would push the same "waiting for you" twice.
            if (persisted.IsNew)
                await PublishRaisedAsync(persisted, orgId, taskId, tool, command);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            await using var registration = timeoutCts.Token.Register(
                () => completion.TrySetResult(ApprovalOutcome.Expired));

            // The TCS resolves only when the decision lands in THIS process. Hosted, it
            // usually doesn't: the agent's hook blocks here in the Worker while the click
            // arrives at an API pod's coordinator instance. The row is the arbiter either
            // way, so poll it as the cross-process path — without this, every cloud
            // approval sat unanswered for the full timeout and then denied.
            var outcome = await WaitForDecisionAsync(approvalId, completion);

            if (outcome == ApprovalOutcome.Expired)
            {
                logger.LogInformation(
                    "Approval {ApprovalId} on task {TaskId} expired unanswered after {Seconds}s",
                    approvalId, taskId, timeout.TotalSeconds);
                await SettleAsync(approvalId, ApprovalStatus.Expired, userId: null, CancellationToken.None);
                await NotifySettledAsync(taskId, approvalId, ApprovalStatus.Expired);
            }

            return outcome;
        }
        finally
        {
            _waiting.TryRemove(approvalId, out _);
        }
    }

    public async Task<bool> DecideAsync(
        Ulid orgId, Ulid taskId, Ulid approvalId, ApprovalDecision decision, string? userId,
        CancellationToken ct)
    {
        var status = decision switch
        {
            ApprovalDecision.Allow => ApprovalStatus.Allowed,
            ApprovalDecision.AllowForSession => ApprovalStatus.AllowedForSession,
            _ => ApprovalStatus.Denied,
        };

        // The database is the arbiter, not the in-memory map: the update only matches while the
        // row is still Pending, so a double click, or a decision racing the expiry sweep, settles
        // exactly once and the loser is told so.
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();

        var updated = await db.TaskApprovalRequests
            .Where(a => a.Id == approvalId && a.OrgId == orgId && a.TaskId == taskId
                && a.Status == ApprovalStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, status)
                .SetProperty(a => a.DecidedByUserId, userId)
                .SetProperty(a => a.DecidedAt, DateTimeOffset.UtcNow)
                .SetProperty(a => a.UpdatedAt, DateTimeOffset.UtcNow),
                ct);

        if (updated == 0)
            return false;

        logger.LogInformation(
            "Approval {ApprovalId} on task {TaskId} {Status} by {UserId}",
            approvalId, taskId, status, userId ?? "(unknown)");

        // The waiter may live in another process (hosted multi-pod) or be gone entirely (its run
        // died). The row is still authoritative and the UI still updates — resolving the local
        // wait is a best-effort fast path, not the mechanism.
        if (_waiting.TryGetValue(approvalId, out var wait))
        {
            wait.Completion.TrySetResult(status switch
            {
                ApprovalStatus.Allowed => ApprovalOutcome.Allowed,
                ApprovalStatus.AllowedForSession => ApprovalOutcome.AllowedForSession,
                _ => ApprovalOutcome.Denied,
            });
        }

        await NotifySettledAsync(taskId, approvalId, status);
        return true;
    }

    public async Task<IReadOnlyList<PendingApproval>> ListPendingAsync(
        Ulid orgId, Ulid taskId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();

        return await db.TaskApprovalRequests
            .AsNoTracking()
            .Where(a => a.OrgId == orgId && a.TaskId == taskId && a.Status == ApprovalStatus.Pending)
            .OrderBy(a => a.CreatedAt)
            .Select(a => new PendingApproval(
                a.Id, a.TaskId, a.ToolUseId, a.Tool, a.Command, a.ApprovalKey, a.CreatedAt))
            .ToListAsync(ct);
    }

    public async Task ExpirePendingAsync(Ulid taskId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();

        var pending = await db.TaskApprovalRequests
            .Where(a => a.TaskId == taskId && a.Status == ApprovalStatus.Pending)
            .Select(a => a.Id)
            .ToListAsync(ct);

        if (pending.Count == 0)
            return;

        await db.TaskApprovalRequests
            .Where(a => a.TaskId == taskId && a.Status == ApprovalStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, ApprovalStatus.Expired)
                .SetProperty(a => a.UpdatedAt, DateTimeOffset.UtcNow),
                ct);

        logger.LogInformation(
            "Expired {Count} pending approval(s) on task {TaskId}", pending.Count, taskId);

        foreach (var id in pending)
        {
            if (_waiting.TryGetValue(id, out var wait))
                wait.Completion.TrySetResult(ApprovalOutcome.Expired);

            await NotifySettledAsync(taskId, id, ApprovalStatus.Expired);
        }
    }

    /// <summary>How often the waiter re-reads its row for a decision made in another process.
    /// A human is on the other end, so a couple of seconds of added latency is invisible.</summary>
    private static readonly TimeSpan DecisionPollInterval = TimeSpan.FromSeconds(2);

    private async Task<ApprovalOutcome> WaitForDecisionAsync(
        Ulid approvalId, TaskCompletionSource<ApprovalOutcome> completion)
    {
        while (true)
        {
            // The delay is never cancelled: timeout resolves the TCS (registration above), so
            // the loop always exits through it at the latest.
            var winner = await Task.WhenAny(
                completion.Task, Task.Delay(DecisionPollInterval, CancellationToken.None));
            if (winner == completion.Task)
                return await completion.Task;

            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();

                var status = await db.TaskApprovalRequests
                    .AsNoTracking()
                    .Where(a => a.Id == approvalId)
                    .Select(a => (ApprovalStatus?)a.Status)
                    .FirstOrDefaultAsync(CancellationToken.None);

                if (status is { } settled && settled != ApprovalStatus.Pending)
                {
                    return settled switch
                    {
                        ApprovalStatus.Allowed => ApprovalOutcome.Allowed,
                        ApprovalStatus.AllowedForSession => ApprovalOutcome.AllowedForSession,
                        ApprovalStatus.Expired => ApprovalOutcome.Expired,
                        _ => ApprovalOutcome.Denied,
                    };
                }
            }
            catch (Exception ex)
            {
                // A failed poll is a slower answer, not a denial — the timeout still bounds it.
                logger.LogWarning(ex, "Approval poll failed for {ApprovalId}", approvalId);
            }
        }
    }

    /// <summary>What PersistAsync learned about the prompt: the row id, whether it was just
    /// created, and the task facts the notification event needs (read in the same scope so the
    /// hot path costs one extra projection, not a second round trip later).</summary>
    private sealed record PersistedApproval(
        Ulid ApprovalId, bool IsNew, Ulid ProjectId, int TaskNumber, string TaskTitle, string? SessionOwnerUserId);

    private async Task<PersistedApproval> PersistAsync(
        Ulid orgId, Ulid taskId, Ulid? executionId, string toolUseId, string tool,
        string command, string approvalKey, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();

        var task = await db.Tasks
            .AsNoTracking()
            .Where(t => t.Id == taskId)
            .Select(t => new { t.ProjectId, t.Number, t.Title, t.CreatedByUserId })
            .FirstOrDefaultAsync(ct);

        // A retried hub invoke re-raises the same tool call. Reuse the existing row rather than
        // colliding on the unique index — and if the human already answered it, that answer is
        // the one to honour.
        var existing = await db.TaskApprovalRequests
            .FirstOrDefaultAsync(a => a.TaskId == taskId && a.ToolUseId == toolUseId, ct);
        if (existing is not null)
            return new PersistedApproval(existing.Id, IsNew: false,
                task?.ProjectId ?? Ulid.Empty, task?.Number ?? 0, task?.Title ?? "", task?.CreatedByUserId);

        var request = new TaskApprovalRequest
        {
            Id = Ulid.NewUlid(),
            OrgId = orgId,
            TaskId = taskId,
            ExecutionId = executionId,
            ToolUseId = toolUseId,
            Tool = tool,
            Command = Truncate(command, 8000),
            ApprovalKey = Truncate(approvalKey, 200),
        };

        db.TaskApprovalRequests.Add(request);
        await db.SaveChangesAsync(ct);
        return new PersistedApproval(request.Id, IsNew: true,
            task?.ProjectId ?? Ulid.Empty, task?.Number ?? 0, task?.Title ?? "", task?.CreatedByUserId);
    }

    /// <summary>Hands the prompt to the notification pipeline (Organizations module) for the
    /// session owner who is not watching the stream — the push to their phone. Best-effort:
    /// the prompt is already on the stream and in the table; a lost event costs a push.</summary>
    private async Task PublishRaisedAsync(
        PersistedApproval persisted, Ulid orgId, Ulid taskId, string tool, string command)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var eventBus = scope.ServiceProvider.GetRequiredService<IEventBus>();
            await eventBus.PublishAsync(new InteractiveApprovalRequested(
                persisted.ApprovalId, taskId, orgId, persisted.ProjectId,
                persisted.TaskNumber, persisted.TaskTitle, persisted.SessionOwnerUserId,
                tool, Truncate(command, 500)));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to publish approval-requested event for {ApprovalId}", persisted.ApprovalId);
        }
    }

    private async Task SettleAsync(
        Ulid approvalId, ApprovalStatus status, string? userId, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();

            var updated = await db.TaskApprovalRequests
                .Where(a => a.Id == approvalId && a.Status == ApprovalStatus.Pending)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.Status, status)
                    .SetProperty(a => a.DecidedByUserId, userId)
                    .SetProperty(a => a.UpdatedAt, DateTimeOffset.UtcNow),
                    ct);

            // Tell the notification pipeline the decision landed, so a "waiting for your
            // approval" push/badge stops pointing at a settled prompt. Only when this call did
            // the settling — a no-op update means someone else already published.
            if (updated > 0)
            {
                var row = await db.TaskApprovalRequests
                    .AsNoTracking()
                    .Where(a => a.Id == approvalId)
                    .Select(a => new { a.TaskId, a.OrgId })
                    .FirstOrDefaultAsync(ct);
                if (row is not null)
                {
                    var eventBus = scope.ServiceProvider.GetRequiredService<IEventBus>();
                    await eventBus.PublishAsync(
                        new InteractiveApprovalSettled(approvalId, row.TaskId, row.OrgId, status.ToString()), ct);
                }
            }
        }
        catch (Exception ex)
        {
            // The agent already has its answer; a stale row only misleads the audit trail.
            logger.LogWarning(ex, "Failed to settle approval {ApprovalId} as {Status}", approvalId, status);
        }
    }

    /// <summary>Prompts and their outcomes ride the task's existing stream, so a browser already
    /// watching the session needs no new subscription. Best-effort: a lost push costs a reload,
    /// while throwing here would fail the agent's command instead.</summary>
    private async Task NotifyRaisedAsync(
        Ulid taskId, Ulid approvalId, string toolUseId, string tool, string command, string approvalKey)
    {
        try
        {
            await taskNotifier.SendStreamEventAsync(taskId, new StreamEvent(
                "ApprovalRequested",
                command,
                DateTimeOffset.UtcNow,
                new
                {
                    approvalId = approvalId.ToString(),
                    toolUseId,
                    tool,
                    approvalKey,
                }));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to push approval {ApprovalId} to task {TaskId}", approvalId, taskId);
        }
    }

    private async Task NotifySettledAsync(Ulid taskId, Ulid approvalId, ApprovalStatus status)
    {
        try
        {
            await taskNotifier.SendStreamEventAsync(taskId, new StreamEvent(
                "ApprovalSettled",
                status.ToString(),
                DateTimeOffset.UtcNow,
                new { approvalId = approvalId.ToString(), status = status.ToString() }));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to push approval outcome for {ApprovalId}", approvalId);
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
