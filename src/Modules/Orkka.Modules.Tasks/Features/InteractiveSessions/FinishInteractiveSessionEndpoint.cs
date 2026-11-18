namespace Orkka.Modules.Tasks.Features.InteractiveSessions;

using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.SharedKernel.Security;

/// <summary>
/// Ends an interactive session and ships it: the worker driving the session runs one last agent
/// turn to commit and push, then hands the task to the normal PR → Code Review path.
///
/// Always an explicit human action. An agent deciding on its own that a session is over and
/// opening a pull request is a bad surprise, so nothing else sets this.
///
/// Abandoning a session is deliberately NOT this endpoint — that is the existing reset/archive
/// flow, which already knows how to tear down a worktree and a branch.
/// </summary>
public sealed class FinishInteractiveSessionEndpoint(
    TasksDbContext db,
    ICurrentUser currentUser,
    ILogger<FinishInteractiveSessionEndpoint> logger)
    : EndpointWithoutRequest<FinishInteractiveSessionResponse>
{
    public override void Configure()
    {
        Post("/api/tasks/{taskId:ulid}/interactive-session/finish");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.TasksExecute))
            return;

        var taskId = Ulid.Parse(Route<string>("taskId")!);

        var task = await db.Tasks.FirstOrDefaultAsync(
            t => t.Id == taskId && t.OrgId == currentUser.OrgId, ct);

        // "Never was a session" is a 404; "was one, and it ended" is the 400 below. The live
        // flag alone can't tell them apart any more — it is cleared at close so the finished
        // task can re-enter the ordinary queue — hence the durable EndedAt marker.
        if (task is null || (!task.IsInteractive && task.InteractiveSessionEndedAt is null))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (task.InteractiveSessionStartedAt is null)
        {
            ThrowError("This session has already ended.");
            return;
        }

        // Idempotent: pressing Finish twice while the last turn runs must not restart it or move
        // the timestamp, which would look like a second session ending.
        if (task.InteractiveFinishRequestedAt is null)
        {
            task.InteractiveFinishRequestedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "Finish requested for interactive session {TaskId} by {UserId}",
                taskId, currentUser.UserId);
        }

        // The session's worker picks this up on its next loop tick — deliberately not executed
        // here. The API process holds no worktree and no warm CLI session; the machine driving
        // the session is the only one that can commit and push it.
        await Send.OkAsync(
            new FinishInteractiveSessionResponse(taskId, task.InteractiveFinishRequestedAt!.Value), ct);
    }
}

public sealed record FinishInteractiveSessionResponse(Ulid TaskId, DateTimeOffset FinishRequestedAt);
