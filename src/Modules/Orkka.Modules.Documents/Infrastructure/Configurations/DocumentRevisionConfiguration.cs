namespace Orkka.Modules.Documents.Infrastructure.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orkka.Modules.Documents.Contracts;
using Orkka.Modules.Documents.Domain;

public sealed class DocumentRevisionConfiguration : IEntityTypeConfiguration<DocumentRevision>
{
    public void Configure(EntityTypeBuilder<DocumentRevision> builder)
    {
        builder.ToTable("document_revisions");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Title).HasMaxLength(DocumentRules.MaxTitleLength).IsRequired();
        builder.Property(x => x.Path).HasMaxLength(DocumentRules.MaxPathLength).IsRequired();
        builder.Property(x => x.Content).IsRequired();
        builder.Property(x => x.ChangedByUserId).HasMaxLength(200);
        builder.Property(x => x.RequestedByUserId).HasMaxLength(200);

        builder.HasIndex(x => new { x.DocumentId, x.Version }).IsUnique();

        // "Which documents did this task touch" is a per-task lookup on the task page.
        builder.HasIndex(x => x.SourceTaskId);
    }
}
