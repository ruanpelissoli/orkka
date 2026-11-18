namespace Orkka.Modules.Organizations.Infrastructure.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orkka.Modules.Organizations.Domain;

public sealed class OrgApiKeyConfiguration : IEntityTypeConfiguration<OrgApiKey>
{
    public void Configure(EntityTypeBuilder<OrgApiKey> builder)
    {
        builder.ToTable("org_api_keys");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.UserId).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Prefix).HasMaxLength(20).IsRequired();
        builder.Property(x => x.KeyHash).HasMaxLength(64).IsRequired();
        builder.Ignore(x => x.IsActive);

        builder.HasIndex(x => x.KeyHash).IsUnique();
        builder.HasIndex(x => new { x.OrgId, x.MemberId });
    }
}
