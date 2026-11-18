namespace Orkka.Modules.Agents.Infrastructure.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Domain;

public sealed class AiModelConfiguration : IEntityTypeConfiguration<AiModel>
{
    public void Configure(EntityTypeBuilder<AiModel> builder)
    {
        builder.ToTable("ai_models");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ModelId).HasMaxLength(100).IsRequired();
        builder.Property(x => x.DisplayName).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Provider).HasConversion<string>().HasMaxLength(50);
        builder.Property(x => x.Kind).HasConversion<string>().HasMaxLength(50);
        builder.Property(x => x.Description).HasMaxLength(500);
        builder.Property(x => x.ImageProviderKey).HasMaxLength(50);
        // 4 decimals, not the 2 used for money elsewhere: rates are dollars per 1M tokens and
        // the cheapest models are already sub-cent (gpt-4o-mini input is $0.15).
        builder.Property(x => x.InputRatePer1M).HasPrecision(12, 4);
        builder.Property(x => x.OutputRatePer1M).HasPrecision(12, 4);
        builder.Property(x => x.Note).HasMaxLength(1000);

        // Vendor-sync provenance. String enums with an explicit default so rows that predate
        // the sync read as Seed-owned (which is what they are), never as "".
        builder.Property(x => x.RateSource).HasConversion<string>().HasMaxLength(20)
            .HasDefaultValue(AiModelRateSource.Seed);
        builder.Property(x => x.Origin).HasConversion<string>().HasMaxLength(20)
            .HasDefaultValue(AiModelOrigin.Seed);
        builder.Property(x => x.VendorInputRatePer1M).HasPrecision(12, 4);
        builder.Property(x => x.VendorOutputRatePer1M).HasPrecision(12, 4);
        builder.Property(x => x.CacheReadRatePer1M).HasPrecision(12, 4);
        builder.Property(x => x.CacheWriteRatePer1M).HasPrecision(12, 4);
        builder.Property(x => x.VendorStatus).HasMaxLength(50);

        builder.HasIndex(x => x.ModelId).IsUnique();
        builder.HasIndex(x => new { x.Kind, x.IsEnabled });
    }
}
