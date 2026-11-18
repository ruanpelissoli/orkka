namespace Orkka.Modules.Tasks.Features.MergeResolution;

using System.Text.Json;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using FastEndpoints;

public sealed class GetMergeConflictsEndpoint(
    TasksDbContext db,
    ICurrentUser currentUser)
    : EndpointWithoutRequest<MergeConflictsResponse>
{
    internal static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public override void Configure()
    {
        Get("/api/tasks/{taskId:ulid}/merge-conflicts");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var taskId = Ulid.Parse(Route<string>("taskId"));

        var task = await db.Tasks
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == taskId && t.OrgId == currentUser.OrgId, ct);
        if (task is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var snapshot = TryParseSnapshot(task.MergeConflictsJson);
        var files = (snapshot?.Files ?? [])
            .Select(f => new MergeConflictFileResponse(f.Path, f.Detail, f.Choice))
            .ToList();

        // The AUTOMATIC resolution pass sets no strategy, but it does hold the task's
        // lease (heartbeat-renewed for the whole orchestration, released however it
        // ends). Surfacing it as "resolving" stops the page from offering the chooser
        // while an agent is already working — picking a strategy then would just queue
        // a second, redundant run. Only counts when merge context exists, so an active
        // lease from a normal pipeline phase never reads as merge activity.
        var autoResolving =
            (task.MergeFailureReason is not null || task.MergeConflictsJson is not null)
            && task.LeaseExpiresAt is { } leaseUntil && leaseUntil > DateTimeOffset.UtcNow;

        // "blocked" = the failure is environmental (dirty working repo, runner offline) —
        // no resolution strategy can fix it, so the page shows the reason and how to fix
        // it instead of the conflict chooser. Checked before the conflict snapshot: even
        // with captured conflicts, the environment must be fixed first.
        // "exhausted" = automation genuinely ran out of moves: the resolver ran its full budget,
        // its output was verified, and the merge still is not finished. Distinct from
        // "awaitingChoice", which means the platform is waiting on a human for the FIRST time
        // and a strategy click may well be all it takes. Conflating the two made every merge
        // failure look equally recoverable, which is the thing this state exists to end.
        var status = task.IsMerged
            ? "resolved"
            : task.MergeResolutionStrategy is not null || autoResolving
                ? "resolving"
                : MergeBlockedReasons.IsEnvironmentBlocked(task.MergeFailureReason)
                  || task.MergeFailureKind == MergeFailureKind.EnvironmentBlocked
                  // A failed push (branch never reached the forge) is environmental too: no
                  // conflict strategy can fix it, and offering the chooser for it is how the
                  // merge page read as broken. The reason text carries the remediation.
                  || task.MergeFailureKind == MergeFailureKind.PushFailed
                    ? "blocked"
                    : task.MergeFailureKind == MergeFailureKind.ResolutionExhausted
                        ? "exhausted"
                        : task.MergeConflictsJson is not null || task.MergeFailureReason is not null
                            ? "awaitingChoice"
                            : "none";

        await Send.OkAsync(new MergeConflictsResponse(
            status,
            task.Title,
            snapshot?.Summary,
            files,
            task.MergeFailureReason,
            task.MergeResolutionStrategy,
            task.MergeFailureKind?.ToString(),
            // Only while something is actually running — a stale stamp would have the page
            // counting up next to a chooser.
            status == "resolving" ? task.MergeResolutionStartedAt : null,
            task.QueuedMergeStrategy,
            task.QueuedMergeGuidance), ct);
    }

    internal static MergeConflictSnapshot? TryParseSnapshot(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<MergeConflictSnapshot>(json, SnapshotJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
