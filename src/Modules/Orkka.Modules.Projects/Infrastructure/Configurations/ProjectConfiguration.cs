namespace Orkka.Modules.Projects.Infrastructure.Configurations;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.SharedKernel.Security;
using Orkka.SharedKernel.Utilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using Orkka.Modules.Projects.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> builder)
    {
        builder.ToTable("projects");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Slug).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Key).HasMaxLength(5).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(2000);
        builder.Property(x => x.CreatedByUserId).HasMaxLength(200);
        builder.Property(x => x.BaseFolder).HasMaxLength(1024);
        builder.HasIndex(x => x.OrgId);
        builder.HasIndex(x => new { x.OrgId, x.Slug }).IsUnique();
        builder.HasIndex(x => new { x.OrgId, x.Key }).IsUnique();

    }
}
