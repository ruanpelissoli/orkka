namespace Orkka.Modules.Tasks.Infrastructure.Configurations;

using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class TaskForecastConfiguration : IEntityTypeConfiguration<TaskForecast>
{
    public void Configure(EntityTypeBuilder<TaskForecast> builder)
    {
        builder.ToTable("task_forecasts");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Surface).HasConversion<string>().HasMaxLength(30);
        builder.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.CostP25).HasPrecision(10, 4);
        builder.Property(x => x.CostMedian).HasPrecision(10, 4);
        builder.Property(x => x.CostP75).HasPrecision(10, 4);
        builder.Property(x => x.ActualCost).HasPrecision(10, 4);

        builder.HasIndex(x => new { x.OrgId, x.TaskId });
        // The scoring sweep runs cross-org every cycle and is almost always empty, so it wants
        // a partial index rather than an org-prefixed one.
        builder.HasIndex(x => x.Outcome).HasFilter($"\"Outcome\" = '{nameof(ForecastOutcome.Pending)}'");
    }
}
