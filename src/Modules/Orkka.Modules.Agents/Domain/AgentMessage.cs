namespace Orkka.Modules.Agents.Domain;

using Orkka.SharedKernel.Domain;

public sealed class AgentMessage : TenantEntity
{
    public Ulid ConversationId { get; set; }
    public required string Role { get; set; }  // "user" or "assistant"
    public required string Content { get; set; }

    // Navigation
    public AgentConversation Conversation { get; set; } = null!;
}
