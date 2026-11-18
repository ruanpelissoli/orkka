namespace Orkka.SharedKernel.Persistence;

using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Orkka.SharedKernel.Domain;
using Orkka.SharedKernel.Security;

/// <summary>
/// Base DbContext for every module. Provides: ULID↔GUID and DateTimeOffset↔UTC-ticks
/// conventions, the multi-tenant global query filter, and audit-field stamping. Module contexts
/// only declare their DbSets, module name, and (optionally) an <see cref="OnBeforeSave"/> override.
/// Every module shares one SQLite file; table names are unique across modules.
/// </summary>
public abstract class ModuleDbContext : DbContext
{
    // EF Core evaluates member access on the DbContext per-query (not cached like constants).
    // These fields MUST be used in query filter expressions for correct multi-tenant isolation.
    // When _hasTenant is false (e.g. Worker service, Migrator), the filter is bypassed so all
    // orgs are visible.
    private readonly bool _hasTenant;
    private readonly Ulid _tenantId;
    private readonly ICurrentUser? _currentUser;

    protected ModuleDbContext(DbContextOptions options, ICurrentUser? currentUser = null) : base(options)
    {
        _currentUser = currentUser;
        _hasTenant = currentUser is { IsAuthenticated: true };
        _tenantId = _hasTenant ? currentUser!.OrgId : Ulid.Empty;
    }

    /// <summary>The module's name (e.g. "tasks"): names its migrations history table.</summary>
    public abstract string ModuleName { get; }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<Ulid>().HaveConversion<UlidToGuidConverter>();
        configurationBuilder.Properties<Ulid?>().HaveConversion<NullableUlidToGuidConverter>();
        // UTC ticks: ORDER BY, comparisons and MIN/MAX translate to integer operations.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcTicksConverter>();
        configurationBuilder.Properties<DateTimeOffset?>().HaveConversion<UtcTicksConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(GetType().Assembly);

        // Apply tenant query filter to all ITenantEntity types.
        // Uses a generic method so EF Core receives a proper lambda with closure,
        // which it correctly re-evaluates per-query (reading _hasTenant/_tenantId
        // from the current DbContext instance, not the one that built the model).
        var applyFilterMethod = typeof(ModuleDbContext)
            .GetMethod(nameof(ApplyTenantFilter), BindingFlags.NonPublic | BindingFlags.Instance)!;

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (typeof(ITenantEntity).IsAssignableFrom(entityType.ClrType))
            {
                applyFilterMethod.MakeGenericMethod(entityType.ClrType)
                    .Invoke(this, [modelBuilder]);
            }
        }
    }

    private void ApplyTenantFilter<TEntity>(ModelBuilder modelBuilder) where TEntity : class, ITenantEntity
    {
        modelBuilder.Entity<TEntity>().HasQueryFilter(e => !_hasTenant || e.OrgId == _tenantId);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        SetAuditFields();
        OnBeforeSave();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        SetAuditFields();
        OnBeforeSave();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    /// <summary>Hook invoked before every save, after audit stamping (e.g. append-only enforcement).</summary>
    protected virtual void OnBeforeSave()
    {
    }

    private void SetAuditFields()
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var entry in ChangeTracker.Entries<IAuditableEntity>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;

                if (entry.Entity is BaseEntity baseEntity && baseEntity.Id == Ulid.Empty)
                    baseEntity.Id = Ulid.NewUlid();
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
            }
        }

        if (_currentUser is { IsAuthenticated: true })
        {
            foreach (var entry in ChangeTracker.Entries<ITenantEntity>())
            {
                if (entry.State == EntityState.Added && entry.Entity.OrgId == Ulid.Empty)
                    entry.Entity.OrgId = _currentUser.OrgId;
            }
        }
    }
}
