namespace Orkka.Modules.Execution.Orchestration;

public interface ITaskOrchestrator
{
    /// <summary>
    /// Executes the full task lifecycle: Planning → (approval) → Developing → PR → CodeReview → Done.
    /// Called by the queue processor when a task is picked up.
    /// </summary>
    Task ExecuteAsync(Ulid taskId, CancellationToken ct = default);

    /// <summary>
    /// Resolves merge conflicts on a task's PR branch and retries merge.
    /// </summary>
    Task ResolveConflictsAndMergeAsync(Ulid taskId, CancellationToken ct = default);

    /// <summary>
    /// Drives one interactive development session until it is finished, abandoned, or this worker
    /// stops. Called by the queue processor once it holds the task's lease — which must then be
    /// heartbeat-extended for the whole session, since every turn has to land on the machine
    /// holding the warm CLI session. See .claude/docs/INTERACTIVE.md.
    /// </summary>
    Task RunInteractiveSessionAsync(Ulid taskId, CancellationToken ct = default);
}
