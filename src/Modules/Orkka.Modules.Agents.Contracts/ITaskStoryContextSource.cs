namespace Orkka.Modules.Agents.Contracts;

/// <summary>
/// The story context of a task that belongs to a Story (P7): the parent story, its sub-tasks in
/// order and what the earlier ones already did — the section the pipeline prompts carry. The
/// task's Discuss chat asks for it through this seam because the builder lives in the Execution
/// module (<c>TaskOrchestrator.BuildStoryContextSectionAsync</c>), which the Agents module
/// cannot reference. Optional: with no implementation registered the section is empty.
/// </summary>
public interface ITaskStoryContextSource
{
    /// <summary>The rendered markdown section, or an empty string when the task is not a story
    /// member. Untrusted content inside it is already fenced.</summary>
    Task<string> BuildStoryContextSectionAsync(Ulid taskId, CancellationToken ct = default);
}
