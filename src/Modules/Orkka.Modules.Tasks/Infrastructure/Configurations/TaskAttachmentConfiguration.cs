namespace Orkka.Modules.Tasks.Infrastructure.Configurations;

using Orkka.Modules.Tasks.Domain;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class TaskAttachmentConfiguration : IEntityTypeConfiguration<TaskAttachment>
{
    public void Configure(EntityTypeBuilder<TaskAttachment> builder)
    {
        builder.ToTable("task_attachments");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.FileUrl).HasMaxLength(2048).IsRequired();
        builder.Property(x => x.FileType).HasMaxLength(100).IsRequired();
        builder.Property(x => x.FileName).HasMaxLength(500).IsRequired();
        builder.HasIndex(x => x.OrgId);
        builder.HasIndex(x => x.TaskId);

        builder.HasOne(x => x.Task)
            .WithMany(t => t.Attachments)
            .HasForeignKey(x => x.TaskId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
