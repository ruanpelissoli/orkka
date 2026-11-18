namespace Orkka.Modules.Tasks.Features.CodeChanges;

using Orkka.Modules.Tasks.Infrastructure;
using Orkka.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using FastEndpoints;

/// <summary>
/// The permanently stored code-change history of a task, newest first. Summaries only — the
/// diff text is fetched per snapshot, so listing never drags megabytes through the page load.
/// </summary>
public sealed class ListCodeChangeSnapshotsEndpoint(
    TasksDbContext db,
    ICurrentUser currentUser)
    : EndpointWithoutRequest<List<CodeChangeSnapshotSummaryResponse>>
{
    public override void Configure()
    {
        Get("/api/tasks/{taskId:ulid}/code-changes");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var taskId = Ulid.Parse(Route<string>("taskId"));

        var taskExists = await db.Tasks
            .AsNoTracking()
            .AnyAsync(t => t.Id == taskId && t.OrgId == currentUser.OrgId, ct);
        if (!taskExists)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // The live row (a run in progress) comes first when there is one — it is the newest
        // truth by definition; permanent snapshots follow newest-first.
        var snapshots = await db.CodeChangeSnapshots
            .AsNoTracking()
            .Where(s => s.TaskId == taskId)
            .OrderByDescending(s => s.IsLive)
            .ThenByDescending(s => s.CreatedAt)
            .Select(s => new CodeChangeSnapshotSummaryResponse(
                s.Id, s.ExecutionId, s.Lane.ToString(), s.IsLive ? s.UpdatedAt : s.CreatedAt,
                s.BranchName, s.BaseBranch,
                s.FileCount, s.Additions, s.Deletions, s.IsTruncated,
                s.IsLive, s.Revision))
            .ToListAsync(ct);

        await Send.OkAsync(snapshots, ct);
    }
}
