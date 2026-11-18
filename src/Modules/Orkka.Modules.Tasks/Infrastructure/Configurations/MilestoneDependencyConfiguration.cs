namespace Orkka.Modules.Tasks.Infrastructure.Configurations;

using Orkka.Modules.Tasks.Domain;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class MilestoneDependencyConfiguration : IEntityTypeConfiguration<MilestoneDependency>
{
    public void Configure(EntityTypeBuilder<MilestoneDependency> builder)
    {
        builder.ToTable("milestone_dependencies");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Reason).HasMaxLength(1000);
        builder.Property(x => x.Source).HasConversion<string>().HasMaxLength(20);

        builder.HasIndex(x => x.OrgId);
        builder.HasIndex(x => new { x.MilestoneId, x.DependsOnMilestoneId }).IsUnique();
        builder.HasIndex(x => x.DependsOnMilestoneId);

        // Cascade on both legs, unlike milestones-to-tasks: an edge is a statement ABOUT two
        // milestones and means nothing once either is gone, whereas a task carries work that
        // must survive its milestone being deleted.
        //
        // NoAction on the second leg, not Cascade: two cascade paths into the same table give
        // "may cause cycles or multiple cascade paths" on SQL Server and are a foot-gun
        // everywhere else. The dependent edge is removed with its owner; the other leg is
        // cleaned up by DeleteMilestone, which is already the only way a milestone goes away.
        builder.HasOne(x => x.Milestone)
            .WithMany(m => m.Dependencies)
            .HasForeignKey(x => x.MilestoneId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.DependsOnMilestone)
            .WithMany(m => m.Dependents)
            .HasForeignKey(x => x.DependsOnMilestoneId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
