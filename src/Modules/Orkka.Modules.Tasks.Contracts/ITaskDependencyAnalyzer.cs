namespace Orkka.Modules.Tasks.Contracts;

public interface ITaskDependencyAnalyzer
{
    /// <summary>
    /// Analyzes a newly submitted task against other active/queued tasks in the same project
    /// and repo, detecting code-level dependencies via AI. Persists results to the database.
    /// </summary>
    Task AnalyzeDependenciesAsync(Ulid taskId, CancellationToken ct = default);
}
