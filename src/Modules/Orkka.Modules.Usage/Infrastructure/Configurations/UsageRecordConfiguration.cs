namespace Orkka.Modules.Usage.Infrastructure.Configurations;

using Orkka.Modules.Usage.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class UsageRecordConfiguration : IEntityTypeConfiguration<UsageRecord>
{
    public void Configure(EntityTypeBuilder<UsageRecord> builder)
    {
        builder.ToTable("usage_records");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Model).HasMaxLength(100).IsRequired();
        builder.Property(x => x.UserId).HasMaxLength(128);
        builder.Property(x => x.CostTotal).HasPrecision(18, 8);
        builder.Property(x => x.VendorReportedCostUsd).HasPrecision(18, 8);

        builder.HasIndex(x => new { x.OrgId, x.CreatedAt }).IsDescending(false, true);
        builder.HasIndex(x => new { x.ProjectId, x.CreatedAt }).IsDescending(false, true);
        builder.HasIndex(x => x.AgentId);
        builder.HasIndex(x => x.TaskId);
    }
}
