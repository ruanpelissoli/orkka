namespace Orkka.Modules.Execution.Orchestration;

using Orkka.Modules.Execution.Agents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Interactive development sessions — see .claude/docs/INTERACTIVE.md.
///
/// This is a <see cref="Lane.Developing"/> phase whose turns are triggered by a human instead of
/// by the queue sweep, so it lives on <see cref="TaskOrchestrator"/> rather than in a service of
/// its own: it needs the same worktree setup, the same streaming/retry wrapper, the same
/// execution bookkeeping and the same provider resolution, all of which are private here. A
/// partial file keeps the new code separate without a speculative extraction that would
/// destabilise the pipeline for no functional gain.
/// </summary>
public sealed partial class TaskOrchestrator
{
    /// <summary>
    /// How often an idle session checks for a new human message.
    ///
    /// This is a poll against one indexed table by the worker that already holds the session's
    /// lease — not a distributed sweep — so it can be far tighter than the queue's interval.
    /// Turn latency is bounded by this, and a human waiting on a reply notices seconds.
    /// </summary>
    private static readonly TimeSpan InteractiveIdlePoll = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Drives one interactive session from pickup until it is finished, abandoned, or this
    /// worker stops. Called by the queue processor once it has the task's lease; the lease is
    /// heartbeat-extended for as long as this runs, which is what pins every turn of the session
    /// to one machine — the CLI session file lives wherever the previous turn ran, so losing
    /// affinity would cold-boot and re-pay full context on every turn the user takes.
    /// </summary>
    public async Task RunInteractiveSessionAsync(Ulid taskId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var tasksApi = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var projectsApi = scope.ServiceProvider.GetRequiredService<IProjectsApi>();
        var agentsApi = scope.ServiceProvider.GetRequiredService<IAgentsApi>();

        var task = await tasksApi.GetTaskSnapshotAsync(taskId, ct);
        if (task is null || !task.IsInteractive || task.InteractiveSessionStartedAt is null)
        {
            // Finished or abandoned, possibly while its backend was away — drop the deferral clock
            // so it cannot outlive the session it belonged to.
            _deferredSince.TryRemove(taskId, out _);

            logger.LogInformation(
                "Task {TaskId} is not a live interactive session — nothing to drive", taskId);
            return;
        }

        // Any prompt still pending belongs to a run that is already gone — this pickup exists
        // precisely because nothing was driving the session. Nobody can answer them, and a live
        // approval card for a dead run is an invitation to click something that does nothing.
        await ExpirePendingApprovalsAsync(scope, taskId, ct);

        var route = executionRouter.Resolve(task);
        var project = await projectsApi.GetProjectSummaryAsync(task.ProjectId, ct);
        var repo = task.RepoId is { } repoId ? await projectsApi.GetRepoAsync(repoId, ct) : null;
        var agent = await agentAssigner.AssignAgentAsync(agentsApi, task, Lane.Developing, ct);

        // Once per pickup, not once per turn — idempotent, so a session resumed after a worker
        // restart reuses the branch and worktree it already had. A conflicted base merge is left
        // staged: the human is right here, driving, and is the best possible resolver.
        var workspace = await SetupGitWorkspaceAsync(
            scope, tasksApi, task, repo, route, ct, BaseSyncMode.AgentResolves);
        if (workspace.WorktreePath is null)
        {
            var reason = workspace.Error
                ?? "No worktree available — cannot start a session without a repository.";

            // The backend being away is a transient condition, not a broken session. Pickup now
            // happens the instant a worker starts, which is seconds BEFORE the runner reconnects
            // to it — closing here destroyed a live session over a six-second reconnect, and the
            // human's only clue was a chat that had quietly ended. Leave it live; the sweep
            // retries within seconds and it self-heals.
            if (!route.IsBackendLive)
            {
                await DeferUnreachableSessionAsync(tasksApi, taskId, route, ct);
                return;
            }

            // A reachable backend that still cannot produce a worktree is a real failure (missing
            // credentials, unreadable repo) and will not fix itself. Say so and end the session,
            // rather than leaving one open that can never do anything.
            logger.LogError("Interactive session {TaskId} could not prepare a workspace: {Error}", taskId, reason);
            await PostSessionMessageAsync(tasksApi, taskId, MessageType.Error,
                $"This session could not start: {reason}", ct);
            await tasksApi.CloseInteractiveSessionAsync(taskId, CancellationToken.None);
            return;
        }

        // Reachable again — forget any deferral clock so a later outage starts fresh.
        _deferredSince.TryRemove(taskId, out _);

        task = task with { BranchName = workspace.BranchName ?? task.BranchName, ReviewBaseSha = workspace.ReviewBaseSha ?? task.ReviewBaseSha };

        logger.LogInformation(
            "Interactive session {TaskId} ready on branch {Branch} (agent {Agent})",
            taskId, task.BranchName, agent?.Name ?? "unassigned");

        await PostSessionMessageAsync(tasksApi, taskId, MessageType.SystemEvent,
            $"Session ready on branch `{task.BranchName}`.", ct);

        // True once a turn has run on this pickup, so the first turn pays for full context and
        // the rest ride the warm CLI session.
        var ranATurnHere = false;

        // Which agent answered the previous turn. A session is long-lived and the task page
        // exposes an agent picker while it runs, so the roster is re-read every turn (below) —
        // this is what notices a change, because a changed persona invalidates the warm session:
        // resuming would keep answering as the agent the user just switched away from.
        Ulid? lastAgentId = null;

        // The idle wait also listens for the worker drain: a deploy lets a turn that is running
        // finish, but must not hold this pod alive for a session that is merely waiting on the
        // human — that one is released here and the pickup sweep hands it to the replacement.
        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct, drain.Token);

