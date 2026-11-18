namespace Orkka.Modules.Agents.Domain;

using Orkka.Modules.Agents.Contracts;
using Orkka.SharedKernel.Domain;

public sealed class ArchitectureDiagram : TenantEntity
{
    // Cross-module reference (Projects module) — plain id, no navigation.
    public Ulid ProjectId { get; set; }
    public Ulid AgentId { get; set; }
    public Ulid ConversationId { get; set; }
    public Ulid? MessageId { get; set; }
    public required string Title { get; set; }
    public DiagramType Type { get; set; }
    public string DiagramData { get; set; } = "{}";
    public int Version { get; set; } = 1;
    public string? Description { get; set; }

    /// <summary>Virtual folder for organizing the Architecture board ("" = root,
    /// e.g. "backend/queues") — same path semantics as Documents.</summary>
    public string FolderPath { get; set; } = "";

    // Navigation (intra-module)
    public ProjectAgent Agent { get; set; } = null!;
    public AgentConversation Conversation { get; set; } = null!;
    public AgentMessage? Message { get; set; }
}
