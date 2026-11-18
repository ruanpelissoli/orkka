namespace Orkka.Modules.Tasks.Infrastructure.Configurations;

using Orkka.Modules.Tasks.Domain;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class MilestoneConfiguration : IEntityTypeConfiguration<Milestone>
{
    public void Configure(EntityTypeBuilder<Milestone> builder)
    {
        builder.ToTable("milestones");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Title).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Outcome).HasMaxLength(2000);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.CreatedByUserId).HasMaxLength(200);
        builder.Property(x => x.BranchName).HasMaxLength(200);
        builder.Property(x => x.ShipState).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.IntegrationMode).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.ShipFailureReason).HasMaxLength(2000);

        builder.HasIndex(x => new { x.OrgId, x.ProjectId, x.Status });
        builder.HasIndex(x => new { x.ProjectId, x.Position });

        // The ship sweep's claim query: "any milestone anywhere waiting to be merged". Filtered
        // so the index stays the size of the work queue rather than the milestone table — the
        // overwhelming majority of rows sit in NotReady/Shipped forever.
        builder.HasIndex(x => x.ShipState)
            .HasFilter("\"ShipState\" IN ('Pending', 'Shipping')");

        // Detach, never cascade. Deleting a milestone is a planning decision — it must not
        // destroy the work underneath it, which may already be merged.
        builder.HasMany(x => x.Tasks)
            .WithOne(t => t.Milestone!)
            .HasForeignKey(t => t.MilestoneId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