        while (!ct.IsCancellationRequested && !drain.IsDraining)
        {
            var current = await tasksApi.GetTaskSnapshotAsync(taskId, ct);

            // Abandoned (marker cleared elsewhere), deleted, or reset out from under us.
            if (current is null || current.InteractiveSessionStartedAt is null)
            {
                logger.LogInformation("Interactive session {TaskId} closed — ending the loop", taskId);
                return;
            }

            var finishing = current.InteractiveFinishRequestedAt is not null;

            var pending = finishing
                ? []
                : await tasksApi.ListUnansweredInteractiveMessagesAsync(
                    taskId, current.InteractiveAnsweredThroughAt, ct);

            // Nothing to do — go back to sleep BEFORE resolving an agent. Resolving above this
            // check ran an assignment (and its write, and a log line) every two seconds for the
            // whole life of an idle session.
            if (!finishing && pending.Count == 0)
            {
                try { await Task.Delay(InteractiveIdlePoll, idleCts.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }
                continue;
            }

            // Re-read per turn rather than captured at pickup: a session can run for an hour and
            // the task page lets the user repoint it at another agent (or change that agent's
            // model) mid-conversation. Captured once, none of that took effect until the session
            // was re-picked-up, which nothing in the UI suggests.
            var turnAgent = await agentAssigner.AssignAgentAsync(agentsApi, current, Lane.Developing, ct);

            if (finishing)
            {
                await FinishInteractiveSessionAsync(
                    scope, tasksApi, current with { BranchName = task.BranchName },
                    project, repo, turnAgent, route, workspace.WorktreePath,
                    sessionIsWarm: ranATurnHere && turnAgent?.Id == lastAgentId, ct);
                return;
            }

            if (ranATurnHere && turnAgent?.Id != lastAgentId)
            {
                logger.LogInformation(
                    "Interactive session {TaskId} switched agent to {Agent} — starting a fresh CLI session",
                    taskId, turnAgent?.Name ?? "unassigned");
            }

            // Named from what was actually asked, before the turn runs rather than after: a turn
            // can take minutes, and "Interactive session" on the board for all of it tells nobody
            // — including the person deciding whether to interrupt — what it is about. One fast
            // utility call is cheap next to the turn it precedes.
            await TryTitleSessionAsync(scope, tasksApi, current, pending, ct);

            await RunInteractiveTurnAsync(
                scope, tasksApi, current with { BranchName = task.BranchName },
                project, repo, turnAgent, route, workspace.WorktreePath, pending,
                sessionIsWarm: ranATurnHere && turnAgent?.Id == lastAgentId, ct);

            lastAgentId = turnAgent?.Id;
            ranATurnHere = true;
        }

        // Reaching here means THIS worker is stopping or draining (deploy, shutdown), not that
        // the session ended. Deliberately no CloseInteractiveSessionAsync: the session stays live
        // and the pickup sweep hands it to another worker — or to this one after a restart.
        // Closing here would kill every open session on every deploy.
        logger.LogInformation(
            "Interactive session {TaskId} released — worker is stopping or draining, session remains open", taskId);
    }

