namespace Orkka.Modules.Projects.Infrastructure.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Projects.Domain;

public sealed class ProjectRunConfigConfiguration : IEntityTypeConfiguration<ProjectRunConfig>
{
    public void Configure(EntityTypeBuilder<ProjectRunConfig> builder)
    {
        builder.ToTable("project_run_configs");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.TargetsJson).IsRequired();
        builder.Property(x => x.Notes).HasColumnType("text");
        builder.Property(x => x.Fingerprint).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Source)
            .HasConversion<string>()
            .HasMaxLength(20)
            .HasDefaultValue(RunConfigSource.Ai);

        builder.HasIndex(x => x.OrgId);
        // One config per project — the detector upserts, and a second row would make "which
        // commands does Run use" a coin flip.
        builder.HasIndex(x => x.ProjectId).IsUnique();

        builder.HasOne(x => x.Project)
            .WithOne()
            .HasForeignKey<ProjectRunConfig>(x => x.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
