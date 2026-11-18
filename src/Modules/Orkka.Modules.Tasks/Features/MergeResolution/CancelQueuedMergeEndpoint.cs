namespace Orkka.Modules.Tasks.Features.MergeResolution;

using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using FastEndpoints;

/// <summary>
/// Drops the follow-up resolution the user queued behind the running attempt.
///
/// Exists so queuing is never a trap. Steering mid-run is a guess about an attempt nobody has
/// seen the result of yet — when the running one turns out fine, the queued one would spend a
/// full agent run undoing it, and without this the only way out would be to let that happen.
/// The attempt in flight is untouched either way.
/// </summary>
public sealed class CancelQueuedMergeEndpoint(
    TasksDbContext db,
    ICurrentUser currentUser,
    ILogger<CancelQueuedMergeEndpoint> logger)
    : EndpointWithoutRequest
{
    public override void Configure()
    {
        Delete("/api/tasks/{taskId:ulid}/resolve-merge/queued");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.CodeReviewMerge))
            return;

        var taskId = Ulid.Parse(Route<string>("taskId"));

        var task = await db.Tasks
            .FirstOrDefaultAsync(t => t.Id == taskId && t.OrgId == currentUser.OrgId, ct);
        if (task is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // Idempotent: the follow-up may have been promoted between the page rendering the
        // cancel button and the click landing, and "there is nothing queued" is the state the
        // caller asked for either way.
        if (task.QueuedMergeStrategy is not null || task.QueuedMergeGuidance is not null)
        {
            task.QueuedMergeStrategy = null;
            task.QueuedMergeGuidance = null;
            await db.SaveChangesAsync(ct);

            logger.LogInformation("Cancelled the queued merge resolution for task {TaskId}", taskId);
        }

        await Send.NoContentAsync(ct);
    }
}
