namespace Orkka.Modules.Agents.Infrastructure.Configurations;

using Orkka.Modules.Agents.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class DesignAssetConfiguration : IEntityTypeConfiguration<DesignAsset>
{
    public void Configure(EntityTypeBuilder<DesignAsset> builder)
    {
        builder.ToTable("design_assets");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.FileName).HasMaxLength(500).IsRequired();
        builder.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.MimeType).HasMaxLength(100).IsRequired();
        builder.Property(x => x.BlobContainer).HasMaxLength(100).IsRequired();
        builder.Property(x => x.BlobName).HasMaxLength(1000).IsRequired();
        builder.Property(x => x.Prompt).HasMaxLength(50000);
        builder.Property(x => x.SourceModel).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.Feedback).HasMaxLength(5000);
        builder.Property(x => x.FolderPath).HasMaxLength(500).IsRequired();
        builder.Property(x => x.RequestedByUserId).HasMaxLength(200);
        builder.Property(x => x.ReviewedByUserId).HasMaxLength(200);

        builder.HasIndex(x => new { x.OrgId, x.ProjectId, x.Status });
        builder.HasIndex(x => new { x.OrgId, x.MessageId });
        builder.HasIndex(x => new { x.OrgId, x.RootAssetId });

        // ProjectId/TaskId are cross-module references — no FK.

        // Optional: user-uploaded assets have no authoring agent. Cascade still removes an
        // agent's own assets when the agent row goes; uploads (AgentId null) are untouched.
        builder.HasOne(x => x.Agent)
            .WithMany(a => a.DesignAssets)
            .HasForeignKey(x => x.AgentId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
