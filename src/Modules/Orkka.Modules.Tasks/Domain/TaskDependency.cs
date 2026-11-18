namespace Orkka.Modules.Tasks.Domain;

using Orkka.Modules.Tasks.Contracts;
using Orkka.SharedKernel.Domain;


public sealed class TaskDependency : TenantEntity
{
    public Ulid TaskId { get; set; }
    public Ulid DependsOnTaskId { get; set; }
    public string? Reason { get; set; }
    public DependencySource Source { get; set; }

    // Navigation
    public TaskEntity Task { get; set; } = null!;
    public TaskEntity DependsOnTask { get; set; } = null!;
}
