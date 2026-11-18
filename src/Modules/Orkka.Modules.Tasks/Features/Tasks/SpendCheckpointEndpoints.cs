namespace Orkka.Modules.Tasks.Features.Tasks;

using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orkka.Modules.Usage.Contracts;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.SharedKernel.Security;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>
/// The two answers to a spend checkpoint (a task parked in Pending Questions by
/// <c>TaskOrchestrator.PauseForSpendLimitAsync</c> because it reached its project's task spend
/// limit). Both are answers, not free text: a typed reply on that question would re-queue the
/// phase against the same exhausted ceiling and park again after one turn.
///
/// <b>Continue</b> approves one more allotment — the task's approved spend becomes "estimated
/// list-price spend so far + the project's limit", in US dollars — and re-queues the phase the checkpoint interrupted, exactly as
/// answering any other question does; the agent reads its own hand-off from the conversation.
/// <b>Stop</b> returns the task to that lane as Blocked, where it shows in the inbox and the
/// usual Retry / reset / archive actions apply (a Retry without a Continue pauses again).
/// </summary>
internal static class SpendCheckpoint
{
    public static async Task<TaskEntity?> LoadParkedTaskAsync(
        TasksDbContext db, Ulid taskId, Ulid orgId, CancellationToken ct)
    {
        var entity = await db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId && t.OrgId == orgId, ct);
        if (entity is null) return null;

        if (entity.CurrentLane != Lane.PendingQuestions || entity.Status != TaskStatus.WaitingAnswer)
            return null;

        // The question the task is parked on has to be a spend checkpoint — not a clarifying
        // question the agent asked, which needs a real answer and gets one through /messages.
        var latestQuestion = await db.TaskMessages.AsNoTracking()
            .Where(m => m.TaskId == taskId && m.MessageType == MessageType.Question)
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => m.Metadata)
            .FirstOrDefaultAsync(ct);
        if (latestQuestion is null || !latestQuestion.Contains("\"spendCheckpoint\"", StringComparison.Ordinal))
            return null;

        return entity;
    }

    /// <summary>The task's estimated list-price spend so far, in US dollars.</summary>
    public static async Task<decimal> SpentUsdAsync(
        IUsageQuery usageQuery, TaskEntity task, CancellationToken ct)
    {
        var spend = await usageQuery.GetTaskEstimatedSpendAsync(task.OrgId, task.ProjectId, [task.Id], ct);
        return Math.Round(spend.GetValueOrDefault(task.Id), 4, MidpointRounding.AwayFromZero);
    }
}

