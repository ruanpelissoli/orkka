namespace Orkka.Modules.Tasks.Infrastructure.Configurations;

using Orkka.Modules.Tasks.Domain;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class TaskExecutionConfiguration : IEntityTypeConfiguration<TaskExecution>
{
    public void Configure(EntityTypeBuilder<TaskExecution> builder)
    {
        builder.ToTable("task_executions");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Lane).HasConversion<string>().HasMaxLength(30);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.SessionId).HasMaxLength(100);
        builder.HasIndex(x => x.OrgId);
        builder.HasIndex(x => new { x.OrgId, x.Status });
        builder.HasIndex(x => new { x.TaskId, x.StartedAt });

        builder.HasOne(x => x.Task)
            .WithMany(t => t.Executions)
            .HasForeignKey(x => x.TaskId)
            .OnDelete(DeleteBehavior.Cascade);

        // AgentId is a cross-module reference (Agents module) since P8 — no FK. The column
        // keeps its historical name from the pre-split shadow FK.
        builder.Property(x => x.AgentId).HasColumnName("ProjectAgentId");
    }
}
