namespace Orkka.Modules.Tasks.Infrastructure.Services;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Features.CodeReview;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>
/// The human board operations — approve/reject a plan, answer a task, submit a code review,
/// merge — as one service so the HTTP endpoints and the MCP tools
/// change task state the same way. Authorization is the caller's job (each surface knows its
/// own actor); this service takes an org and applies the operation.
///
/// Scoped: uses the ambient TasksDbContext (tenant filter on for request scopes; every query
/// also carries the org id explicitly so non-request callers behave identically).
/// </summary>
public sealed class BoardOperations(
    TasksDbContext db,
    IProjectsApi projectsApi,
    IPullRequestService pullRequests,
    ITaskNotifier taskNotifier,
    Orkka.SharedKernel.Events.IEventBus eventBus,
    TaskSubmissionService submissions,
    Orkka.Modules.Agents.Contracts.IProviderReadiness providerReadiness,
    ITaskNumberService taskNumbers,
    ILogger<BoardOperations> logger)
{
    // ── submit ──────────────────────────────────────────────────────────

    /// <summary>Drafting → InQueue, exactly like the Submit button (same pre-flight checks).</summary>
    public async Task<BoardOpOutcome> SubmitTaskAsync(Ulid orgId, Ulid taskId, string? locale, CancellationToken ct)
    {
        var entity = await db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId && t.OrgId == orgId, ct);
        if (entity is null)
            return BoardOpOutcome.NotFound("Task not found.");
        if (entity.CurrentLane != Lane.Drafting)
            return BoardOpOutcome.Conflict("Only tasks in Drafting can be submitted.");
        if (providerReadiness.GetAnyProviderBlocker() is { } providerBlocker)
            return BoardOpOutcome.Conflict(providerBlocker);
        if (entity.RepoId is null)
            return BoardOpOutcome.Invalid("A repository must be assigned before submitting.");

        await submissions.SubmitAsync(entity, locale, ct);
        return BoardOpOutcome.Success(taskId);
    }

    // ── plan review ─────────────────────────────────────────────────────

    public async Task<PendingPlanSummary?> GetPendingPlanAsync(Ulid orgId, Ulid taskId, CancellationToken ct)
    {
        var plan = await db.TaskPlans.AsNoTracking()
            .Where(p => p.TaskId == taskId && p.OrgId == orgId && p.Status == PlanStatus.PendingReview)
            .OrderByDescending(p => p.Version)
            .FirstOrDefaultAsync(ct);
        if (plan is null) return null;
        var files = plan.Content.FilesChanged?.Select(f => $"{f.Action}: {f.Path}").ToList() ?? [];
        return new PendingPlanSummary(plan.Id, plan.Version, plan.Content.Summary, plan.Content.Approach, files);
    }

    public async Task<BoardOpOutcome> ReviewPlanAsync(
        Ulid orgId, Ulid taskId, Ulid planId, bool approve, string? feedback, CancellationToken ct)
    {
        if (feedback?.Length > 5000)
            return BoardOpOutcome.Invalid("Feedback must be 5,000 characters or fewer.");

        var plan = await db.TaskPlans
            .Include(p => p.Task)
            .FirstOrDefaultAsync(p => p.Id == planId && p.TaskId == taskId && p.OrgId == orgId, ct);
        if (plan is null)
            return BoardOpOutcome.NotFound("Plan not found.");
        if (plan.Status != PlanStatus.PendingReview)
            return BoardOpOutcome.Conflict("Only plans in PendingReview status can be reviewed.");

        var task = plan.Task;

        if (approve)
        {
            plan.Status = PlanStatus.Approved;
            plan.ApprovedAt = DateTimeOffset.UtcNow;
            plan.Feedback = feedback;

            // Always move to Developing (fixed pipeline) — queued so the worker claims it
            task.CurrentLane = Lane.Developing;
            task.Status = TaskStatus.InQueue;
            task.HasAgentWorking = false;
        }
        else
        {
            // Delete the rejected plan to keep history clean
            db.TaskPlans.Remove(plan);

            // Preserve the rejected plan's research in the conversation: the next
            // planning run reuses it instead of re-paying for repo exploration.
            var approach = plan.Content.Approach.Length > 4000
                ? plan.Content.Approach[..4000] + "\n... (truncated)"
                : plan.Content.Approach;
            var files = plan.Content.FilesChanged is { Count: > 0 }
                ? string.Join("\n", plan.Content.FilesChanged.Select(f => $"- {f.Action}: {f.Path}"))
                : "(none listed)";

            var rejectionContext = $"""
                Plan v{plan.Version} was REJECTED.{(string.IsNullOrWhiteSpace(feedback) ? "" : $"\n\nFeedback: {feedback}")}

                The rejected plan is below for reference — its codebase research is still valid.
                Reuse it and address the feedback instead of re-exploring from scratch.

                Summary: {plan.Content.Summary}
                Files:
                {files}
                Approach:
                {approach}
                """;

            db.TaskMessages.Add(new TaskMessage
            {
                TaskId = taskId,
                OrgId = task.OrgId,
                Role = "reviewer",
                Content = rejectionContext,
                MessageType = MessageType.Text
            });

            // Move task back to Planning for re-planning — queued so the worker claims it
            task.CurrentLane = Lane.Planning;
            task.Status = TaskStatus.InQueue;
            task.HasAgentWorking = false;
        }

        await db.SaveChangesAsync(ct);

        await taskNotifier.SendPlanReviewedAsync(taskId, new PlanReviewedEvent(
            taskId, planId, approve ? "approve" : "reject", feedback, DateTimeOffset.UtcNow));
        await taskNotifier.SendLaneChangedAsync(task.ProjectId, new LaneChangedEvent(
            taskId, task.Title,
            Lane.PlanReady.ToString(),
            task.CurrentLane.ToString(),
            task.Status.ToString(), DateTimeOffset.UtcNow));

        logger.LogInformation("Plan {PlanId} on task {TaskId} {Decision}", planId, taskId, approve ? "approved" : "rejected");
        return BoardOpOutcome.Success(approve ? planId : null);
    }

    // ── answer / message ────────────────────────────────────────────────

    public async Task<BoardOpOutcome> AnswerTaskAsync(
        Ulid orgId, Ulid taskId, string content, string messageType, string? senderUserId, string? locale,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(content))
            return BoardOpOutcome.Invalid("Content is required.");
        if (!Enum.TryParse<MessageType>(messageType, true, out var parsedType))
            return BoardOpOutcome.Invalid("Invalid message type.");

        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId && t.OrgId == orgId, ct);
        if (task is null)
            return BoardOpOutcome.NotFound("Task not found.");

        // The reset sweep wipes all messages when it finalizes — reject instead of
        // silently discarding what the user just typed.
        if (task.ResetRequestedAt is not null)
            return BoardOpOutcome.Conflict("Task is being reset.");

        task.RequesterLocale = locale ?? task.RequesterLocale;

        // The routing question of a tracker task (TasksApi.AskTrackerRepoAsync): the answer is a
        // repository, not guidance for an agent — no agent is waiting.
        if (task.CurrentLane == Lane.PendingQuestions && task.QuestionSourceLane == Lane.Drafting && task.RepoId is null)
            return await AnswerRepoQuestionAsync(task, content, parsedType, senderUserId, locale, ct);

        var message = new TaskMessage
        {
            OrgId = orgId,
            TaskId = taskId,
            Role = "user",
            Content = content,
            MessageType = parsedType == MessageType.Text ? MessageType.Answer : parsedType,
            SenderId = senderUserId
        };
        db.TaskMessages.Add(message);

        // Answering while the agent waits in PendingQuestions resumes the phase the
        // question came from — the worker queue re-claims the task and re-runs the
        // agent with the full conversation (including this answer) in its prompt.
        var resumedFromQuestion = task.CurrentLane == Lane.PendingQuestions;
        var fromLane = task.CurrentLane;
        if (resumedFromQuestion)
        {
            task.CurrentLane = task.QuestionSourceLane ?? Lane.Ready;
            task.Status = TaskStatus.InQueue;
            task.QuestionSourceLane = null;
            task.HasAgentWorking = false;
        }

        // A message on a task parked for manual verification is feedback — send it back
        // to the dev agent with fresh automated-QA and review/no-commit budgets for this
        // human cycle.
        var resumedFromVerification = !resumedFromQuestion
            && task.Status == TaskStatus.WaitingUserVerification;
        if (resumedFromVerification)
        {
            task.CurrentLane = Lane.Developing;
            task.Status = TaskStatus.InQueue;
            task.HasAgentWorking = false;
            task.QaCycleCount = 0;
            task.CheckCycleCount = 0;
            task.ReviewCycleCount = 0;
        }

        // A message on a review-escalated task is guidance, and guidance means "try again".
        var resumedFromReviewEscalation = !resumedFromQuestion && !resumedFromVerification
            && task.Status == TaskStatus.WaitingCodeReview;
        if (resumedFromReviewEscalation)
        {
            task.CurrentLane = Lane.Developing;
            task.Status = TaskStatus.InQueue;
            task.HasAgentWorking = false;
            task.ReviewCycleCount = 0;
        }

        await db.SaveChangesAsync(ct);

        await taskNotifier.SendNewMessageAsync(taskId, new NewMessageEvent(
            taskId, message.Id, message.Role, message.Content,
            message.MessageType.ToString(), message.SenderId, message.CreatedAt));

        if (resumedFromQuestion || resumedFromVerification || resumedFromReviewEscalation)
        {
            await taskNotifier.SendLaneChangedAsync(task.ProjectId, new LaneChangedEvent(
                taskId, task.Title, fromLane.ToString(), task.CurrentLane.ToString(),
                task.Status.ToString(), DateTimeOffset.UtcNow));
        }

        return BoardOpOutcome.Success(message.Id);
    }

    /// <summary>
    /// Sets the repository the answer names (two or more: a cross-repo group, split on submit),
    /// returns the task to Drafting and — when the ticket source auto-submits — submits it. An
    /// answer that names no repository of the project is refused with the choices, and the task
    /// keeps waiting.
    /// </summary>
    private async Task<BoardOpOutcome> AnswerRepoQuestionAsync(
        TaskEntity task, string content, MessageType parsedType, string? senderUserId, string? locale, CancellationToken ct)
    {
        var repos = await projectsApi.ListReposAsync(task.ProjectId, ct);
        var picked = TrackerRepoQuestion.Match(content, repos);
        if (picked.Count == 0)
        {
            return BoardOpOutcome.Invalid(repos.Count == 0
                ? "This project has no repository yet — add one, then answer with its name."
                : $"Answer with one of this project's repositories: {string.Join(", ", repos.Select(r => r.Name))}.");
        }

        var question = await db.TaskMessages.AsNoTracking()
            .Where(m => m.TaskId == task.Id && m.MessageType == MessageType.Question)
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => m.Metadata)
            .FirstOrDefaultAsync(ct);
        var submitOnAnswer = TrackerRepoQuestion.ParseMetadata(question)?.SubmitOnAnswer == true;

        var message = new TaskMessage
        {
            OrgId = task.OrgId,
            TaskId = task.Id,
            Role = "user",
            Content = content,
            MessageType = parsedType == MessageType.Text ? MessageType.Answer : parsedType,
            SenderId = senderUserId,
        };
        db.TaskMessages.Add(message);

        task.RepoId = picked[0].Id;
        // A Story member spanning several repositories becomes one member per repository in
        // story order — what intake does for a sub-task the router placed in several.
        var storyMembers = task.GroupId is not null && task.StoryOrder is not null
            ? await StoryChain.SpanReposAsync(db, taskNumbers, task, [.. picked.Select(r => r.Id)], ct)
            : [];
        if (picked.Count >= 2 && task.GroupId is null)
        {
            var group = new TaskGroup
            {
                Id = Ulid.NewUlid(),
                OrgId = task.OrgId,
                ProjectId = task.ProjectId,
                MilestoneId = task.MilestoneId,
                Title = task.Title,
                Request = task.Description,
                SpansRepoIdsJson = SpannedRepoIds.Serialize([.. picked.Select(r => r.Id)]),
                Status = TaskGroupStatus.Splitting,
                CreatedByUserId = task.CreatedByUserId,
            };
            db.TaskGroups.Add(group);
            task.GroupId = group.Id;
        }
        var fromLane = task.CurrentLane;
        task.CurrentLane = Lane.Drafting;
        task.Status = TaskStatus.Draft;
        task.QuestionSourceLane = null;
        task.HasAgentWorking = false;
        await db.SaveChangesAsync(ct);

        await taskNotifier.SendNewMessageAsync(task.Id, new NewMessageEvent(
            task.Id, message.Id, message.Role, message.Content,
            message.MessageType.ToString(), message.SenderId, message.CreatedAt));
        await taskNotifier.SendLaneChangedAsync(task.ProjectId, new LaneChangedEvent(
            task.Id, task.Title, fromLane.ToString(), task.CurrentLane.ToString(),
            task.Status.ToString(), DateTimeOffset.UtcNow));

        if (submitOnAnswer)
        {
            if (providerReadiness.GetAnyProviderBlocker() is { } blocker)
            {
                await AddSystemNoteAsync(task, $"Repository set, but not submitted: {blocker}", ct);
            }
            else
            {
                await submissions.SubmitAsync(task, locale, ct);
                // The dependency on the member before holds each one in the queue.
                foreach (var added in storyMembers)
                    await submissions.SubmitAsync(added, locale, ct);
            }
        }

        foreach (var added in storyMembers)
        {
            await taskNotifier.SendTaskCreatedAsync(task.ProjectId, new TaskCreatedEvent(
                added.Id, added.Number, $"{(await projectsApi.GetProjectSummaryAsync(task.ProjectId, ct))?.Key}-{added.Number}",
                added.Title, added.Priority.ToString(), added.Type.ToString(), added.CurrentLane.ToString(),
                added.Status.ToString(), added.RepoId, picked.FirstOrDefault(r => r.Id == added.RepoId)?.Name,
                picked.FirstOrDefault(r => r.Id == added.RepoId)?.CardColor,
                System.Text.Json.JsonSerializer.Deserialize<List<string>>(added.Tags ?? "[]") ?? [], added.CreatedAt));
        }
        if (storyMembers.Count > 0)
            await AddSystemNoteAsync(task,
                $"Worked in {string.Join(", ", picked.Select(r => r.Name))}: one sub-task per repository, in this order, "
                + "on the story's branch.", ct);

        logger.LogInformation("Tracker task {TaskId} routed by its owner to {Repos}",
            task.Id, string.Join(", ", picked.Select(r => r.Name)));
        return BoardOpOutcome.Success(message.Id);
    }

    private async Task AddSystemNoteAsync(TaskEntity task, string text, CancellationToken ct)
    {
        var note = new TaskMessage
        {
            OrgId = task.OrgId,
            TaskId = task.Id,
            Role = "system",
            Content = text,
            MessageType = MessageType.SystemEvent,
        };
        db.TaskMessages.Add(note);
        await db.SaveChangesAsync(ct);
        await taskNotifier.SendNewMessageAsync(task.Id, new NewMessageEvent(
            task.Id, note.Id, note.Role, note.Content, note.MessageType.ToString(), note.SenderId, note.CreatedAt));
    }

    // ── code review ─────────────────────────────────────────────────────

    public async Task<BoardOpOutcome> SubmitCodeReviewAsync(
        Ulid orgId, Ulid taskId, string decision, string? body, IReadOnlyList<BoardReviewComment>? comments,
        CancellationToken ct)
    {
        var eventType = decision.ToUpperInvariant() switch
        {
            "APPROVE" or "APPROVED" => "APPROVE",
            "REQUEST_CHANGES" or "REJECT" or "REJECTED" => "REQUEST_CHANGES",
            "COMMENT" => "COMMENT",
            _ => null,
        };
        if (eventType is null)
            return BoardOpOutcome.Invalid("Decision must be 'approve', 'request_changes', or 'comment'.");
        if (eventType == "REQUEST_CHANGES" && string.IsNullOrWhiteSpace(body))
            return BoardOpOutcome.Invalid("Body is required when requesting changes.");
        if (body?.Length > 10_000)
            return BoardOpOutcome.Invalid("Body must be 10,000 characters or fewer.");

        var (context, taskExists, resolveError) = await CodeReviewHelpers.ResolveTaskContextDetailedAsync(
            db, projectsApi, taskId, orgId, ct);
        if (context is null)
            return taskExists
                ? BoardOpOutcome.Invalid(resolveError ?? "The pull request could not be resolved.")
                : BoardOpOutcome.NotFound("Task not found.");

        var task = context.Task;

        IReadOnlyList<GitHubNewReviewComment>? reviewComments = comments?.Count > 0
            ? comments.Select(c => new GitHubNewReviewComment(c.Path, c.Line, c.Body)).ToList()
            : null;

        // Posting to the forge is best-effort; Orkka's own record of the review is not.
        var forgeError = await TryPostForgeReviewAsync(context, eventType, body, reviewComments, taskId, ct);

        var fromLane = task.CurrentLane.ToString();
        var isApprove = eventType == "APPROVE";
        var isRequestChanges = eventType == "REQUEST_CHANGES";

        if (isApprove)
        {
            task.CurrentLane = Lane.Done;
            task.Status = TaskStatus.Completed;
            task.CompletedAt = DateTimeOffset.UtcNow;
            logger.LogInformation("Code review approved for task {TaskId}: moving to Done", taskId);
        }
        else if (isRequestChanges)
        {
            if (!string.IsNullOrWhiteSpace(body))
            {
                db.TaskMessages.Add(new TaskMessage
                {
                    TaskId = taskId,
                    OrgId = task.OrgId,
                    Role = "reviewer",
                    Content = body,
                    MessageType = MessageType.CodeReview
                });
            }
            task.CurrentLane = Lane.Developing;
            task.Status = TaskStatus.InQueue;
            task.HasAgentWorking = false;
            logger.LogInformation("Code review requested changes for task {TaskId}: re-queuing for agent", taskId);
        }

        await db.SaveChangesAsync(ct);

        await taskNotifier.SendCodeReviewedAsync(taskId, new CodeReviewedEvent(
            taskId, decision, body, DateTimeOffset.UtcNow));

        if (isApprove || isRequestChanges)
        {
            await taskNotifier.SendLaneChangedAsync(task.ProjectId, new LaneChangedEvent(
                taskId, task.Title, fromLane, task.CurrentLane.ToString(),
                task.Status.ToString(), DateTimeOffset.UtcNow));
        }

        return BoardOpOutcome.Success(warning: forgeError);
    }

    /// <summary>
    /// Posts the review to the forge, degrading an approve/request-changes the forge refuses into
    /// a plain comment. Returns null on success, or a human-readable reason the forge copy did
    /// not land. Never throws — the caller has already decided what the task's state should be.
    /// </summary>
    private async Task<string?> TryPostForgeReviewAsync(
        TaskPullRequestContext context, string eventType, string? body,
        IReadOnlyList<GitHubNewReviewComment>? comments, Ulid taskId, CancellationToken ct)
    {
        try
        {
            var result = await pullRequests.CreateReviewAsync(
                context.RepoId, context.PrNumber,
                eventType, body, comments, ct);

            if (!result.IsFailure)
                return null;

            if (eventType == "COMMENT")
            {
                logger.LogWarning("Forge review comment failed for task {TaskId}: {Error}", taskId, result.Error);
                return result.Error ?? "The review could not be posted to the repository.";
            }

            logger.LogWarning(
                "Forge review ({Event}) failed for task {TaskId}: {Error} — retrying as COMMENT",
                eventType, taskId, result.Error);

            var prefix = eventType == "APPROVE" ? "✅ Approved in Orkka." : "❌ Changes requested in Orkka.";
            var fallback = await pullRequests.CreateReviewAsync(
                context.RepoId, context.PrNumber,
                "COMMENT", string.IsNullOrWhiteSpace(body) ? prefix : $"{prefix}\n\n{body}",
                comments, ct);

            return fallback.IsFailure
                ? result.Error ?? "The review could not be posted to the repository."
                : null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Forge review threw for task {TaskId}", taskId);
            return $"The review was recorded in Orkka, but posting it to the repository failed: {ex.Message}";
        }
    }

    // ── merge ───────────────────────────────────────────────────────────

    public async Task<BoardOpOutcome> MergePullRequestAsync(Ulid orgId, Ulid taskId, CancellationToken ct)
    {
        var (context, taskExists, resolveError) = await CodeReviewHelpers.ResolveTaskContextDetailedAsync(
            db, projectsApi, taskId, orgId, ct);
        if (context is null)
            return taskExists
                ? BoardOpOutcome.Invalid(resolveError ?? "The pull request could not be resolved.")
                : BoardOpOutcome.NotFound("Task not found.");

        var task = context.Task;
        var fromLane = task.CurrentLane.ToString();

        var mergeResult = await pullRequests.MergeAsync(context.RepoId, context.PrNumber, ct);

        if (mergeResult.IsFailure)
        {
            logger.LogWarning("Failed to merge PR for task {TaskId}: {Error}", taskId, mergeResult.Error);

            var reason = mergeResult.Error ?? "Merge failed";
            reason = reason.Length > 1000 ? reason[..1000] : reason;

            // Conflict failures hand off to the automatic resolver — the click is the merge
            // intent, so the platform combines the branches and finishes the merge instead of
            // parking the failure on the human.
            var armed = await MergeAutoResolution.TryArmAsync(
                db, pullRequests, context.RepoId,
                context.PrNumber, task, reason, logger, ct);

            // The click did not land the work, so the task is not done — same rule the
            // sweep applies. Runs outside the change-detection below: a repeat click on the
            // same failure still has a lane to correct.
            var demotedFrom = MergeFailureLane.DemoteFromDone(task);

            if (task.MergeFailureReason != reason)
            {
                task.MergeFailureReason = reason;
                var failureMessage = new TaskMessage
                {
                    TaskId = task.Id,
                    OrgId = task.OrgId,
                    Role = "system",
                    Content = armed == MergeAutoResolution.ArmResult.Armed
                        ? MergeAutoResolution.StartedMessage
                        : $"Merge failed: {reason}",
                    MessageType = armed == MergeAutoResolution.ArmResult.Armed ? MessageType.Text : MessageType.Error
                };
                db.TaskMessages.Add(failureMessage);
                await db.SaveChangesAsync(ct);

                await taskNotifier.SendTaskMergeFailedAsync(task.ProjectId,
                    new TaskMergeFailedEvent(taskId, reason, DateTimeOffset.UtcNow));
                await taskNotifier.SendNewMessageAsync(taskId, new NewMessageEvent(
                    taskId, failureMessage.Id, failureMessage.Role, failureMessage.Content,
                    failureMessage.MessageType.ToString(), failureMessage.SenderId, failureMessage.CreatedAt));
            }
            else if (demotedFrom is not null)
            {
                await db.SaveChangesAsync(ct);
            }

            if (demotedFrom is { } demotedLane)
                await taskNotifier.SendLaneChangedAsync(task.ProjectId, new LaneChangedEvent(
                    taskId, task.Title, demotedLane.ToString(), task.CurrentLane.ToString(),
                    task.Status.ToString(), DateTimeOffset.UtcNow));

            // Armed (or already in flight) is the intended path working, not a failure —
            // the resolver combines the branches and the merge lands on a worker cycle.
            return armed != MergeAutoResolution.ArmResult.NotArmed
                ? BoardOpOutcome.ResolutionInProgress(task.Id, MergeAutoResolution.StartedMessage)
                : BoardOpOutcome.Conflict(mergeResult.Error!);
        }

        task.CurrentLane = Lane.Done;
        task.Status = TaskStatus.Completed;
        task.CompletedAt = DateTimeOffset.UtcNow;
        task.IsMerged = true;
        // A remote's "merge" publishes the work as a forge pull request — record it in the same
        // save as IsMerged. Without it the task read as merged with no forge PR, which the
        // tracker write-back takes for a folder landing: the ticket jumped straight to the
        // "Merged" status (Done) while its pull request had only just been opened.
        if (mergeResult.Value!.ForgeUrl is { } forgeUrl)
            task.PublishedPrUrl = forgeUrl;
        var hadFailure = task.MergeFailureReason is not null;
        // Mirror MarkTaskMergedAsync: the merge landed, so EVERY piece of merge state comes
        // off — a surviving kind or armed strategy kept steering the merge page (and the
        // sweep) for a task that was already done.
        task.MergeFailureReason = null;
        task.MergeFailureKind = null;
        task.MergeConflictsJson = null;
        task.MergeResolutionStrategy = null;
        task.MergeResolutionGuidance = null;
        task.MergeResolutionStartedAt = null;
        task.QueuedMergeStrategy = null;
        task.QueuedMergeGuidance = null;

        await db.SaveChangesAsync(ct);

        if (hadFailure)
        {
            await taskNotifier.SendTaskMergeFailedAsync(task.ProjectId,
                new TaskMergeFailedEvent(taskId, null, DateTimeOffset.UtcNow));
        }

        logger.LogInformation("PR merged for task {TaskId}, moved to Done", taskId);

        await taskNotifier.SendLaneChangedAsync(task.ProjectId, new LaneChangedEvent(
            taskId, task.Title, fromLane, Lane.Done.ToString(),
            TaskStatus.Completed.ToString(), DateTimeOffset.UtcNow));

        // Same derivation as MarkTaskMergedAsync: a milestone task's PR lands on the
        // integration branch, not the default — deploy-side consumers key on this, and the
        // old 5-arg publish defaulted it to true for every human-clicked merge.
        var landedOnDefault = task.MilestoneId is not { } milestoneId
            || !await db.Milestones.AsNoTracking()
                .AnyAsync(m => m.Id == milestoneId && m.BranchName != null, ct);

        await eventBus.PublishAsync(new Orkka.SharedKernel.Events.Catalog.TaskMerged(
            task.Id, task.OrgId, task.ProjectId, task.RepoId, mergeResult.Value!.MergeCommitSha,
            landedOnDefault), ct);

        return BoardOpOutcome.Success(taskId);
    }

    // ── attention list ──────────────────────────────────────────────────

    public async Task<IReadOnlyList<AttentionTaskSummary>> ListAttentionTasksAsync(
        Ulid orgId, Ulid? projectId, int take, CancellationToken ct)
    {
        var statuses = AttentionStatuses.Actionable.ToList();
        var query = db.Tasks.AsNoTracking()
            .Where(t => t.OrgId == orgId && !t.IsArchived && statuses.Contains(t.Status));
        if (projectId is { } pid)
            query = query.Where(t => t.ProjectId == pid);

        var rows = await query
            .OrderByDescending(t => t.UpdatedAt)
            .Take(Math.Clamp(take, 1, 200))
            .Select(t => new { t.Id, t.Number, t.Title, t.CurrentLane, t.Status, t.ProjectId, t.UpdatedAt })
            .ToListAsync(ct);
        if (rows.Count == 0) return [];

        var projectIds = rows.Select(r => r.ProjectId).Distinct().ToList();
        var projects = new Dictionary<Ulid, ProjectSummary>();
        foreach (var id in projectIds)
        {
            var summary = await projectsApi.GetProjectSummaryAsync(id, ct);
            if (summary is not null) projects[id] = summary;
        }

        return rows.Select(r =>
        {
            var p = projects.GetValueOrDefault(r.ProjectId);
            return new AttentionTaskSummary(
                r.Id, r.Number, p is null ? $"#{r.Number}" : $"{p.Key}-{r.Number}", r.Title,
                r.CurrentLane.ToString(), r.Status.ToString(), r.ProjectId, p?.Slug ?? "", r.UpdatedAt);
        }).ToList();
    }
}
