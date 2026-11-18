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

public sealed class ProjectRepoConfiguration : IEntityTypeConfiguration<ProjectRepo>
{
    public void Configure(EntityTypeBuilder<ProjectRepo> builder)
    {
        builder.ToTable("project_repos");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.GitUrl).HasMaxLength(2048).IsRequired();
        builder.Property(x => x.DefaultBranch).HasMaxLength(100).HasDefaultValue("main");
        builder.Property(x => x.ForgeKind).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.CliAccount).HasMaxLength(200);
        builder.Ignore(x => x.Location);
        builder.Property(x => x.WorkspaceStatus).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.SetupCommand).HasMaxLength(2000);
        builder.Property(x => x.Stack).HasMaxLength(20);
        builder.Property(x => x.BuildCommand).HasMaxLength(2000);
        builder.Property(x => x.CheckCommand).HasMaxLength(2000);
        builder.Property(x => x.QaRunCommand).HasMaxLength(2000);
        builder.Property(x => x.FolderPath).HasMaxLength(500);
        builder.Property(x => x.Description).HasMaxLength(300);
        builder.HasIndex(x => x.OrgId);
        builder.HasIndex(x => x.ProjectId);

        builder.HasOne(x => x.Project)
            .WithMany(p => p.Repos)
            .HasForeignKey(x => x.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

    }
}
