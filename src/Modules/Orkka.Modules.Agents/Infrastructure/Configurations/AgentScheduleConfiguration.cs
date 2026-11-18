namespace Orkka.Modules.Agents.Infrastructure.Configurations;

using Orkka.Modules.Agents.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class AgentScheduleConfiguration : IEntityTypeConfiguration<AgentSchedule>
{
    public void Configure(EntityTypeBuilder<AgentSchedule> builder)
    {
        builder.ToTable("agent_schedules");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(120).IsRequired();
        builder.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.CronExpression).HasMaxLength(120);
        builder.Property(x => x.TimeZoneId).HasMaxLength(80);
        builder.Property(x => x.LastRunStatus).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.Message).HasMaxLength(4000);
        builder.Property(x => x.AutopilotCapUsd).HasPrecision(12, 2);
        builder.Property(x => x.CreatedByUserId).HasMaxLength(120).IsRequired();
        builder.Property(x => x.RequesterLocale).HasMaxLength(16);
        builder.Property(x => x.LeaseOwner).HasMaxLength(120);

        builder.HasIndex(x => new { x.OrgId, x.ProjectId });
        // The sweep: due rows only.
        builder.HasIndex(x => x.NextRunAt).HasFilter("\"IsEnabled\" = TRUE AND \"NextRunAt\" IS NOT NULL");
        // Reminder quota per agent.
        builder.HasIndex(x => new { x.AgentId, x.Kind });
    }
}

public sealed class AgentScheduleRunConfiguration : IEntityTypeConfiguration<AgentScheduleRun>
{
    public void Configure(EntityTypeBuilder<AgentScheduleRun> builder)
    {
        builder.ToTable("agent_schedule_runs");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.Summary).HasMaxLength(2000);
        builder.Property(x => x.Error).HasMaxLength(2000);
        builder.Property(x => x.Trigger).HasMaxLength(20);

        builder.HasOne(x => x.Schedule)
            .WithMany()
            .HasForeignKey(x => x.ScheduleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.ScheduleId, x.StartedAt });
        builder.HasIndex(x => new { x.OrgId, x.ProjectId, x.StartedAt });
    }
}
