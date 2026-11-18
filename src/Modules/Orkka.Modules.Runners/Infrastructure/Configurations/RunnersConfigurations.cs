namespace Orkka.Modules.Runners.Infrastructure.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orkka.Modules.Runners.Domain;

public sealed class RunnerTokenConfiguration : IEntityTypeConfiguration<RunnerToken>
{
    public void Configure(EntityTypeBuilder<RunnerToken> builder)
    {
        builder.Property(t => t.UserId).HasMaxLength(128);
        builder.Property(t => t.Name).HasMaxLength(100);
        builder.Property(t => t.TokenHash).HasMaxLength(64);
        builder.Property(t => t.LastMachineName).HasMaxLength(256);

        // The hub looks a presented token up by hash — one index serves auth.
        builder.HasIndex(t => t.TokenHash).IsUnique();
        builder.HasIndex(t => new { t.OrgId, t.UserId });
    }
}

public sealed class RunnerPresenceConfiguration : IEntityTypeConfiguration<RunnerPresence>
{
    public void Configure(EntityTypeBuilder<RunnerPresence> builder)
    {
        builder.Property(p => p.UserId).HasMaxLength(128);
        builder.Property(p => p.WorkerInstance).HasMaxLength(256);
        builder.Property(p => p.ConnectionId).HasMaxLength(128);
        builder.Property(p => p.MachineName).HasMaxLength(256);
        builder.Property(p => p.DaemonVersion).HasMaxLength(64);

        // One live runner per user — a reconnect upserts the same row.
        builder.HasIndex(p => new { p.OrgId, p.UserId }).IsUnique();
    }
}

public sealed class RunnerProviderSnapshotConfiguration : IEntityTypeConfiguration<RunnerProviderSnapshot>
{
    public void Configure(EntityTypeBuilder<RunnerProviderSnapshot> builder)
    {
        builder.Property(p => p.UserId).HasMaxLength(128);
        builder.Property(p => p.MachineName).HasMaxLength(256);
        builder.Property(p => p.DaemonVersion).HasMaxLength(64);

        // One memory per user — every Hello overwrites it.
        builder.HasIndex(p => new { p.OrgId, p.UserId }).IsUnique();
    }
}
