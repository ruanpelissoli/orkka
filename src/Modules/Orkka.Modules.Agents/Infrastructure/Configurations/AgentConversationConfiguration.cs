namespace Orkka.Modules.Agents.Infrastructure.Configurations;

using Orkka.Modules.Agents.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class AgentConversationConfiguration : IEntityTypeConfiguration<AgentConversation>
{
    public void Configure(EntityTypeBuilder<AgentConversation> builder)
    {
        builder.ToTable("agent_conversations");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Title).HasMaxLength(500);
        builder.Property(x => x.ClaudeSessionId).HasMaxLength(100);
        builder.Property(x => x.SessionProvider).HasMaxLength(30);

        builder.HasIndex(x => x.OrgId);
        builder.HasIndex(x => new { x.ProjectId, x.AgentId });
        builder.HasIndex(x => new { x.AgentId, x.CreatedAt });

        // One Discuss conversation per (task, agent); the Team page's chats have no task.
        builder.HasIndex(x => new { x.TaskId, x.AgentId })
            .IsUnique()
            .HasFilter("\"TaskId\" IS NOT NULL");

        builder.HasOne(x => x.Agent)
            .WithMany(a => a.Conversations)
            .HasForeignKey(x => x.AgentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
