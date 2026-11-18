namespace Orkka.Modules.Tasks.Infrastructure.Configurations;

using Orkka.Modules.Tasks.Domain;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class TaskDependencyConfiguration : IEntityTypeConfiguration<TaskDependency>
{
    public void Configure(EntityTypeBuilder<TaskDependency> builder)
    {
        builder.ToTable("task_dependencies");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Reason).HasMaxLength(1000);
        builder.Property(x => x.Source).HasConversion<string>().HasMaxLength(20);

        builder.HasIndex(x => x.OrgId);
        builder.HasIndex(x => new { x.TaskId, x.DependsOnTaskId }).IsUnique();
        builder.HasIndex(x => x.DependsOnTaskId);

        builder.HasOne(x => x.Task)
            .WithMany(t => t.Dependencies)
            .HasForeignKey(x => x.TaskId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.DependsOnTask)
            .WithMany(t => t.Dependents)
            .HasForeignKey(x => x.DependsOnTaskId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
