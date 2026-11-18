namespace Orkka.Modules.Organizations.Infrastructure.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orkka.Modules.Organizations.Domain;

public sealed class OrgMemberConfiguration : IEntityTypeConfiguration<OrgMember>
{
    public void Configure(EntityTypeBuilder<OrgMember> builder)
    {
        builder.ToTable("org_members");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.UserId).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Role).HasConversion<string>().HasMaxLength(20);
        builder.HasIndex(x => x.OrgId);
        builder.HasIndex(x => new { x.OrgId, x.UserId }).IsUnique();
        // Resolution reads every membership of one user — one per workspace (job).
        builder.HasIndex(x => x.UserId);

        builder.HasOne(x => x.Organization)
            .WithMany(o => o.Members)
            .HasForeignKey(x => x.OrgId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
