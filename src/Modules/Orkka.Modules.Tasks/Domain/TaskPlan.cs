namespace Orkka.Modules.Tasks.Domain;

using Orkka.Modules.Tasks.Contracts;
using Orkka.SharedKernel.Domain;

using Orkka.Modules.Tasks.Domain;

public sealed class TaskPlan : TenantEntity
{
    public Ulid TaskId { get; set; }
    public int Version { get; set; } = 1;
    public required PlanContent Content { get; set; }
    public PlanStatus Status { get; set; } = PlanStatus.Draft;
    public string? Feedback { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }

    // Navigation
    public TaskEntity Task { get; set; } = null!;
}
