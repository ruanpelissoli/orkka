namespace Orkka.Modules.Agents.Infrastructure.Configurations;

using Orkka.Modules.Agents.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class BrandKitConfiguration : IEntityTypeConfiguration<BrandKit>
{
    public void Configure(EntityTypeBuilder<BrandKit> builder)
    {
        builder.ToTable("brand_kits");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.PaletteJson).IsRequired();
        builder.Property(x => x.DisplayFont).HasMaxLength(200);
        builder.Property(x => x.BodyFont).HasMaxLength(200);
        builder.Property(x => x.Tone).HasMaxLength(500);
        builder.Property(x => x.Source).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.UpdatedByUserId).HasMaxLength(200);

        // One kit per project — a second would reintroduce exactly the "which palette is
        // real?" ambiguity this entity exists to remove.
        builder.HasIndex(x => x.ProjectId).IsUnique();
        builder.HasIndex(x => x.OrgId);
    }
}