    /// <summary>
    /// One turn: everything the human said since the last turn goes to the agent, the reply
    /// streams back into the task timeline, and the watermark advances to the newest message the
    /// turn actually consumed.
    /// </summary>
    private async Task RunInteractiveTurnAsync(
        IServiceScope scope, ITasksApi tasksApi, TaskSnapshot task, ProjectSummary? project,
        RepoSummary? repo, AgentSummary? agent, ExecutionRoute route, string worktreePath,
        IReadOnlyList<InteractiveUserMessage> pending, bool sessionIsWarm, CancellationToken ct)
    {
        // Captured BEFORE the run: a message the human types while the agent is working is older
        // than the run that finishes after it, so stamping the completion time instead would
        // silently mark that follow-up as answered and never reply to it.
        var answeredThrough = pending[^1].CreatedAt;

        var start = await tasksApi.StartExecutionAsync(
            task.Id, Lane.Developing, agent?.Id, moveTaskToLane: false, setStartedAt: false, ct);
        if (start is null)
        {
            logger.LogWarning("Task {TaskId} vanished while starting an interactive turn", task.Id);
            return;
        }
        var executionId = start.ExecutionId;

        var agentsApi = scope.ServiceProvider.GetRequiredService<IAgentsApi>();

        // Logged per turn, matching the batch phases. Without it a session is silent between
        // pickup and its next lane change, so a turn that fails inside the invoker looks
        // identical to a loop that never noticed the message at all — which is exactly the
        // wrong question to be left asking.
        logger.LogInformation(
            "Interactive turn on task {TaskId} — Agent: {Agent}, Messages: {Count}, Warm: {Warm}",
            task.Id, agent?.Name ?? "unassigned", pending.Count, sessionIsWarm);

        // A resumable session means the CLI transcript already holds the persona, the repo
        // context and everything said earlier — the turn then costs only the new messages.
        var resume = route.Invoker.SupportsSessionResume && sessionIsWarm
            ? await tasksApi.GetLatestExecutionSessionAsync(task.Id, Lane.Developing, ct)
            : null;

        var memories = agent is not null
            ? await LoadMemoriesSectionAsync(agentsApi, agent, task, _pipelineToolsEnabled, ct)
            : "";
        // History stops where the messages this turn delivers as authoritative begin. Included,
        // they appear twice — once inside the untrusted-content fence — and a session was
        // observed refusing its own operator's `rm` as a suspected prompt injection, citing the
        // fenced copy of the very instruction it had just been handed.
        var conversation = resume is null
            ? await BuildConversationSectionAsync(
                tasksApi, task.Id, since: null, ct, until: pending[0].CreatedAt)
            : "";

        var siblingRepos = ConcatSections(
            await LoadSiblingReposSectionAsync(scope, project, repo, ct),
            await BuildStoryContextSectionAsync(scope, tasksApi, task, ct));
        var prompt = BuildInteractiveTurnPrompt(
            task, project, repo, agent, memories, conversation, pending, coldStart: resume is null, siblingRepos);

        var (runModel, runProvider) = await ResolveRunModelAsync(scope, tasksApi, task, agent, project, route, ct);
        var request = new AgentRequest(
            SystemPrompt: prompt.System,
            UserMessage: prompt.User,
            WorktreePath: worktreePath,
            Provider: runProvider,
            Model: runModel,
            MaxTurns: _budgets.Developing.MaxTurns,
            MaxToolCalls: _budgets.Developing.MaxToolCalls,
            MaxWallClockSeconds: _budgets.Developing.MaxWallClockSeconds,
            MaxCostUsd: _budgets.Developing.MaxCostUsd,
            Phase: AgentPhase.Developing,
            AgentName: agent?.Name ?? "agent",
            AgentRole: agent?.Role.ToString() ?? "developer",
            OrgId: task.OrgId,
            ProjectId: task.ProjectId,
            TaskId: task.Id,
            AgentId: agent?.Id,
            ExecutionId: executionId,
            ResumeSessionId: resume?.SessionId,
            // A human is present for every command of this run, so Bash goes through the guard
            // that can ask them rather than the one that can only allow or refuse.
            Interactive: true);

        var liveDiff = repo is not null && !string.IsNullOrEmpty(task.BranchName)
            ? new LiveDiffContext(task.OrgId, repo.Id, task.Id, repo.DefaultBranch, task.BranchName, Lane.Developing, executionId, task.ReviewBaseSha)
            : null;

        var outcome = await StreamAgentRunAsync(
            task.Id, agent, request, route, persistText: true, RunTimeoutFor(project?.Settings), ct, liveDiff);

        // Session file gone (worker moved, cleanup) and nothing streamed yet — rerun cold with
        // the full context, exactly as the batch Developing phase does.
        if (resume is not null && outcome.Error is not null && !outcome.ReceivedOutput)
        {
            logger.LogInformation(
                "Interactive session {TaskId} could not resume CLI session {SessionId} ({Error}) — running cold",
                task.Id, resume.SessionId, outcome.Error);

            var cold = BuildInteractiveTurnPrompt(
                task, project, repo, agent, memories,
                await BuildConversationSectionAsync(tasksApi, task.Id, since: null, ct),
                pending, coldStart: true, siblingRepos);

            request = request with
            {
                SystemPrompt = cold.System,
                UserMessage = cold.User,
                ResumeSessionId = null,
            };
            outcome = await StreamAgentRunAsync(
            task.Id, agent, request, route, persistText: true, RunTimeoutFor(project?.Settings), ct, liveDiff);
        }

        if (outcome.Error is not null)
        {
            // Only the execution row fails. A failed turn must NOT block the task or park it the
            // way a batch phase failure does — the human is right there and can rephrase, and
            // tearing the session down under them would be the worst possible response to a
            // transient model error.
            await tasksApi.MarkExecutionFailedAsync(task.Id, executionId, outcome.Error, CancellationToken.None);
            await PostSessionMessageAsync(tasksApi, task.Id, MessageType.Error,
                $"That turn failed: {outcome.Error}\n\nThe session is still open — try again or rephrase.",
                CancellationToken.None);
        }
        else
        {
            await CompleteExecutionAsync(
                scope, tasksApi, task, executionId, project, agent, outcome,
                TruncateAgentLog(outcome.FullText), route, ct);
        }

        // Advanced on BOTH paths, and with CancellationToken.None: leaving the watermark behind
        // after a failure would replay the same message on the next tick forever, burning tokens
        // on a loop the human never asked for.
        await tasksApi.SetInteractiveAnsweredThroughAsync(
            task.Id, answeredThrough, CancellationToken.None);
    }

