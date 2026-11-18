namespace Orkka.Modules.Agents.Infrastructure.Configurations;

using Orkka.Modules.Agents.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class ArchitectureDiagramConfiguration : IEntityTypeConfiguration<ArchitectureDiagram>
{
    public void Configure(EntityTypeBuilder<ArchitectureDiagram> builder)
    {
        builder.ToTable("architecture_diagrams");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Title).HasMaxLength(500).IsRequired();
        builder.Property(x => x.Type).HasConversion<string>().HasMaxLength(30);
        builder.Property(x => x.DiagramData).IsRequired();
        builder.Property(x => x.Description).HasColumnType("text");
        builder.Property(x => x.FolderPath).HasMaxLength(500).IsRequired();

        builder.HasIndex(x => x.OrgId);
        builder.HasIndex(x => new { x.ProjectId, x.AgentId });
        builder.HasIndex(x => x.ConversationId);

        builder.HasOne(x => x.Agent)
            .WithMany(a => a.Diagrams)
            .HasForeignKey(x => x.AgentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.Conversation)
            .WithMany()
            .HasForeignKey(x => x.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.Message)
            .WithMany()
            .HasForeignKey(x => x.MessageId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
