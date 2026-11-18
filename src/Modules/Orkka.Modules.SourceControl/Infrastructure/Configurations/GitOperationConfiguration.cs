namespace Orkka.Modules.SourceControl.Infrastructure.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orkka.Modules.SourceControl.Domain;

public sealed class GitOperationConfiguration : IEntityTypeConfiguration<GitOperation>
{
    public void Configure(EntityTypeBuilder<GitOperation> builder)
    {
        builder.ToTable("git_operations");
        builder.HasKey(x => x.Id);

        // Enums by name: the ledger is read by operators and by dashboards, and a renumbered
        // enum must never re-label history.
        builder.Property(x => x.Kind).HasConversion<string>().HasMaxLength(40).IsRequired();
        builder.Property(x => x.Code).HasConversion<string>().HasMaxLength(40).IsRequired();
        builder.Property(x => x.Topology).HasMaxLength(40).IsRequired();
        builder.Property(x => x.Error).HasMaxLength(2000);
        builder.Property(x => x.LastCommand).HasMaxLength(1000);

        // The dashboard's shape: a time window, grouped by kind and topology, failures first.
        builder.HasIndex(x => x.StartedAt);
        builder.HasIndex(x => new { x.Kind, x.Topology, x.StartedAt });
        builder.HasIndex(x => new { x.TaskId, x.StartedAt });
    }
}
