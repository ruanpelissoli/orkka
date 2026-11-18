namespace Orkka.Modules.Tasks.Domain;

using Orkka.Modules.Tasks.Contracts;
using Orkka.SharedKernel.Domain;


public sealed class TaskMessage : TenantEntity
{
    public Ulid TaskId { get; set; }
    public required string Role { get; set; }
    public required string Content { get; set; }
    public MessageType MessageType { get; set; } = MessageType.Text;
    public string? SenderId { get; set; }
    public string? Metadata { get; set; }

    // Navigation
    public TaskEntity Task { get; set; } = null!;
}
