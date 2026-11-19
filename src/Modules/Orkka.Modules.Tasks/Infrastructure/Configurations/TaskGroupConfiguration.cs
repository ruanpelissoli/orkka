namespace Orkka.Modules.Tasks.Infrastructure.Configurations;

using Orkka.Modules.Tasks.Domain;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class TaskGroupConfiguration : IEntityTypeConfiguration<TaskGroup>
{
    public void Configure(EntityTypeBuilder<TaskGroup> builder)
    {
        builder.ToTable("task_groups");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Title).HasMaxLength(500).IsRequired();
        builder.Property(x => x.Request).HasMaxLength(10000);
        // No length cap. A contract covering several endpoints with their request and response
        // shapes runs long, and truncating the one artifact both halves code against would
        // reintroduce exactly the invented-endpoint failure it exists to prevent. The prompt
        // that writes it is what keeps it proportionate.
        builder.Property(x => x.Contract);
        builder.Property(x => x.ContractSource).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.MergePolicy).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.CreatedByUserId).HasMaxLength(200);
        // A number, not a string: CrossRepo = 0 is the default every existing row already has.
        builder.Property(x => x.Kind).HasDefaultValue(Orkka.Modules.Tasks.Contracts.TaskGroupKind.CrossRepo);
        builder.Property(x => x.BranchName).HasMaxLength(200);
        builder.Property(x => x.ExternalKey).HasMaxLength(160);
        builder.Property(x => x.ExternalUrl).HasMaxLength(1000);

        builder.HasMany(x => x.Branches)
            .WithOne(b => b.Group)
            .HasForeignKey(b => b.GroupId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.OrgId, x.ProjectId, x.Status });

        // The merge gate sweep runs cross-org every poll cycle, so the OrgId-prefixed index
        // above never applies to it. Partial, because settled groups are the majority and none
        // of them can ever be acted on again.
        builder.HasIndex(x => x.Status)
            .HasFilter("\"Status\" IN ('Active', 'AwaitingMerge')");

        // Detach, never cascade — same reasoning as milestones. Deleting a group is a planning
        // decision; the tasks under it may already be merged.
        builder.HasMany(x => x.Tasks)
            .WithOne(t => t.Group!)
            .HasForeignKey(t => t.GroupId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public sealed class GroupBranchConfiguration : IEntityTypeConfiguration<GroupBranch>
{
    public void Configure(EntityTypeBuilder<GroupBranch> builder)
    {
        builder.ToTable("group_branches");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.BranchName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.ForgePrUrl).HasMaxLength(1000);
        builder.Property(x => x.ForgeState).HasMaxLength(20);
        builder.HasIndex(x => new { x.GroupId, x.RepoId }).IsUnique();
        builder.HasIndex(x => x.OrgId);
    }
}
