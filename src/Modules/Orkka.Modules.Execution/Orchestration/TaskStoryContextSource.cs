namespace Orkka.Modules.Execution.Orchestration;

using Microsoft.Extensions.DependencyInjection;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Trackers.Contracts;

/// <summary>
/// The task Discuss chat's view of the Story context: the same section the pipeline prompts
/// carry (<see cref="TaskOrchestrator.LoadStoryContextSectionAsync"/>), so a conversation about a
/// story member knows the story, its sub-tasks and what the earlier ones did.
/// </summary>
internal sealed class TaskStoryContextSource(ITasksApi tasksApi, IServiceProvider services) : ITaskStoryContextSource
{
    public async Task<string> BuildStoryContextSectionAsync(Ulid taskId, CancellationToken ct = default)
        => await tasksApi.GetTaskSnapshotAsync(taskId, ct) is { } task
            ? await TaskOrchestrator.LoadStoryContextSectionAsync(tasksApi, services.GetService<ITrackersApi>(), task, ct)
            : "";
}
