namespace Orkka.Local.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Orkka.Modules.Organizations.Infrastructure;

/// <summary>
/// Verifies the platform database (the SQLite file) actually opens, rather than only that the
/// process is up.
/// </summary>
/// <remarks>
/// Degraded, never Unhealthy: the launcher polls <c>/health</c> to know the host is up, and a
/// database blip must not read as a dead host. The breakdown is in the response body.
/// </remarks>
internal sealed class DatabaseHealthCheck(IServiceScopeFactory scopeFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrganizationsDbContext>();
            return await db.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy("Database reachable.")
                : HealthCheckResult.Degraded("Database unreachable.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Degraded("Database unreachable.", ex);
        }
    }
}
