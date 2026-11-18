namespace Orkka.Modules.Agents.Infrastructure.Configurations;

using Orkka.Modules.Agents.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class AgentAuditEventConfiguration : IEntityTypeConfiguration<AgentAuditEvent>
{
    public void Configure(EntityTypeBuilder<AgentAuditEvent> builder)
    {
        builder.ToTable("agent_audit_events");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.EventType).HasConversion<string>().HasMaxLength(60).IsRequired();
        builder.Property(x => x.Actor).HasMaxLength(120).IsRequired();
        builder.Property(x => x.ActorKind).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(x => x.ActorUserId).HasMaxLength(200);
        builder.Property(x => x.Tool).HasMaxLength(120);
        builder.Property(x => x.Summary).HasMaxLength(4000).IsRequired();
        // MetadataJson is plain text, stored verbatim: the stored text must equal what was
        // hashed. The column is a hashed record, not a query target.
        builder.Property(x => x.PreviousHash).HasMaxLength(64);
        builder.Property(x => x.EntryHash).HasMaxLength(64);

        // Query patterns: per-org timeline, per-task/project drill-down, per-actor filter,
        // and the chain walk (org + created).
        builder.HasIndex(x => new { x.OrgId, x.CreatedAt });
        builder.HasIndex(x => x.ProjectId);
        builder.HasIndex(x => x.TaskId);
        builder.HasIndex(x => new { x.OrgId, x.ActorUserId });
        builder.HasIndex(x => new { x.OrgId, x.EventType });
    }
}
