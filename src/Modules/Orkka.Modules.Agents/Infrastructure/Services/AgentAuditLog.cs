namespace Orkka.Modules.Agents.Infrastructure.Services;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orkka.Git;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Domain;
using Orkka.SharedKernel.Utilities;

/// <summary>
/// Persists audit events to the append-only <c>agent_audit_events</c> table. Each write runs
/// in its own DI scope + DbContext so it is independent of the caller's transaction (audit must
/// survive a caller rollback) and safe to call from singletons. Summaries and metadata are
/// redacted and length-capped so no secret ever lands in the log.
///
/// Tamper evidence: every row carries the SHA-256 of the org's previous row and its own hash over
/// its immutable fields, computed under a per-org lock (in-process: Orkka is one process) and a
/// write transaction, so concurrent writers serialize on the chain head. A dropped write (this method swallows failures on purpose — audit
/// must never break the run it observes) leaves no gap: the chain covers what was persisted, and
/// <c>POST /api/audit/verify</c> checks exactly that.
/// </summary>
internal sealed class AgentAuditLog(
    IServiceScopeFactory scopeFactory,
    ILogger<AgentAuditLog> logger) : IAgentAuditLog
{
    private const int MaxSummaryLength = 4000;
    private const int MaxMetadataLength = 8_000;

    /// <summary>One chain-head writer per org at a time. Static: every instance writes the same file.</summary>
    private static readonly KeyedLock<Ulid> OrgLocks = new();

    private static readonly JsonSerializerOptions MetadataJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public async Task RecordAsync(AgentAuditRecord record, CancellationToken ct = default)
    {
        try
        {
            var summary = GitUrlRedactor.Redact(record.Summary);
            if (summary.Length > MaxSummaryLength)
                summary = summary[..MaxSummaryLength];

            var tool = record.Tool;
            if (tool is { Length: > 120 })
                tool = tool[..120];

            string? metadata = null;
            if (record.Metadata is not null)
            {
                metadata = GitUrlRedactor.Redact(JsonSerializer.Serialize(record.Metadata, MetadataJson));
                if (metadata.Length > MaxMetadataLength)
                    metadata = JsonSerializer.Serialize(new { truncated = true, preview = metadata[..1000] });
            }

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();

            var entity = new AgentAuditEvent
            {
                Id = Ulid.NewUlid(),
                OrgId = record.OrgId,
                ProjectId = record.ProjectId,
                TaskId = record.TaskId,
                AgentId = record.AgentId,
                ExecutionId = record.ExecutionId,
                EventType = record.EventType,
                Actor = string.IsNullOrWhiteSpace(record.Actor) ? "agent" : record.Actor,
                ActorKind = record.ActorKind,
                ActorUserId = record.ActorUserId,
                Tool = tool,
                Summary = summary,
                MetadataJson = metadata,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            // Serialize chain-head reads per org: two concurrent writers must not both link to
            // the same predecessor. The lock orders this process's writers; the transaction
            // (BEGIN IMMEDIATE on SQLite) makes the read and the insert one unit. (The in-memory
            // provider used by unit tests has no transactions — the lock alone serializes there.)
            await using var orgLock = await OrgLocks.AcquireAsync(record.OrgId, ct);
            var relational = db.Database.IsRelational();
            await using var tx = relational ? await db.Database.BeginTransactionAsync(ct) : null;

            var previous = await db.AgentAuditEvents.IgnoreQueryFilters().AsNoTracking()
                .Where(e => e.OrgId == record.OrgId && e.EntryHash != null)
                .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id)
                .Select(e => e.EntryHash)
                .FirstOrDefaultAsync(ct);

            // EntryHash is computed in AgentsDbContext.OnBeforeSave, after CreatedAt is stamped.
            entity.PreviousHash = previous;

            db.AgentAuditEvents.Add(entity);
            await db.SaveChangesAsync(ct);
            if (tx is not null) await tx.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            // Auditing must never break the run it is observing — log and move on.
            logger.LogWarning(ex, "Failed to write agent audit event {EventType}", record.EventType);
        }
    }
}
