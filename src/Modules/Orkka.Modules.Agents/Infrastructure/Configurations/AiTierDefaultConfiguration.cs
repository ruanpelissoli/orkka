namespace Orkka.Modules.Agents.Infrastructure.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orkka.Modules.Agents.Domain;

public sealed class AiTierDefaultConfiguration : IEntityTypeConfiguration<AiTierDefault>
{
    public void Configure(EntityTypeBuilder<AiTierDefault> builder)
    {
        builder.ToTable("ai_tier_defaults");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Provider).HasConversion<string>().HasMaxLength(50);
        builder.Property(x => x.Tier).HasConversion<string>().HasMaxLength(50);
        builder.Property(x => x.ModelId).HasMaxLength(100).IsRequired();
        builder.HasIndex(x => new { x.Provider, x.Tier }).IsUnique();
    }
}
