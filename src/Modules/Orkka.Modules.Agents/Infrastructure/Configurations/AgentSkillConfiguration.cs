namespace Orkka.Modules.Agents.Infrastructure.Configurations;

using Orkka.Modules.Agents.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class AgentSkillConfiguration : IEntityTypeConfiguration<AgentSkill>
{
    public void Configure(EntityTypeBuilder<AgentSkill> builder)
    {
        builder.ToTable("agent_skills");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Slug).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(120).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(400);
        builder.Property(x => x.Content).HasColumnType("text");
        builder.Property(x => x.BuiltInKey).HasMaxLength(64);
        builder.Property(x => x.CreatedByUserId).HasMaxLength(120);
        builder.Property(x => x.UpdatedByUserId).HasMaxLength(120);

        builder.HasIndex(x => new { x.OrgId, x.ProjectId, x.Slug }).IsUnique();
        // One row per built-in per project — the seeder and the startup backfill are both
        // guarded by this, so concurrent hosts can't double-insert.
        builder.HasIndex(x => new { x.ProjectId, x.BuiltInKey })
            .IsUnique()
            .HasFilter("\"BuiltInKey\" IS NOT NULL");
    }
}
