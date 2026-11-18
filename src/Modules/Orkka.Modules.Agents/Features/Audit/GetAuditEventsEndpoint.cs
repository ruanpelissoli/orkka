namespace Orkka.Modules.Agents.Features.Audit;

using System.Text;
using System.Text.Json;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Domain;
using Orkka.Modules.Agents.Infrastructure;
using Orkka.SharedKernel.Security;

public sealed record AuditEventDto(
    string Id,
    string EventType,
    string Actor,
    string ActorKind,
    string? ActorUserId,
    string? Tool,
    string Summary,
    JsonElement? Metadata,
    string? TaskId,
    string? ProjectId,
    string? AgentId,
    string? PreviousHash,
    string? EntryHash,
    DateTimeOffset CreatedAt);

public sealed record AuditPage(IReadOnlyList<AuditEventDto> Items, string? NextCursor);

/// <summary>Filters for the list. Cursor = the last row's id (ULIDs sort by time).</summary>
public record AuditQueryRequest
{
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    /// <summary>Comma-separated event type names.</summary>
    public string? EventType { get; init; }
    /// <summary>Actor user id, or a substring of the display actor.</summary>
    public string? Actor { get; init; }
    public string? ProjectId { get; init; }
    public string? TaskId { get; init; }
    public string? Cursor { get; init; }
    public int? Limit { get; init; }
}

internal static class AuditQueries
{
    public static IQueryable<AgentAuditEvent> Apply(IQueryable<AgentAuditEvent> query, AuditQueryRequest req, out string? error)
    {
        error = null;
        if (req.From is { } from) query = query.Where(e => e.CreatedAt >= from);
        if (req.To is { } to) query = query.Where(e => e.CreatedAt < to);
        if (!string.IsNullOrWhiteSpace(req.EventType))
        {
            var types = new List<AgentAuditEventType>();
            foreach (var raw in req.EventType.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!Enum.TryParse<AgentAuditEventType>(raw, ignoreCase: true, out var t))
                {
                    error = $"Unknown event type \"{raw}\".";
                    return query;
                }
                types.Add(t);
            }
            query = query.Where(e => types.Contains(e.EventType));
        }
        if (!string.IsNullOrWhiteSpace(req.Actor))
        {
            var actor = req.Actor.Trim();
            query = query.Where(e => e.ActorUserId == actor || EF.Functions.Like(e.Actor, "%" + actor + "%"));
        }
        if (Ulid.TryParse(req.ProjectId, out var projectId)) query = query.Where(e => e.ProjectId == projectId);
        if (Ulid.TryParse(req.TaskId, out var taskId)) query = query.Where(e => e.TaskId == taskId);
        if (Ulid.TryParse(req.Cursor, out var cursor)) query = query.Where(e => e.Id.CompareTo(cursor) < 0);
        return query;
    }

    public static AuditEventDto ToDto(AgentAuditEvent e) => new(
        e.Id.ToString(), e.EventType.ToString(), e.Actor, e.ActorKind.ToString(), e.ActorUserId, e.Tool, e.Summary,
        e.MetadataJson is null ? null : JsonDocument.Parse(e.MetadataJson).RootElement.Clone(),
        e.TaskId?.ToString(), e.ProjectId?.ToString(), e.AgentId?.ToString(),
        e.PreviousHash, e.EntryHash, e.CreatedAt);
}

/// <summary>
/// The org's audit trail, most recent first, with filters and cursor paging. Requires the
/// AuditView grant — the audit log is a sensitive security record.
/// </summary>
public sealed class GetAuditEventsEndpoint(AgentsDbContext db, ICurrentUser currentUser)
    : Endpoint<AuditQueryRequest, AuditPage>
{
    public override void Configure() => Get("/api/audit");

    public override async Task HandleAsync(AuditQueryRequest req, CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.AuditView))
            return;

        var take = Math.Clamp(req.Limit ?? 200, 1, 1000);
        var query = AuditQueries.Apply(
            db.AgentAuditEvents.AsNoTracking().Where(e => e.OrgId == currentUser.OrgId), req, out var error);
        if (error is not null)
        {
            AddError(r => r.EventType, error);
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        // ULIDs are time-ordered, so id order == time order and doubles as the cursor.
        var rows = await query.OrderByDescending(e => e.Id).Take(take + 1).ToListAsync(ct);
        var next = rows.Count > take ? rows[take - 1].Id.ToString() : null;
        if (rows.Count > take) rows.RemoveAt(rows.Count - 1);

        await Send.OkAsync(new AuditPage(rows.Select(AuditQueries.ToDto).ToList(), next), ct);
    }
}
