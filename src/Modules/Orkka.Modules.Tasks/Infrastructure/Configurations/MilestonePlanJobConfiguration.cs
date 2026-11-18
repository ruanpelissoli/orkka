namespace Orkka.Modules.Tasks.Infrastructure.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;

public sealed class MilestonePlanJobConfiguration : IEntityTypeConfiguration<MilestonePlanJob>
{
    public void Configure(EntityTypeBuilder<MilestonePlanJob> builder)
    {
        builder.ToTable("milestone_plan_jobs");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .HasDefaultValue(MilestonePlanStatus.Pending);

        builder.Property(x => x.Goal).HasColumnType("text").IsRequired();
        builder.Property(x => x.RequesterUserId).HasMaxLength(200).IsRequired();
        builder.Property(x => x.RequesterLocale).HasMaxLength(20);
        builder.Property(x => x.CurrentStep).HasMaxLength(40);
        builder.Property(x => x.FailureCode).HasMaxLength(20);
        builder.Property(x => x.ErrorMessage).HasColumnType("text");

        // Plain text: the proposal is round-tripped verbatim to the client and never queried.
        builder.Property(x => x.ResultJson).HasColumnType("text");

        // The sweep's claim query: "any job anywhere still waiting". Filtered so the index
        // stays the size of the queue rather than of every plan ever run — finished rows are
        // the overwhelming majority and are only ever read by primary key.
        builder.HasIndex(x => x.Status)
            .HasFilter("\"Status\" IN ('Pending', 'Running')");

        // Reattaching a refreshed client: "my newest job on this project".
        builder.HasIndex(x => new { x.OrgId, x.ProjectId, x.RequesterUserId, x.CreatedAt });
    }
}
