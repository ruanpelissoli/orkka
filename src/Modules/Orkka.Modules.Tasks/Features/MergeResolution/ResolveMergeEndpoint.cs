namespace Orkka.Modules.Tasks.Features.MergeResolution;

using System.Text.Json;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using FastEndpoints;
using FluentValidation;

public sealed class ResolveMergeValidator : Validator<ResolveMergeRequest>
{
    private static readonly string[] Strategies = ["combine", "task", "current", "perFile"];
    private static readonly string[] FileChoices = ["task", "current", "ai"];

    public ResolveMergeValidator()
    {
        RuleFor(x => x.Strategy)
            .Must(s => Strategies.Contains(s))
            .WithMessage("Strategy must be one of: combine, task, current, perFile.");

        RuleFor(x => x.Guidance)
            .MaximumLength(2000).WithMessage("Guidance is limited to 2000 characters.");

        When(x => x.Strategy == "perFile", () =>
        {
            RuleFor(x => x.Files)
                .NotEmpty().WithMessage("Per-file resolution requires file choices.");
            RuleForEach(x => x.Files!).ChildRules(f =>
            {
                f.RuleFor(c => c.Path).NotEmpty();
                f.RuleFor(c => c.Choice)
                    .Must(c => FileChoices.Contains(c))
                    .WithMessage("Each file choice must be one of: task, current, ai.");
            });
        });
    }
}

public sealed class ResolveMergeEndpoint(
    TasksDbContext db,
    ICurrentUser currentUser,
    ITaskNotifier taskNotifier,
    ILogger<ResolveMergeEndpoint> logger)
    : Endpoint<ResolveMergeRequest>
{
    public override void Configure()
    {
        Post("/api/tasks/{taskId:ulid}/resolve-merge");
    }

    public override async Task HandleAsync(ResolveMergeRequest req, CancellationToken ct)
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

        if (task.IsMerged)
        {
            ThrowError("This task is already merged.");
            return;
        }
        if (string.IsNullOrEmpty(task.PrUrl))
        {
            ThrowError("This task has no pull request to merge.");
            return;
        }
        // What is running right now, if anything. The automatic pass arms no strategy and is
        // recognisable only by the lease it holds — it is always a combine, so calling it one
        // lets every case below be decided the same way.
        var leaseHeld = task.LeaseExpiresAt is { } leaseUntil && leaseUntil > DateTimeOffset.UtcNow;
        var running = task.MergeResolutionStrategy ?? (leaseHeld ? "combine" : null);

        if (running is not null)
        {
            // Asking again for what is already running is not an error — it is someone who
            // cannot see that it started (a merge click arms 'combine' server-side). Refusing
            // it showed a failure for a merge that was in fact being handled.
            if (running == req.Strategy
                && req.Strategy != "perFile"
                && string.IsNullOrWhiteSpace(req.Guidance))
            {
                await Send.NoContentAsync(ct);
                return;
            }

            // Per-file is the one thing that cannot be queued: its choices ride inside the
            // conflict snapshot, and the attempt in flight may rewrite that snapshot before
            // the follow-up would run — so the choices would be against conflicts that no
            // longer exist.
            if (req.Strategy == "perFile")
            {
                ThrowError(
                    $"Orkka is resolving this merge right now ('{running}'). File-by-file " +
                    "choices have to be made against the final conflicts, so wait for the " +
                    "current attempt to finish before applying them.");
                return;
            }

            // Everything else is steering: a different strategy, or the same one with new
            // guidance. Queue it instead of refusing — the user is saying the running attempt
            // is not what they want, and making them watch it finish before they are allowed
            // to say so is the complaint this exists to answer. One slot, newest wins.
            task.QueuedMergeStrategy = req.Strategy;
            task.QueuedMergeGuidance =
                string.IsNullOrWhiteSpace(req.Guidance) ? null : req.Guidance.Trim();
            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "Queued follow-up merge resolution '{Strategy}' for task {TaskId} behind the running '{Running}'",
                req.Strategy, taskId, running);

            await taskNotifier.SendStreamEventAsync(taskId, new StreamEvent(
                "Progress",
                "Follow-up queued — it starts as soon as the current attempt finishes.",
                DateTimeOffset.UtcNow, null));

            await Send.NoContentAsync(ct);
            return;
        }
        if (MergeBlockedReasons.IsEnvironmentBlocked(task.MergeFailureReason))
        {
            ThrowError(
                "The merge is blocked by your repository's current state, not by conflicting " +
                "changes — no resolution can run until that is fixed: " + task.MergeFailureReason);
            return;
        }
        if (task.MergeFailureKind == Orkka.Modules.Tasks.Contracts.MergeFailureKind.PushFailed)
        {
            // Same shape as environment-blocked: the branch never reached the forge, so a
            // conflict strategy has nothing to resolve — running one would only bill tokens.
            ThrowError(
                "The task's branch could not be pushed to the repository, so there is nothing "
                + "to resolve yet: " + task.MergeFailureReason);
            return;
        }

        // Per-file picks ride inside the conflict snapshot so the worker (a separate
        // process) sees exactly what the user chose.
        if (req.Strategy == "perFile")
        {
            var snapshot = GetMergeConflictsEndpoint.TryParseSnapshot(task.MergeConflictsJson);
            if (snapshot is null || snapshot.Files.Count == 0)
            {
                ThrowError("Conflict details are not available yet — try again in a moment.");
                return;
            }

            var choiceByPath = req.Files!.ToDictionary(f => f.Path, f => f.Choice);
            var updatedFiles = snapshot.Files
                .Select(f => f with { Choice = choiceByPath.GetValueOrDefault(f.Path, f.Choice ?? "ai") })
                .ToList();
            task.MergeConflictsJson = JsonSerializer.Serialize(
                snapshot with { Files = updatedFiles },
                GetMergeConflictsEndpoint.SnapshotJsonOptions);
        }

        task.MergeResolutionStrategy = req.Strategy;
        task.MergeResolutionGuidance = string.IsNullOrWhiteSpace(req.Guidance) ? null : req.Guidance.Trim();
        task.MergeResolutionStartedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "User requested merge resolution '{Strategy}' for task {TaskId}", req.Strategy, taskId);

        // Live nudge for anyone watching the task/merge page — the worker picks the
        // request up on its next merge cycle.
        await taskNotifier.SendStreamEventAsync(taskId, new StreamEvent(
            "Progress", "Merge resolution requested — combining changes shortly...",
            DateTimeOffset.UtcNow, null));

        await Send.NoContentAsync(ct);
    }
}