    /// <summary>
    /// The human pressed Finish: one last agent run to commit and push, then the task hands off
    /// to the normal pipeline. Everything after this point — PR creation, Sentinel's review,
    /// merge — is the machinery that already exists, which is why finishing is nearly free.
    /// </summary>
    private async Task FinishInteractiveSessionAsync(
        IServiceScope scope, ITasksApi tasksApi, TaskSnapshot task, ProjectSummary? project,
        RepoSummary? repo, AgentSummary? agent, ExecutionRoute route, string worktreePath,
        bool sessionIsWarm, CancellationToken ct)
    {
        logger.LogInformation("Finishing interactive session {TaskId} on branch {Branch}",
            task.Id, task.BranchName);

        await PostSessionMessageAsync(tasksApi, task.Id, MessageType.SystemEvent,
            "Wrapping up the session — committing and pushing.", ct);

        var start = await tasksApi.StartExecutionAsync(
            task.Id, Lane.Developing, agent?.Id, moveTaskToLane: false, setStartedAt: false, ct);

        var wrapUpFailed = false;
        if (start is not null)
        {
            var resume = route.Invoker.SupportsSessionResume && sessionIsWarm
                ? await tasksApi.GetLatestExecutionSessionAsync(task.Id, Lane.Developing, ct)
                : null;

            var (runModel, runProvider) = await ResolveRunModelAsync(scope, tasksApi, task, agent, project, route, ct);
            var request = new AgentRequest(
                SystemPrompt: BuildInteractiveFinishSystemPrompt(task, project, agent),
                UserMessage: BuildInteractiveFinishUserMessage(task),
                WorktreePath: worktreePath,
                Provider: runProvider,
                Model: runModel,
                MaxTurns: _budgets.Developing.MaxTurns,
                MaxToolCalls: _budgets.Developing.MaxToolCalls,
                MaxWallClockSeconds: _budgets.Developing.MaxWallClockSeconds,
                MaxCostUsd: _budgets.Developing.MaxCostUsd,
                Phase: AgentPhase.Developing,
                AgentName: agent?.Name ?? "agent",
                AgentRole: agent?.Role.ToString() ?? "developer",
                OrgId: task.OrgId,
                ProjectId: task.ProjectId,
                TaskId: task.Id,
                AgentId: agent?.Id,
                ExecutionId: start.ExecutionId,
                ResumeSessionId: resume?.SessionId,
                // Deliberately NOT interactive, unlike a normal turn. Pressing Finish IS the
                // human's approval to commit and push — re-asking for the `git push` this run
                // exists to perform would prompt them for the thing they just clicked, and with
                // nobody watching the session any more it would simply hang until it expired.
                Interactive: false);

            var outcome = await StreamAgentRunAsync(
            task.Id, agent, request, route, persistText: true, RunTimeoutFor(project?.Settings), ct);

            if (outcome.Error is not null)
            {
                // The session still ends — the human asked it to. What changes is that the work
                // may not have reached the remote, which the timeline has to say out loud rather
                // than leaving them to discover an empty PR.
                wrapUpFailed = true;
                await tasksApi.MarkExecutionFailedAsync(
                    task.Id, start.ExecutionId, outcome.Error, CancellationToken.None);
                await PostSessionMessageAsync(tasksApi, task.Id, MessageType.Error,
                    $"The final commit/push run failed: {outcome.Error}\n\n" +
                    "Any uncommitted work is still in the worktree — check the branch before merging.",
                    CancellationToken.None);
            }
            else
            {
                await CompleteExecutionAsync(
                    scope, tasksApi, task, start.ExecutionId, project, agent, outcome,
                    TruncateAgentLog(outcome.FullText), route, ct);

                // The platform pushes from the runner so Finish really ships the branch,
                // whatever the agent's own push did.
                if (repo is not null)
                {
                    var finishPushError = await EnsureBranchPushedAfterRunAsync(route, task, repo, ct);
                    if (finishPushError is not null)
                    {
                        // The session still hands off — but the timeline must say the branch
                        // never reached the forge, or the reader discovers it as a PR that
                        // cannot be created.
                        await PostSessionMessageAsync(tasksApi, task.Id, MessageType.Error,
                            $"The branch could not be pushed to the forge: {finishPushError}\n\n" +
                            "The session's work is still in the worktree on the machine that ran it. " +
                            "The pull request cannot open until the branch is pushed.",
                            CancellationToken.None);
                    }
                }

                // The cumulative diff of everything the session changed, captured while the
                // machine that drove it still holds the worktree. Best-effort — a lost snapshot
                // degrades a history view, never a finished session.
                if (repo is not null && !string.IsNullOrEmpty(task.BranchName))
                {
                    try
                    {
                        var sessionDiff = await route.Git.GetDiffAsync(
                            task.OrgId, repo.Id, task.Id, repo.DefaultBranch,
                            maxChars: CodeChangeSnapshotRules.MaxDiffChars,
                            headBranch: task.BranchName, ct: ct);

                        if (sessionDiff.IsSuccess && !string.IsNullOrWhiteSpace(sessionDiff.Value))
                        {
                            await tasksApi.SaveCodeChangeSnapshotAsync(
                                task.Id, start.ExecutionId, repo.Id, Lane.Developing,
                                task.BranchName!, repo.DefaultBranch, sessionDiff.Value!,
                                isTruncated: sessionDiff.Value!.Length >= CodeChangeSnapshotRules.MaxDiffChars,
                                ct: ct);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex,
                            "Code-change snapshot capture failed for interactive task {TaskId}", task.Id);
                    }
                }
            }
        }

        // Close BEFORE handing off, so the pickup sweep cannot re-claim a task that is now the
        // pipeline's. Idempotent, and it also clears the finish request.
        await tasksApi.CloseInteractiveSessionAsync(task.Id, CancellationToken.None);
        await ExpirePendingApprovalsAsync(scope, task.Id, CancellationToken.None);

        // A session can be ended at any time, including one that changed nothing — someone opened
        // it to ask a question, or explored and decided against the idea. Routing that into code
        // review would park an empty task in front of a reviewer and leave the awaiting-PR sweep
        // retrying a branch that was never pushed.
        //
        // The pushed branch is the test, not the agent's report of what it did: it is the same
        // thing the PR would be built from, and it costs one cheap remote lookup. Tri-state:
        // null means the check itself failed, which must never be read as "nothing shipped".
        bool? pushed = repo is not null && !string.IsNullOrWhiteSpace(task.BranchName)
            ? await BranchWasPushedAsync(route, task, repo, CancellationToken.None)
            : false;

        if (wrapUpFailed && pushed != true)
        {
            // The wrap-up errored and nothing verifiably reached the remote. This used to fall
            // through to the "no code changes → Done" path — directly contradicting the failure
            // message just posted, and leaving any local commits in a worktree the GC deletes.
            // Park for the human instead: WaitingUserVerification is resumable (a message or the
            // approve button moves it on), and the worktree survives.
            logger.LogWarning(
                "Interactive session {TaskId} wrap-up failed with nothing verifiably pushed — parking for verification",
                task.Id);

            await PostSessionMessageAsync(tasksApi, task.Id, MessageType.Error,
                "The session ended, but the wrap-up run failed before the work could be confirmed "
                + "on the remote. The worktree is preserved — check the branch, then approve to "
                + "continue or send a message to retry.",
                CancellationToken.None);

            var parked = await tasksApi.CompleteDevelopingPhaseAsync(
                task.Id, skipCodeReview: false, routeToTesting: false,
                parkForUserVerification: true, ct: CancellationToken.None);

            if (parked is not null)
            {
                await taskNotifier.SendLaneChangedAsync(task.ProjectId, new LaneChangedEvent(
                    task.Id, task.Title, parked.FromLane.ToString(), parked.NewLane.ToString(),
                    parked.NewStatus.ToString(), DateTimeOffset.UtcNow));
            }

            return;
        }

        if (pushed == false)
        {
            logger.LogInformation(
                "Interactive session {TaskId} ended with nothing pushed — closing without review",
                task.Id);

            await PostSessionMessageAsync(tasksApi, task.Id, MessageType.SystemEvent,
                "Session ended with no code changes — nothing to push or review.",
                CancellationToken.None);

            // CompleteTaskWithoutPrAsync rather than a skip-review develop transition: it also
            // clears BranchName, which is what keeps the awaiting-PR sweep from later opening —
            // and auto-merging — a PR for a Done task off a branch that was never pushed.
            await tasksApi.CompleteTaskWithoutPrAsync(task.Id, CancellationToken.None);
            return;
        }

        // Shipped (or the check was inconclusive — the safe direction is review, where the
        // awaiting-PR sweep can still push and open the PR, or escalate recoverably).
        //
        // No QA and no user-verification park: the human just spent the whole session verifying
        // interactively. Code review is the one gate that still earns its keep, and the PR is
        // created by the existing awaiting-PR sweep once the task lands in CodeReview/Done.
        var transition = await tasksApi.CompleteDevelopingPhaseAsync(
            task.Id, task.SkipCodeReview, routeToTesting: false,
            parkForUserVerification: false, ct: CancellationToken.None);

        if (transition is not null)
        {
            await taskNotifier.SendLaneChangedAsync(task.ProjectId, new LaneChangedEvent(
                task.Id, task.Title, transition.FromLane.ToString(), transition.NewLane.ToString(),
                transition.NewStatus.ToString(), DateTimeOffset.UtcNow));
        }

        logger.LogInformation(
            "Interactive session {TaskId} finished — task moved to {Lane}",
            task.Id, transition?.NewLane.ToString() ?? "(unchanged)");
    }

