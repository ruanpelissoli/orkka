namespace Orkka.Modules.Runners.Infrastructure.Services;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Runners.Domain;
using Orkka.Modules.Runners.Infrastructure;

/// <summary>
/// Presence row upkeep for the hosted runner hub. Runs on the Worker with no user
/// context — every query ignores the tenant filter and scopes by explicit ids.
/// </summary>
public sealed class RunnerPresenceWriter(RunnersDbContext db)
{
    // Kind as its name ("Setup", "Check"), which is what the Machine page shows.
    internal static readonly JsonSerializerOptions ApprovalJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>Upserts the (org, user) presence row on Hello — the 60s re-Hello makes
    /// this the heartbeat that keeps LastSeenAt fresh.</summary>
    public async Task UpsertAsync(
        Ulid orgId, string userId, Ulid tokenId, string workerInstance, string connectionId,
        RunnerHello hello, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var row = await db.RunnerPresences.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.OrgId == orgId && p.UserId == userId, ct);

        if (row is null)
        {
            row = new RunnerPresence
            {
                OrgId = orgId,
                UserId = userId,
                WorkerInstance = workerInstance,
                ConnectionId = connectionId,
                MachineName = hello.MachineName,
                ConnectedAt = now,
            };
            db.RunnerPresences.Add(row);
        }
        else if (row.ConnectionId != connectionId)
        {
            // New connection (reconnect or replacement) — reset the session start.
            row.ConnectionId = connectionId;
            row.WorkerInstance = workerInstance;
            row.ConnectedAt = now;
        }

        row.MachineName = hello.MachineName;
        row.DaemonVersion = hello.DaemonVersion;
        row.TokenId = tokenId;
        row.LastSeenAt = now;
        row.ProvidersJson = JsonSerializer.Serialize(hello.Providers);
        // Re-snapshotted every Hello: the user fixes a wrong push identity by running
        // `gh auth login`, and a cached answer would keep accusing the old account.
        row.GitIdentityJson = hello.GitIdentity is null
            ? null
            : JsonSerializer.Serialize(hello.GitIdentity);
        row.CapabilitiesJson = hello.Capabilities is null
            ? null
            : JsonSerializer.Serialize(hello.Capabilities);
        row.ForgeClisJson = hello.ForgeClis is null
            ? null
            : JsonSerializer.Serialize(hello.ForgeClis);
        row.PendingCommandApprovalsJson = hello.PendingCommandApprovals is null
            ? null
            : JsonSerializer.Serialize(hello.PendingCommandApprovals, ApprovalJson);
        row.CommitIdentityJson = hello.CommitIdentity is null
            ? null
            : JsonSerializer.Serialize(hello.CommitIdentity);

        // The durable twin of ProvidersJson: survives RemoveAsync so the execution-target
        // decision still knows which CLIs this machine had while the runner is offline.
        var snapshot = await db.RunnerProviderSnapshots.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.OrgId == orgId && s.UserId == userId, ct);
        if (snapshot is null)
        {
            snapshot = new RunnerProviderSnapshot
            {
                OrgId = orgId,
                UserId = userId,
                MachineName = hello.MachineName,
            };
            db.RunnerProviderSnapshots.Add(snapshot);
        }
        snapshot.MachineName = hello.MachineName;
        snapshot.DaemonVersion = hello.DaemonVersion;
        snapshot.ProvidersJson = row.ProvidersJson;
        snapshot.CapturedAt = now;

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Forgets a user's last-known providers — called when their last runner token
    /// is revoked, so a machine they no longer use stops steering where tasks run.</summary>
    public async Task ForgetProvidersAsync(Ulid orgId, string userId, CancellationToken ct = default)
    {
        await db.RunnerProviderSnapshots.IgnoreQueryFilters()
            .Where(s => s.OrgId == orgId && s.UserId == userId)
            .ExecuteDeleteAsync(ct);
    }

    /// <summary>Removes the presence row on disconnect — but only when it still belongs
    /// to the disconnecting connection. A runner that already reconnected (possibly to a
    /// different Worker replica) has overwritten the row; the stale connection's teardown
    /// must not erase the live one.</summary>
    public async Task RemoveAsync(
        Ulid orgId, string userId, string connectionId, CancellationToken ct = default)
    {
        await db.RunnerPresences.IgnoreQueryFilters()
            .Where(p => p.OrgId == orgId && p.UserId == userId && p.ConnectionId == connectionId)
            .ExecuteDeleteAsync(ct);
    }
}
