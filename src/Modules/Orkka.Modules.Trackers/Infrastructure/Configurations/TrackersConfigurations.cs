namespace Orkka.Modules.Trackers.Infrastructure.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orkka.Modules.Trackers.Domain;

public sealed class TrackerConnectionConfiguration : IEntityTypeConfiguration<TrackerConnection>
{
    public void Configure(EntityTypeBuilder<TrackerConnection> builder)
    {
        builder.ToTable("connections");
        builder.Property(c => c.UserId).HasMaxLength(128);
        builder.Property(c => c.ConnectionKey).HasMaxLength(100);
        builder.Property(c => c.Kind).HasMaxLength(20);
        builder.Property(c => c.AuthMode).HasMaxLength(20);
        builder.Property(c => c.SiteUrl).HasMaxLength(512);
        builder.Property(c => c.AccountId).HasMaxLength(256);
        builder.Property(c => c.DisplayName).HasMaxLength(256);
        builder.Property(c => c.HealthDetail).HasMaxLength(1000);
        builder.Property(c => c.MachineName).HasMaxLength(256);

        // The runner names a connection by its key; one user's machines share the namespace,
        // so the same key reported by a second machine is the same connection, last seen there.
        builder.HasIndex(c => new { c.OrgId, c.UserId, c.ConnectionKey }).IsUnique();
    }
}

public sealed class ProjectTrackerConfigConfiguration : IEntityTypeConfiguration<ProjectTrackerConfig>
{
    public void Configure(EntityTypeBuilder<ProjectTrackerConfig> builder)
    {
        builder.ToTable("project_configs");
        builder.Property(c => c.OwnerUserId).HasMaxLength(128);
        builder.Property(c => c.Scope).HasMaxLength(256);
        builder.Property(c => c.ExtraClause).HasMaxLength(2000);
        builder.Property(c => c.BoardId).HasMaxLength(128);
        builder.Property(c => c.BoardName).HasMaxLength(256);
        builder.Property(c => c.StatusField).HasMaxLength(200);
        builder.Property(c => c.RepoFilterJson).HasDefaultValue("[]");
        builder.Property(c => c.BranchTemplate).HasMaxLength(200);
        builder.Property(c => c.LastPollError).HasMaxLength(2000);
        builder.Property(c => c.LeaseHolder).HasMaxLength(100);
        // Explicit defaults so a row written before these columns existed reads as an intake
        // source over all of the owner's To Do tickets (an EF string enum otherwise defaults to '').
        builder.Property(c => c.ScopeMode).HasConversion<string>().HasMaxLength(20)
            .HasDefaultValue(TrackerScopeMode.All);
        builder.Property(c => c.IntakeStatusesJson)
            .HasDefaultValue("[\"To Do\"]");
        builder.Property(c => c.StoryMode).HasDefaultValue(1);

        builder.HasIndex(c => c.ProjectId).IsUnique();
        builder.HasIndex(c => c.TrackerConnectionId);

        builder.HasMany(c => c.Epics)
            .WithOne()
            .HasForeignKey(e => e.ConfigId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class TrackerImportConfiguration : IEntityTypeConfiguration<TrackerImport>
{
    public void Configure(EntityTypeBuilder<TrackerImport> builder)
    {
        builder.ToTable("tracker_imports");
        builder.Property(i => i.TicketKey).HasMaxLength(160);
        builder.Property(i => i.SubTaskKeysJson).HasMaxLength(20000);
        builder.Property(i => i.RequestedByUserId).HasMaxLength(200);
        builder.Property(i => i.Error).HasMaxLength(2000);
        builder.HasIndex(i => new { i.ConfigId, i.CompletedAt });
        builder.HasIndex(i => i.ProjectId);
    }
}

public sealed class TicketLinkConfiguration : IEntityTypeConfiguration<TicketLink>
{
    public void Configure(EntityTypeBuilder<TicketLink> builder)
    {
        builder.ToTable("ticket_links");
        builder.Property(l => l.Kind).HasMaxLength(20);
        builder.Property(l => l.SiteUrl).HasMaxLength(512);
        // GitHub keys are owner/repo#n (up to ~150 characters); its ids are GraphQL node ids.
        builder.Property(l => l.TicketKey).HasMaxLength(160);
        builder.Property(l => l.TicketId).HasMaxLength(128);
        builder.Property(l => l.TicketUrl).HasMaxLength(2048);
        builder.Property(l => l.EpicKey).HasMaxLength(160);
        builder.Property(l => l.Subpath).HasMaxLength(500);
        builder.Property(l => l.ContentHash).HasMaxLength(100);
        builder.Property(l => l.RemoteStatus).HasMaxLength(200);
        builder.Property(l => l.SyncedPhase).HasConversion<string>().HasMaxLength(20);
        builder.Property(l => l.PendingPhase).HasConversion<string>().HasMaxLength(20);
        builder.Property(l => l.LastTransitionError).HasMaxLength(1000);
        builder.Property(l => l.PrState).HasMaxLength(20);
        builder.Property(l => l.ParentKey).HasMaxLength(160);
        builder.Property(l => l.Role).HasConversion<string>().HasMaxLength(20)
            .HasDefaultValue(TicketRole.Standalone);
        builder.Property(l => l.ParentTitle).HasMaxLength(500);
        builder.Property(l => l.Level).HasConversion<string>().HasMaxLength(20)
            .HasDefaultValue(Orkka.LocalRunner.Contracts.TicketLevel.Unknown);
        builder.Property(l => l.BlockedByKeysJson).HasMaxLength(8000);
        builder.Property(l => l.ExcludedChildKeysJson).HasMaxLength(20000);
        builder.Property(l => l.ParentDescription).HasMaxLength(20000);

        builder.HasIndex(l => new { l.ConfigId, l.TicketKey }).IsUnique();
        // One ticket per task — except a Story's link, which points at its first member (that
        // member's own sub-task link is the one the task belongs to).
        builder.HasIndex(l => l.TaskId).IsUnique().HasFilter("\"Role\" <> 'Story'");
        builder.HasIndex(l => l.GroupId).HasFilter("\"GroupId\" IS NOT NULL");
    }
}

public sealed class TrackerEpicMappingConfiguration : IEntityTypeConfiguration<TrackerEpicMapping>
{
    public void Configure(EntityTypeBuilder<TrackerEpicMapping> builder)
    {
        builder.ToTable("epic_mappings");
        // epic_key holds the rule value: an issue key / work item id, or a component, label or
        // area path (which can be long).
        builder.Property(e => e.EpicKey).HasMaxLength(256);
        builder.Property(e => e.EpicTitle).HasMaxLength(500);
        builder.Property(e => e.Subpath).HasMaxLength(500);
        builder.Property(e => e.Kind).HasConversion<string>().HasMaxLength(20)
            .HasDefaultValue(TrackerRuleKind.Epic);

        builder.HasIndex(e => new { e.ConfigId, e.Kind, e.EpicKey }).IsUnique();
        builder.HasIndex(e => e.RepoId);
    }
}