    /// <summary>
    /// When each task's backend first went unreachable, so a deferral can be announced once and
    /// bounded rather than repeating silently on every sweep.
    ///
    /// In memory, and static because the orchestrator is a singleton per process. A worker
    /// restart resets the clock, which only makes the grace period more generous — the failure
    /// this guards against is a session sitting silent for an hour, not a minute of slack.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Ulid, DateTimeOffset>
        _deferredSince = new();

    /// <summary>
    /// How long a session waits for its backend before giving up. Generous: a runner restart,
    /// a laptop lid, or a hotel wifi drop should all be survivable without losing the
    /// conversation. Past it, the session has been unusable long enough that saying so beats
    /// leaving it open.
    /// </summary>
    private static readonly TimeSpan UnreachableBackendGrace = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The session's execution backend is away — on Orkka Local and hybrid tasks, that means the
    /// user's runner is not connected.
    ///
    /// This used to log and return. It is a transient condition, so not closing the session was
    /// right, but silence was not: pickup retries every few seconds, so a runner that stays away
    /// leaves a session reading "Session live / preparing the workspace" indefinitely with no
    /// branch, no reply, and nothing anywhere telling the human their runner is the problem. Say
    /// it once, then bound the wait.
    /// </summary>
    private async Task DeferUnreachableSessionAsync(
        ITasksApi tasksApi, Ulid taskId, ExecutionRoute route, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        if (_deferredSince.TryAdd(taskId, now))
        {
            logger.LogInformation(
                "Interactive session {TaskId} deferred — its runner is not reachable",
                taskId);

            await PostSessionMessageAsync(tasksApi, taskId, MessageType.SystemEvent,
                "Waiting for your runner to reconnect — this session runs on your machine. "
                + "Start it with `./orkka start` if it is not running. Your messages are "
                + "saved and the session resumes on its own once it is back.", ct);
            return;
        }

        var since = _deferredSince.GetValueOrDefault(taskId, now);
        if (now - since < UnreachableBackendGrace)
            return;

        // Long enough. An open session nobody can drive is worse than an honest ending — the work
        // is on its branch either way, and a closed session can be reopened.
        logger.LogWarning(
            "Closing interactive session {TaskId} — backend unreachable for {Minutes:F0} minutes",
            taskId, (now - since).TotalMinutes);

        await PostSessionMessageAsync(tasksApi, taskId, MessageType.Error,
            $"This session ended because your runner did not reconnect within "
            + $"{UnreachableBackendGrace.TotalMinutes:F0} minutes. Anything already committed is "
            + "safe on the branch — reconnect the runner and start a new session to continue.",
            CancellationToken.None);

        await tasksApi.CloseInteractiveSessionAsync(taskId, CancellationToken.None);
        _deferredSince.TryRemove(taskId, out _);
    }

