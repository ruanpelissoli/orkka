namespace Orkka.Modules.Tasks.Domain;

public interface ITaskNumberService
{
    /// <summary>Reserves the next task number for the project. Call it BEFORE opening a write
    /// transaction: the counter is on the projects table, written through its own connection,
    /// and SQLite's single writer would make it wait on the caller's own transaction. A failed
    /// insert afterwards leaves a gap in the numbering, which is accepted.</summary>
    Task<int> ReserveAsync(Ulid projectId, CancellationToken ct = default);

    /// <summary>Reserves a contiguous range of task numbers and returns the last one.
    /// The range is [result - count + 1, result].</summary>
    Task<int> ReserveRangeAsync(Ulid projectId, int count, CancellationToken ct = default);
}
