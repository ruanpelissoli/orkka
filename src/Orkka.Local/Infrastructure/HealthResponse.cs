namespace Orkka.Local.Infrastructure;

using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

/// <summary>
/// Writes the per-check breakdown as JSON.
/// </summary>
/// <remarks>
/// The default writer emits only the aggregate status string. Since every dependency check
/// here reports Degraded rather than Unhealthy — so a dependency cannot fail
/// the endpoint — the aggregate alone would always read as healthy and the breakdown would be
/// invisible. This makes "the database is degraded but the host is up" observable.
///
/// Names and statuses only: the endpoint is anonymous, so exception detail stays out.
/// </remarks>
public static class HealthResponse
{
    public const string ServiceName = "orkka";

    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";

        return context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            // Lets `./orkka start` tell another Orkka on its port from some other program.
            service = ServiceName,
            status = report.Status.ToString(),
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                description = entry.Value.Description,
            }),
        }));
    }

    /// <summary>
    /// Degraded maps to 200. Anything else would let a dependency check fail the endpoint — see
    /// <see cref="DatabaseHealthCheck"/>.
    /// </summary>
    public static readonly Dictionary<HealthStatus, int> StatusCodes = new()
    {
        [HealthStatus.Healthy] = 200,
        [HealthStatus.Degraded] = 200,
        [HealthStatus.Unhealthy] = 503,
    };
}