public sealed class ContinueTaskSpendEndpoint(
    TasksDbContext db,
    IProjectsApi projectsApi,
    IUsageQuery usageQuery,
    ICurrentUser currentUser,
    ITaskNotifier taskNotifier,
    ILogger<ContinueTaskSpendEndpoint> logger)
    : EndpointWithoutRequest<TaskResponse>
{
    public override void Configure()
    {
        Post("/api/tasks/{id:ulid}/spend/continue");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.TasksExecute))
            return;

        var id = Ulid.Parse(Route<string>("id"));
        var entity = await SpendCheckpoint.LoadParkedTaskAsync(db, id, currentUser.OrgId, ct);
        if (entity is null)
        {
            ThrowError("This task is not waiting at a spend checkpoint.");
            return;
        }

        var project = await projectsApi.GetProjectSummaryAsync(entity.ProjectId, ct);
        var spent = await SpendCheckpoint.SpentUsdAsync(usageQuery, entity, ct);
        // One more allotment of the project's limit on top of what is already spent. If the
        // project switched the limit off meanwhile there is nothing to grant — the re-queued
        // run is simply uncapped, which is what "off" means.
        var allotment = project?.Settings?.TaskSpendLimit;
        if (allotment is { } more)
            entity.SpendApprovedUsd = spent + more;

        var fromLane = entity.CurrentLane;
        entity.CurrentLane = entity.QuestionSourceLane ?? Lane.Developing;
        entity.Status = TaskStatus.InQueue;
        entity.QuestionSourceLane = null;
        entity.HasAgentWorking = false;

        var message = new TaskMessage
        {
            OrgId = entity.OrgId,
            TaskId = entity.Id,
            Role = "user",
            Content = allotment is { } granted
                ? string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"Continue — approved another US${granted:N2} of estimated list-price spend (up to US${entity.SpendApprovedUsd:N2} in total).")
                : "Continue.",
            MessageType = MessageType.Answer,
            SenderId = currentUser.UserId,
            Metadata = """{"spendDecision":"continue"}""",
        };
        db.TaskMessages.Add(message);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Task {TaskId}: spend checkpoint answered Continue — approved up to US${Approved} of estimated spend, re-queued in {Lane}",
            id, entity.SpendApprovedUsd, entity.CurrentLane);

        await taskNotifier.SendNewMessageAsync(entity.Id, new NewMessageEvent(
            entity.Id, message.Id, message.Role, message.Content,
            message.MessageType.ToString(), message.SenderId, message.CreatedAt));
        await taskNotifier.SendLaneChangedAsync(entity.ProjectId, new LaneChangedEvent(
            entity.Id, entity.Title, fromLane.ToString(), entity.CurrentLane.ToString(),
            entity.Status.ToString(), DateTimeOffset.UtcNow));

        var repo = entity.RepoId is { } repoId ? await projectsApi.GetRepoAsync(repoId, ct) : null;
        await Send.OkAsync(entity.ToResponse(project!.Key, repo), ct);
    }
}

public sealed class StopTaskSpendEndpoint(
    TasksDbContext db,
    IProjectsApi projectsApi,
    ICurrentUser currentUser,
    ITaskNotifier taskNotifier,
    ILogger<StopTaskSpendEndpoint> logger)
    : EndpointWithoutRequest<TaskResponse>
{
    public override void Configure()
    {
        Post("/api/tasks/{id:ulid}/spend/stop");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.TasksExecute))
            return;

        var id = Ulid.Parse(Route<string>("id"));
        var entity = await SpendCheckpoint.LoadParkedTaskAsync(db, id, currentUser.OrgId, ct);
        if (entity is null)
        {
            ThrowError("This task is not waiting at a spend checkpoint.");
            return;
        }

        var fromLane = entity.CurrentLane;
        // Back to the lane it was working in, parked as Blocked: visible in the inbox, and every
        // existing way out (Retry, reset, archive, a message with direction) still applies.
        entity.CurrentLane = entity.QuestionSourceLane ?? Lane.Developing;
        entity.Status = TaskStatus.Blocked;
        entity.QuestionSourceLane = null;
        entity.HasAgentWorking = false;

        var message = new TaskMessage
        {
            OrgId = entity.OrgId,
            TaskId = entity.Id,
            Role = "user",
            Content = "Stop — leave the task here for now.",
            MessageType = MessageType.Answer,
            SenderId = currentUser.UserId,
            Metadata = """{"spendDecision":"stop"}""",
        };
        db.TaskMessages.Add(message);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Task {TaskId}: spend checkpoint answered Stop — parked as Blocked in {Lane}",
            id, entity.CurrentLane);

        await taskNotifier.SendNewMessageAsync(entity.Id, new NewMessageEvent(
            entity.Id, message.Id, message.Role, message.Content,
            message.MessageType.ToString(), message.SenderId, message.CreatedAt));
        await taskNotifier.SendLaneChangedAsync(entity.ProjectId, new LaneChangedEvent(
            entity.Id, entity.Title, fromLane.ToString(), entity.CurrentLane.ToString(),
            entity.Status.ToString(), DateTimeOffset.UtcNow));

        var project = await projectsApi.GetProjectSummaryAsync(entity.ProjectId, ct);
        var repo = entity.RepoId is { } repoId ? await projectsApi.GetRepoAsync(repoId, ct) : null;
        await Send.OkAsync(entity.ToResponse(project!.Key, repo), ct);
    }
}
