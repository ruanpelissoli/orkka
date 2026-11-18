namespace Orkka.Modules.Tasks.Infrastructure.Configurations;

using Orkka.Modules.Tasks.Domain;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class TaskCodeChangeSnapshotConfiguration : IEntityTypeConfiguration<TaskCodeChangeSnapshot>
{
    public void Configure(EntityTypeBuilder<TaskCodeChangeSnapshot> builder)
    {
        builder.ToTable("task_code_change_snapshots");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Lane).HasConversion<string>().HasMaxLength(30);
        builder.Property(x => x.BranchName).HasMaxLength(300).IsRequired();
        builder.Property(x => x.BaseBranch).HasMaxLength(300).IsRequired();
        builder.Property(x => x.DiffHash).HasMaxLength(64).IsRequired();

        builder.Property(x => x.IsLive).HasDefaultValue(false);
        builder.Property(x => x.Revision).HasDefaultValue(0L);

        builder.HasIndex(x => x.OrgId);
        builder.HasIndex(x => x.TaskId);
        // One live row per task; the filter keeps permanent rows out of the uniqueness.
        // Keyed on (TaskId, IsLive) so it is a distinct index from the plain TaskId one above.
        builder.HasIndex(x => new { x.TaskId, x.IsLive })
            .HasDatabaseName("ix_task_code_change_snapshots_task_live")
            .IsUnique()
            .HasFilter("\"IsLive\" = true");

        builder.HasOne(x => x.Task)
            .WithMany()
            .HasForeignKey(x => x.TaskId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
