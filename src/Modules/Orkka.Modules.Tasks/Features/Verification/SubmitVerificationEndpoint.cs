namespace Orkka.Modules.Tasks.Features.Verification;

using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.SharedKernel.Security;

using FastEndpoints;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

public sealed record SubmitVerificationRequest(string Decision, string? Feedback);

public sealed class SubmitVerificationValidator : Validator<SubmitVerificationRequest>
{
    public SubmitVerificationValidator()
    {
        RuleFor(x => x.Decision)
            .NotEmpty()
            .Must(d => d.Equals("approve", StringComparison.OrdinalIgnoreCase)
                     || d.Equals("request_changes", StringComparison.OrdinalIgnoreCase))
            .WithMessage("Decision must be 'approve' or 'request_changes'.");

        RuleFor(x => x.Feedback)
            .NotEmpty()
            .When(x => x.Decision.Equals("request_changes", StringComparison.OrdinalIgnoreCase))
            .WithMessage("Feedback is required when requesting changes.");

        RuleFor(x => x.Feedback)
            .MaximumLength(10000).WithMessage("Feedback must be 10,000 characters or fewer.");
    }
}

/// <summary>
/// Resolves a task parked in Testing/WaitingUserVerification: approve moves it down the
/// pipeline (CodeReview, or straight to Done when code review is skipped); request_changes
/// stores the feedback in the conversation and re-queues the dev agent.
/// </summary>
public sealed class SubmitVerificationEndpoint(
    TasksDbContext db,
    ICurrentUser currentUser,
    ITaskNotifier taskNotifier,
    ILogger<SubmitVerificationEndpoint> logger)
    : Endpoint<SubmitVerificationRequest>
{
    public override void Configure()
    {
        Post("/api/tasks/{taskId:ulid}/verification");
    }

    public override async Task HandleAsync(SubmitVerificationRequest req, CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.TasksExecute))
            return;

        var taskId = Ulid.Parse(Route<string>("taskId"));

        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId && t.OrgId == currentUser.OrgId, ct);
        if (task is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (task.Status != TaskStatus.WaitingUserVerification)
        {
            ThrowError("Task is not waiting for verification.");
            return;
        }

        var fromLane = task.CurrentLane;
        var isApprove = req.Decision.Equals("approve", StringComparison.OrdinalIgnoreCase);
        TaskMessage message;

        if (isApprove)
        {
            if (task.SkipCodeReview)
            {
                task.CurrentLane = Lane.Done;
                task.Status = TaskStatus.Completed;
                task.CompletedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                task.CurrentLane = Lane.CodeReview;
                task.Status = TaskStatus.InQueue;
            }
            task.HasAgentWorking = false;

            message = new TaskMessage
            {
                OrgId = task.OrgId,
                TaskId = taskId,
                Role = "system",
                Content = task.SkipCodeReview
                    ? "Verified by the requester — task completed."
                    : "Verified by the requester — handing off to code review.",
                MessageType = MessageType.SystemEvent,
                SenderId = currentUser.UserId
            };

            logger.LogInformation("Verification approved for task {TaskId}: moving to {Lane}",
                taskId, task.CurrentLane);
        }
        else
        {
            // Feedback lands in the conversation as a user answer — exactly what the dev
            // agent reads on the re-run. Fresh automated-QA budget for this human cycle.
            message = new TaskMessage
            {
                OrgId = task.OrgId,
                TaskId = taskId,
                Role = "user",
                Content = req.Feedback!,
                MessageType = MessageType.Answer,
                SenderId = currentUser.UserId
            };

            task.CurrentLane = Lane.Developing;
            task.Status = TaskStatus.InQueue;
            task.HasAgentWorking = false;
            task.QaCycleCount = 0;
            task.CheckCycleCount = 0;

            logger.LogInformation("Verification feedback for task {TaskId}: re-queuing for dev", taskId);
        }

        db.TaskMessages.Add(message);
        await db.SaveChangesAsync(ct);

        await taskNotifier.SendNewMessageAsync(taskId, new NewMessageEvent(
            taskId, message.Id, message.Role, message.Content,
            message.MessageType.ToString(), message.SenderId, message.CreatedAt));

        await taskNotifier.SendLaneChangedAsync(task.ProjectId, new LaneChangedEvent(
            taskId, task.Title, fromLane.ToString(), task.CurrentLane.ToString(),
            task.Status.ToString(), DateTimeOffset.UtcNow));

        await Send.NoContentAsync(ct);
    }
}
