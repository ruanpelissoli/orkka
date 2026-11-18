namespace Orkka.Modules.Agents.Infrastructure.Configurations;

using Orkka.Modules.Agents.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class ChatAttachmentConfiguration : IEntityTypeConfiguration<ChatAttachment>
{
    public void Configure(EntityTypeBuilder<ChatAttachment> builder)
    {
        builder.ToTable("chat_attachments");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.FileName).HasMaxLength(500).IsRequired();
        builder.Property(x => x.MimeType).HasMaxLength(100).IsRequired();
        builder.Property(x => x.BlobContainer).HasMaxLength(100).IsRequired();
        builder.Property(x => x.BlobName).HasMaxLength(1000).IsRequired();
        builder.Property(x => x.UploadedByUserId).HasMaxLength(200).IsRequired();

        builder.HasIndex(x => new { x.OrgId, x.ProjectId, x.CreatedAt });

        // ProjectId/ConversationId are plain references — no FK (conversation deletion
        // must not cascade away context files that may be re-linked elsewhere).
    }
}