    /// <summary>How much of the opening message the titler reads. A title is six words; anything
    /// past this is paste, and paying for it on every session adds up.</summary>
    private const int TitleSourceCharCap = 2_000;

    /// <summary>Titles are short by construction, and a long one breaks board cards.</summary>
    private const int MaxSessionTitleLength = 80;

    /// <summary>
    /// Names an unnamed session from the human's opening message.
    ///
    /// Only ever runs while the title is still the placeholder, and the write re-checks that in
    /// its WHERE clause — a title the user typed is never replaced. Best-effort throughout: a
    /// session with a generic name works fine, so no failure here may cost the user their turn.
    /// </summary>
    private async Task TryTitleSessionAsync(
        IServiceScope scope, ITasksApi tasksApi, TaskSnapshot task,
        IReadOnlyList<InteractiveUserMessage> pending, CancellationToken ct)
    {
        if (pending.Count == 0
            || !string.Equals(task.Title, InteractiveSessionDefaults.UntitledSession, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            var runnerUtility = scope.ServiceProvider
                .GetRequiredService<Orkka.SharedKernel.Ai.IUserRunnerUtility>();
            var options = scope.ServiceProvider
                .GetRequiredService<IOptions<Orkka.SharedKernel.Ai.AnthropicOptions>>().Value;

            var opening = pending[0].Content;
            if (opening.Length > TitleSourceCharCap)
                opening = opening[..TitleSourceCharCap];

            var request = new Orkka.SharedKernel.Ai.AnthropicRequest
            {
                Model = options.FastUtilityModel,
                MaxTokens = 40,
                System =
                    "You title work items. Given the first thing a developer asked an AI agent to "
                    + "do, reply with ONLY a short title (3-6 words) naming that work. Write it in "
                    + "the same language they used. Use the imperative — \"Fix the login redirect\", "
                    + "not \"The user wants the login redirect fixed\". No quotes, no trailing "
                    + "punctuation, no explanation.",
                Messages = [new Orkka.SharedKernel.Ai.AnthropicMessage("user", opening)],
            };

            // The session itself runs on the user's runner at $0 — its title does too. No
            // platform-key fallback: a miss keeps the placeholder title, which the next opening
            // turn (or the user) can replace.
            var response = await runnerUtility.TryCompleteAsync(
                task.OrgId, task.RunnerUserId ?? task.CreatedByUserId, request, ct);
            if (response is null)
            {
                logger.LogInformation(
                    "Interactive session {TaskId} left untitled — {Reason}", task.Id,
                    Orkka.SharedKernel.Ai.RunnerUtilityUnavailableException.MessageFor(
                        "let it title this session"));
                return;
            }

            var title = response.Text?.Trim().Trim('"', '“', '”', '.').Trim();
            if (string.IsNullOrWhiteSpace(title))
                return;

            if (title.Length > MaxSessionTitleLength)
                title = title[..MaxSessionTitleLength];

            if (!await tasksApi.TrySetGeneratedSessionTitleAsync(task.Id, title, ct))
                return;

            // Recorded at $0 (runner-served) and attributed to whoever opened the session — the
            // same rule the rest of the platform follows: whoever asked for the work owns it.
            try
            {
                var usageRecorder = scope.ServiceProvider.GetRequiredService<IUsageRecorder>();
                await usageRecorder.RecordUsageAsync(new RecordUsageCommand(
                    task.OrgId, task.ProjectId, task.Id, AgentId: null, ExecutionId: null,
                    options.FastUtilityModel, response.InputTokens, response.OutputTokens,
                    response.CacheReadTokens, response.CacheWriteTokens,
                    FlatCostBase: 0m,
                    UserId: task.CreatedByUserId), ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to record titling usage for session {TaskId}", task.Id);
            }

            await taskNotifier.SendTaskUpdatedAsync(task.ProjectId, new TaskUpdatedEvent(
                task.Id, title, Priority: null, Status: null, CurrentLane: null,
                IsArchived: null, DateTimeOffset.UtcNow));

            logger.LogInformation(
                "Interactive session {TaskId} titled '{Title}' from its opening message",
                task.Id, title);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to title interactive session {TaskId}", task.Id);
        }
    }

    /// <summary>Whether the session's branch actually reached the remote. Never throws: a lookup
    /// that fails tells us nothing, and treating "unknown" as shipped would send an empty task to
    /// a reviewer, while treating it as not shipped only closes a session the human already
    /// ended.</summary>
    /// <summary>
    /// Tri-state on purpose: null means "could not check", which is a different fact from
    /// "checked, and it isn't there". A runner that dropped mid-finish used to collapse to
    /// false here, sending fully-pushed work down the "nothing shipped" path — and a task
    /// marked Done with a live branch is precisely the state that let the PR sweep merge
    /// unreviewed code.
    /// </summary>
    private async Task<bool?> BranchWasPushedAsync(
        ExecutionRoute route, TaskSnapshot task, RepoSummary repo, CancellationToken ct)
    {
        try
        {
            return await route.Git.RemoteBranchExistsAsync(task.OrgId, repo.Id, task.BranchName!, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Could not check whether branch {Branch} was pushed for task {TaskId}",
                task.BranchName, task.Id);
            return null;
        }
    }

    /// <summary>Fails every approval prompt still open on this task, releasing anything blocked on
    /// one. Best-effort: a stale prompt is a UI wart, while throwing here would take down the
    /// session it was trying to tidy up.</summary>
    private async Task ExpirePendingApprovalsAsync(IServiceScope scope, Ulid taskId, CancellationToken ct)
    {
        try
        {
            var coordinator = scope.ServiceProvider.GetRequiredService<IInteractiveApprovalCoordinator>();
            await coordinator.ExpirePendingAsync(taskId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to expire pending approvals for task {TaskId}", taskId);
        }
    }

    /// <summary>Timeline entry from the platform itself (not the agent, not the human) — session
    /// lifecycle and failures that the human must see even if they were not watching the stream.
    /// Best-effort: a lost note must never take the session down with it.</summary>
    private async Task PostSessionMessageAsync(
        ITasksApi tasksApi, Ulid taskId, MessageType type, string content, CancellationToken ct)
    {
        try
        {
            await tasksApi.AddTaskMessageAsync(taskId, "system", content, type, ct: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to post session message for task {TaskId}", taskId);
        }
    }

    private const int MaxInteractiveLogChars = 100_000;

    private static string TruncateAgentLog(string text) =>
        text.Length > MaxInteractiveLogChars
            ? "... (truncated)\n" + text[^MaxInteractiveLogChars..]
            : text;
}
