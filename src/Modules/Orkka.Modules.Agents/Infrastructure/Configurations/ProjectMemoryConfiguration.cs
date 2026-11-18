namespace Orkka.Modules.Agents.Infrastructure.Configurations;

using Orkka.Modules.Agents.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class ProjectMemoryConfiguration : IEntityTypeConfiguration<ProjectMemory>
{
    public void Configure(EntityTypeBuilder<ProjectMemory> builder)
    {
        builder.ToTable("project_memories");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.Source).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.Title).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Content).IsRequired();
        builder.Property(x => x.CreatedByUserId).HasMaxLength(200);
        builder.Property(x => x.DedupeKey).HasMaxLength(200);

        builder.HasIndex(x => new { x.OrgId, x.ProjectId, x.IsArchived });
        builder.HasIndex(x => x.AgentId);
        builder.HasIndex(x => x.TaskId);
        builder.HasIndex(x => new { x.ProjectId, x.DedupeKey }).IsUnique().HasFilter("\"DedupeKey\" IS NOT NULL");
        // Full-text: the FTS5 table project_memories_fts (MemorySearch) and the triggers that
        // keep it in step with this table are created in the Initial migration (raw SQL).

        // Deleting an agent keeps its notes as team knowledge rather than losing them.
        builder.HasOne(x => x.Agent)
            .WithMany(a => a.Memories)
            .HasForeignKey(x => x.AgentId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
