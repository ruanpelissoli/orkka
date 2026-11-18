namespace Orkka.Modules.Organizations.Infrastructure.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orkka.Modules.Organizations.Domain;

public sealed class MemberPermissionConfiguration : IEntityTypeConfiguration<MemberPermission>
{
    public void Configure(EntityTypeBuilder<MemberPermission> builder)
    {
        builder.ToTable("member_permissions");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Permission).HasConversion<string>().HasMaxLength(50);
        builder.HasIndex(x => x.OrgId);

        // One grant per (member, permission, project). SQLite treats NULLs as distinct in a
        // unique index, so the org-wide grant (ProjectId = null) gets its own partial index —
        // without it a duplicate org-wide grant would slip through.
        builder.HasIndex(x => new { x.MemberId, x.Permission, x.ProjectId }, "IX_member_permissions_MemberId_Permission_ProjectId")
            .IsUnique()
            .HasFilter("\"ProjectId\" IS NOT NULL");
        builder.HasIndex(x => new { x.MemberId, x.Permission }, "IX_member_permissions_MemberId_Permission_OrgWide")
            .IsUnique()
            .HasFilter("\"ProjectId\" IS NULL");

        builder.HasOne(x => x.Member)
            .WithMany(m => m.Permissions)
            .HasForeignKey(x => x.MemberId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
