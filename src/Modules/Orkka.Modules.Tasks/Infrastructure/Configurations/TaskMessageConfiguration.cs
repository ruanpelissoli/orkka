namespace Orkka.Modules.Tasks.Infrastructure.Configurations;

using Orkka.Modules.Tasks.Domain;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class TaskMessageConfiguration : IEntityTypeConfiguration<TaskMessage>
{
    public void Configure(EntityTypeBuilder<TaskMessage> builder)
    {
        builder.ToTable("task_messages");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Role).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Content).IsRequired();
        builder.Property(x => x.MessageType).HasConversion<string>().HasMaxLength(30);
        builder.Property(x => x.SenderId).HasMaxLength(200);
        builder.HasIndex(x => x.OrgId);
        builder.HasIndex(x => new { x.TaskId, x.CreatedAt });

        builder.HasOne(x => x.Task)
            .WithMany(t => t.Messages)
            .HasForeignKey(x => x.TaskId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
