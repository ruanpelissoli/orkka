namespace Orkka.Modules.Tasks.Domain;

using Orkka.Modules.Tasks.Contracts;
using Orkka.SharedKernel.Domain;

public sealed class TaskAttachment : TenantEntity
{
    public Ulid TaskId { get; set; }
    public required string FileUrl { get; set; }
    public required string FileType { get; set; }
    public required string FileName { get; set; }
    public long FileSize { get; set; }

    // Navigation
    public TaskEntity Task { get; set; } = null!;
}
