namespace Orkka.Modules.Execution.Orchestration;

using Orkka.Modules.Tasks.Contracts;

/// <summary>
/// Records a merge failure on the task and broadcasts it (board badge + task thread).
/// Shared by the merge poller and the conflict-resolution orchestration so every
/// terminal merge failure surfaces the same way. Change-detected inside
/// <see cref="ITasksApi.SetTaskMergeFailureAsync"/> — repeated identical reasons
/// (the poller retries every cycle) produce no new message or broadcast.
/// </summary>
internal static class MergeFailureNotifications
{
    /// <summary>Returns true when the reason was new (recorded + broadcast) — callers use
    /// this to log loudly once instead of every poller cycle.</summary>
    public static async Task<bool> NotifyMergeFailureAsync(
        ITasksApi tasksApi, ITaskNotifier taskNotifier, Ulid taskId, string reason,
        CancellationToken ct, MergeFailureKind kind = MergeFailureKind.Unknown)
    {
        var update = await tasksApi.SetTaskMergeFailureAsync(taskId, reason, kind, ct);
        if (update is not { Changed: true }) return false;

        await taskNotifier.SendTaskMergeFailedAsync(update.ProjectId,
            new TaskMergeFailedEvent(taskId, reason, DateTimeOffset.UtcNow));

        if (update.Message is { } message)
        {
            await taskNotifier.SendNewMessageAsync(taskId, new NewMessageEvent(
                taskId, message.Id, message.Role, message.Content,
                message.MessageType.ToString(), message.SenderId, message.CreatedAt));
        }

        return true;
    }
}
