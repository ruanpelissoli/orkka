namespace Orkka.Modules.Tasks.Infrastructure.Configurations;

using Orkka.Modules.Tasks.Domain;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class AutopilotSessionConfiguration : IEntityTypeConfiguration<AutopilotSession>
{
    public void Configure(EntityTypeBuilder<AutopilotSession> builder)
    {
        builder.ToTable("autopilot_sessions");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.StartedByUserId).HasMaxLength(200).IsRequired();
        builder.Property(x => x.StopReason).HasConversion<string>().HasMaxLength(30);
        builder.Property(x => x.CapUsd).HasPrecision(18, 2);
        builder.Property(x => x.SpentUsd).HasPrecision(18, 2);

        // The sweep's hot query is "the open session for this project", and the toggle's
        // invariant is that there is at most one. A filtered unique index enforces both —
        // the same trick the agent roster uses for singleton specialists. Column names are
        // PascalCase in this database (only table names are snake_case), hence the quoting.
        builder.HasIndex(x => new { x.OrgId, x.ProjectId })
            .IsUnique()
            .HasFilter("\"StopReason\" = 'Running'");

        // Session history for a project, newest first.
        builder.HasIndex(x => new { x.ProjectId, x.StartedAt });
    }
}
