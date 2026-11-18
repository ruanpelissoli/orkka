namespace Orkka.Modules.Runners.PublicApi;

using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Runners.Contracts;
using Orkka.Modules.Runners.Infrastructure;

/// <summary>DB-backed <see cref="IRunnerFleet"/> — presence rows are written by the hub
/// in whichever Worker replica owns the connection, so reads work from any process.</summary>
public sealed class RunnerFleet(RunnersDbContext db) : IRunnerFleet
{
    public async Task<RunnerPresenceSnapshot?> GetPresenceAsync(
        Ulid orgId, string userId, CancellationToken ct = default)
    {
        var row = await db.RunnerPresences.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(p => p.OrgId == orgId && p.UserId == userId, ct);
        return row is null ? null : ToSnapshot(row);
    }

    public async Task<IReadOnlyList<RunnerPresenceSnapshot>> ListPresenceAsync(
        Ulid orgId, CancellationToken ct = default)
    {
        var rows = await db.RunnerPresences.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.OrgId == orgId)
            .OrderBy(p => p.UserId)
            .ToListAsync(ct);
        return rows.Select(ToSnapshot).ToList();
    }

    public async Task<RunnerProviderCoverage?> GetProviderCoverageAsync(
        Ulid orgId, string userId, CancellationToken ct = default)
    {
        // The live row wins while fresh — it is what the hub is routing on right now.
        var presence = await db.RunnerPresences.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(p => p.OrgId == orgId && p.UserId == userId, ct);
        if (presence is not null
            && DateTimeOffset.UtcNow - presence.LastSeenAt < RunnerPresenceRules.OnlineWindow)
        {
            return new RunnerProviderCoverage(
                presence.MachineName, presence.LastSeenAt, Online: true,
                Features.Presence.ProviderJson.ParseStatuses(presence.ProvidersJson));
        }

        var snapshot = await db.RunnerProviderSnapshots.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(s => s.OrgId == orgId && s.UserId == userId, ct);
        return snapshot is null
            ? null
            : new RunnerProviderCoverage(
                snapshot.MachineName, snapshot.CapturedAt, Online: false,
                Features.Presence.ProviderJson.ParseStatuses(snapshot.ProvidersJson));
    }

    private static RunnerPresenceSnapshot ToSnapshot(Domain.RunnerPresence p) => new(
        p.OrgId,
        p.UserId,
        Online: DateTimeOffset.UtcNow - p.LastSeenAt < RunnerPresenceRules.OnlineWindow,
        p.MachineName,
        p.DaemonVersion,
        p.WorkerInstance,
        p.ConnectedAt,
        p.LastSeenAt,
        p.ProvidersJson,
        p.CapabilitiesJson);
}
