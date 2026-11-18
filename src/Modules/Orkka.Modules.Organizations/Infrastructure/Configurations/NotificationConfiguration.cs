namespace Orkka.Modules.Organizations.Infrastructure.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orkka.Modules.Organizations.Domain;

public sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("notifications");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.UserId).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Type).HasConversion<string>().HasMaxLength(30);
        builder.Property(x => x.Title).HasMaxLength(500).IsRequired();
        builder.Property(x => x.Body).HasMaxLength(2000);
        builder.Property(x => x.ProjectSlug).HasMaxLength(200);
        builder.HasIndex(x => x.OrgId);
        // TaskId/ConversationId are cross-module references (tasks/agents schemas) — indexed, no FK.
        builder.HasIndex(x => x.TaskId);
        builder.HasIndex(x => x.ConversationId);
        builder.HasIndex(x => new { x.UserId, x.IsRead, x.CreatedAt });
    }
}
