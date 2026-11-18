namespace Orkka.Modules.Agents.Infrastructure.Configurations;

using Orkka.Modules.Agents.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class ProjectAgentConfiguration : IEntityTypeConfiguration<ProjectAgent>
{
    public void Configure(EntityTypeBuilder<ProjectAgent> builder)
    {
        builder.ToTable("project_agents");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Role).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.Avatar).HasMaxLength(200);
        builder.Property(x => x.Model).HasMaxLength(100);
        builder.Property(x => x.ImageProvider).HasMaxLength(20);
        builder.Property(x => x.SystemPrompt).HasColumnType("text");
        builder.Property(x => x.UserInstructions).HasColumnType("text");
        builder.Property(x => x.TotalCostUsd).HasPrecision(12, 4);


        builder.HasIndex(x => x.OrgId);
        builder.HasIndex(x => new { x.ProjectId, x.Role });

        // Singleton specialist agents (AgentSeeder.Specialists) allow exactly one instance
        // per project — the seeder and the startup roster backfill are both guarded by this
        // index, so concurrent hosts can't double-insert. Other roles stay duplicable
        // (historical user-created agents), hence the filter.
        builder.HasIndex(x => new { x.ProjectId, x.Role }, "ix_project_agents_optional_role")
            .IsUnique()
            .HasFilter("\"Role\" IN ('GameDev','GameDesigner','DBA','Legal','MobileDev')");

    }
}
