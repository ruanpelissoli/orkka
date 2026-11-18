namespace Orkka.Modules.Tasks.Infrastructure.Configurations;

using Orkka.Modules.Tasks.Domain;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class TaskApprovalRequestConfiguration : IEntityTypeConfiguration<TaskApprovalRequest>
{
    public void Configure(EntityTypeBuilder<TaskApprovalRequest> builder)
    {
        builder.ToTable("task_approval_requests");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.ToolUseId).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Tool).HasMaxLength(100).IsRequired();
        builder.Property(x => x.ApprovalKey).HasMaxLength(200).IsRequired();
        // Long enough for any real command line without truncating what the human is approving —
        // a shortened command is not the thing they agreed to run.
        builder.Property(x => x.Command).HasMaxLength(8000).IsRequired();

        // Stored as text: the status is read in logs and audit exports, and an integer there
        // means nothing to whoever is answering "who allowed this".
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);

        builder.HasIndex(x => x.OrgId);
        builder.HasIndex(x => x.TaskId);

        // One prompt per tool call. The runner retries its hub invoke on a dropped connection,
        // and without this a reconnect mid-decision would raise a second prompt for a command the
        // human has already ruled on.
        builder.HasIndex(x => new { x.TaskId, x.ToolUseId }).IsUnique();

        builder.HasOne<TaskEntity>()
            .WithMany()
            .HasForeignKey(x => x.TaskId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
