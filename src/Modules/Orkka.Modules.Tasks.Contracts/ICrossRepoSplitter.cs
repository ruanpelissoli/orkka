namespace Orkka.Modules.Tasks.Contracts;

/// <summary>
/// Turns one request that spans repositories into one task per repository, plus the contract
/// they all code against.
///
/// Separate from <c>ITaskDecomposer</c>, which answers a different question. Decomposition plans
/// a goal into independent tasks and may happen to file them in different repos; this splits a
/// SINGLE piece of work whose halves are useless apart, and its whole job is the contract that
/// lets those halves be written at the same time.
/// </summary>
public interface ICrossRepoSplitter
{
    /// <summary>
    /// Splits the task's group across its declared repositories. Writes the contract, rewrites
    /// the originating task as its own repo's half, creates the sibling tasks, and records the
    /// build order as real edges.
    ///
    /// Never throws for a planning failure: an unusable model reply cancels the group and leaves
    /// the originating task as an ordinary single-repo task, which is worse than a split but far
    /// better than half a plan.
    /// </summary>
    Task SplitAsync(Ulid taskId, CancellationToken ct = default);
}
