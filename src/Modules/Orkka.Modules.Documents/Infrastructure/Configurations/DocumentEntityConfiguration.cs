namespace Orkka.Modules.Documents.Infrastructure.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orkka.Modules.Documents.Contracts;
using Orkka.Modules.Documents.Domain;

public sealed class DocumentEntityConfiguration : IEntityTypeConfiguration<DocumentEntity>
{
    public void Configure(EntityTypeBuilder<DocumentEntity> builder)
    {
        builder.ToTable("documents");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Title).HasMaxLength(DocumentRules.MaxTitleLength).IsRequired();
        builder.Property(x => x.Path).HasMaxLength(DocumentRules.MaxPathLength).IsRequired();
        builder.Property(x => x.Content).IsRequired();
        builder.Property(x => x.Source).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.CreatedByUserId).HasMaxLength(200);

        builder.Property(x => x.FileBlobName).HasMaxLength(500);
        builder.Property(x => x.FileContentType).HasMaxLength(100);
        builder.Property(x => x.FileName).HasMaxLength(500);
        builder.Ignore(x => x.IsFile);

        builder.HasMany(x => x.Revisions)
            .WithOne(r => r.Document)
            .HasForeignKey(r => r.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.OrgId, x.ProjectId });
        builder.HasIndex(x => x.SourceMessageId);
        builder.HasIndex(x => x.SourceTaskId);
    }
}
