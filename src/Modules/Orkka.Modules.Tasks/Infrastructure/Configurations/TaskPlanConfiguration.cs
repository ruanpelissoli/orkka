namespace Orkka.Modules.Tasks.Infrastructure.Configurations;

using Orkka.Modules.Tasks.Domain;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class TaskPlanConfiguration : IEntityTypeConfiguration<TaskPlan>
{
    public void Configure(EntityTypeBuilder<TaskPlan> builder)
    {
        builder.ToTable("task_plans");
        builder.HasKey(x => x.Id);
        builder.OwnsOne(x => x.Content, cb =>
        {
            cb.ToJson();
            cb.OwnsMany(c => c.FilesChanged);
        });
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.Feedback).HasMaxLength(5000);
        builder.HasIndex(x => x.OrgId);
        builder.HasIndex(x => new { x.TaskId, x.Version }).IsUnique();

        builder.HasOne(x => x.Task)
            .WithMany(t => t.Plans)
            .HasForeignKey(x => x.TaskId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
