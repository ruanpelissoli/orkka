namespace Orkka.Modules.Tasks.Infrastructure.Configurations;

using Orkka.Modules.Tasks.Domain;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class TaskEntityConfiguration : IEntityTypeConfiguration<TaskEntity>
{
    public void Configure(EntityTypeBuilder<TaskEntity> builder)
    {
        builder.ToTable("tasks");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Title).HasMaxLength(500).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(10000);
        builder.Property(x => x.Priority).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.CurrentLane).HasConversion<string>().HasMaxLength(30);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(30);
        builder.Property(x => x.QuestionSourceLane).HasConversion<string>().HasMaxLength(30);
        builder.Property(x => x.CreatedByUserId).HasMaxLength(200);
        builder.Property(x => x.RequesterLocale).HasMaxLength(10);
        builder.Property(x => x.DocSyncStatus).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.RunnerUserId).HasMaxLength(128);
        builder.Property(x => x.Model).HasMaxLength(100);
        builder.Property(x => x.RecommendedModel).HasMaxLength(100);

        builder.Property(x => x.BranchName).HasMaxLength(200);
        builder.Property(x => x.BaseBranch).HasMaxLength(200);
        builder.Property(x => x.ReviewBaseSha).HasMaxLength(64);
        builder.Property(x => x.SpendApprovedUsd).HasPrecision(18, 4);
        builder.Property(x => x.PrUrl).HasMaxLength(2048);
        builder.Property(x => x.PublishedPrUrl).HasMaxLength(2048);
        builder.Property(x => x.MergeFailureReason).HasMaxLength(1000);
        builder.Property(x => x.MergeFailureKind).HasConversion<string>().HasMaxLength(30);
        builder.Property(x => x.SetupFailure).HasMaxLength(1000);
        builder.Property(x => x.MergeResolutionStrategy).HasMaxLength(20);
        builder.Property(x => x.MergeResolutionGuidance).HasMaxLength(2000);
        builder.Property(x => x.QueuedMergeStrategy).HasMaxLength(20);
        builder.Property(x => x.QueuedMergeGuidance).HasMaxLength(2000);
        builder.Property(x => x.LeaseOwner).HasMaxLength(200);
        builder.Property(x => x.ExternalRef).HasMaxLength(300);
        // owner/repo#n for a GitHub issue.
        builder.Property(x => x.ExternalKey).HasMaxLength(160);
        builder.Property(x => x.ExternalUrl).HasMaxLength(2048);
        builder.Property(x => x.ExternalEpic).HasMaxLength(600);
        builder.Property(x => x.ExternalBlockedBy).HasMaxLength(1000);
        builder.Property(x => x.WorkingSubpath).HasMaxLength(500);
        builder.Property(x => x.ExternalSyncError).HasMaxLength(500);
        builder.HasIndex(x => new { x.OrgId, x.ProjectId, x.ExternalRef }).IsUnique().HasFilter("\"ExternalRef\" IS NOT NULL");

        builder.HasIndex(x => x.OrgId);
        builder.HasIndex(x => x.RepoId);
        // Loading a group's members, and the merge gate's per-group roll-up. Partial: almost
        // every task is ungrouped, so the index stays proportional to the ones that aren't.
        builder.HasIndex(x => x.GroupId).HasFilter("\"GroupId\" IS NOT NULL");
        // Every milestone rollup and the backlog query (MilestoneId IS NULL) hit this.
        builder.HasIndex(x => new { x.OrgId, x.MilestoneId });
        builder.HasIndex(x => x.SourceMessageId);
        builder.HasIndex(x => new { x.OrgId, x.ProjectId, x.CurrentLane });
        builder.HasIndex(x => new { x.OrgId, x.ProjectId, x.IsArchived, x.CurrentLane });
        builder.HasIndex(x => new { x.OrgId, x.Status, x.Priority });
        builder.HasIndex(x => new { x.OrgId, x.QueuePosition });
        builder.HasIndex(x => new { x.ProjectId, x.CreatedAt });
        builder.HasIndex(x => new { x.ProjectId, x.Number }).IsUnique();

        // Worker sweep queries run cross-org every poll cycle, so the OrgId-prefixed
        // indexes above never apply to them.
        builder.HasIndex(x => new { x.Status, x.IsArchived });
        builder.HasIndex(x => new { x.CurrentLane, x.PrUrl });
        builder.HasIndex(x => new { x.CurrentLane, x.IsMerged });
        // Documentation sweep: nearly every task is already past it, so the partial index keeps
        // the poll proportional to the handful still awaiting a pass.
        // Completed excludes the overwhelming majority of rows; the three states left are the
        // only ones the sweep can act on (Running only once it looks abandoned).
        builder.HasIndex(x => new { x.CurrentLane, x.DocSyncStatus, x.CompletedAt })
            .HasFilter("\"DocSyncStatus\" <> 'Completed'");
        // Reset sweep: almost always empty — the partial index keeps it O(pending).
        builder.HasIndex(x => x.ResetRequestedAt).HasFilter("\"ResetRequestedAt\" IS NOT NULL");

    }
}
