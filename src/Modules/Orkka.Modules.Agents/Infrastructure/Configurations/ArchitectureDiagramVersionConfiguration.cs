namespace Orkka.Modules.Agents.Infrastructure.Configurations;

using Orkka.Modules.Agents.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class ArchitectureDiagramVersionConfiguration : IEntityTypeConfiguration<ArchitectureDiagramVersion>
{
    public void Configure(EntityTypeBuilder<ArchitectureDiagramVersion> builder)
    {
        builder.ToTable("architecture_diagram_versions");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Title).HasMaxLength(500).IsRequired();
        builder.Property(x => x.Description).HasColumnType("text");
        builder.Property(x => x.DiagramData).IsRequired();
        builder.Property(x => x.ChangedByUserId).HasMaxLength(200);
        builder.Property(x => x.RequestedByUserId).HasMaxLength(200);

        builder.HasIndex(x => x.OrgId);
        builder.HasIndex(x => new { x.DiagramId, x.Version }).IsUnique();

        builder.HasOne(x => x.Diagram)
            .WithMany()
            .HasForeignKey(x => x.DiagramId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
