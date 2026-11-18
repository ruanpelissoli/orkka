namespace Orkka.Modules.Execution.Orchestration;

using System.Text.Json;
using System.Text.RegularExpressions;
using Orkka.Modules.Execution.Agents;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

public sealed partial class TaskOrchestrator(
    IServiceScopeFactory scopeFactory,
    IExecutionRouter executionRouter,
    Orkka.Modules.Runners.Contracts.IRunnerClaimAffinity claimAffinity,
    IAgentAssigner agentAssigner,
    IMemoryExtractor memoryExtractor,
    ITaskNotifier taskNotifier,
    IAgentAuditLog auditLog,
    IConfiguration configuration,
    IOptions<AnthropicOptions> anthropicOptions,
    IOptions<ExecutionContextOptions> contextOptions,
    IOptions<PhaseBudgetsOptions> phaseBudgets,
    WorkerDrain drain,
    ILogger<TaskOrchestrator> logger) : ITaskOrchestrator
{
    private readonly ExecutionContextOptions _context = contextOptions.Value;

    private readonly PhaseBudgetsOptions _budgets = phaseBudgets.Value;

    /// <summary>Max review→develop iterations before a human must take over. Config-tunable
    /// ("Worker:MaxReviewCycles"); applies when a human is around to take the escalation.</summary>
    private readonly int _maxReviewCycles =
        configuration.GetValue<int>("Worker:MaxReviewCycles", 2);

    /// <summary>
    /// The review budget when reviews auto-approve (autopilot, or the project setting): with
    /// nobody watching, an escalation is a task silently parked until morning, so the loop
    /// gets far more room to actually resolve the findings. Deliberately still finite — a task
    /// the reviewer rejects ten times running is stuck on something iteration won't fix, and
    /// an unbounded loop burns budget with nothing to show.
    /// </summary>
    private readonly int _autoApproveMaxReviewCycles =
        configuration.GetValue<int>("Worker:AutopilotMaxReviewCycles", 10);

    /// <summary>Max QA-fail→develop iterations before a human must take over.</summary>
    private const int MaxQaCycles = 2;

    /// <summary>Max failed-check→develop iterations before a human must take over
    /// ("Worker:MaxCheckCycles"). The check is the repo's own <c>CheckCommand</c> (lint,
    /// typecheck, tests) run after every Developing pass; each failure costs a full dev run,
    /// so the loop is short — and a task still red after two passes is stuck on something a
    /// third pass won't fix.</summary>
    private readonly int _maxCheckCycles =
        configuration.GetValue<int>("Worker:MaxCheckCycles", 2);

    /// <summary>How long the post-Developing check may run ("Worker:CheckTimeoutSeconds").
    /// Generous on purpose: this is lint + typecheck + a whole test suite, and a timeout here
    /// fails OPEN — a check that cannot finish must never become a task that cannot advance.</summary>
    private readonly int _checkTimeoutSeconds =
        configuration.GetValue<int>("Worker:CheckTimeoutSeconds", 1200);

    /// <summary>How many dev passes a task may spend reconciling a moved base branch before it
    /// is parked for a human. Deliberately small: each lap is a full agent run, and a task that
    /// has failed to absorb the base three times is not going to on the fourth.</summary>
    internal const int MaxBaseSyncCycles = 3;

    /// <summary>The review-cycle budget this task actually runs under — see the two fields above.</summary>
    private int MaxReviewCyclesFor(TaskSnapshot task, ProjectSettingsDto? settings) =>
        EffectiveTaskPolicy.For(task, settings).AutoApproveCodeReview
            ? _autoApproveMaxReviewCycles
            : _maxReviewCycles;

    /// <summary>
    /// A configured DefaultModel forces every agent onto a local model (Ollama dev setup)
    /// that cannot drive Playwright MCP browser tools — the QA phase is skipped then.
    /// </summary>
    private readonly bool _localModelOverride =
        !string.IsNullOrWhiteSpace(anthropicOptions.Value.DefaultModel);

    /// <summary>
    /// Deployment-wide fallback wall-clock limit per agent run, in minutes
    /// ("Worker:AgentTimeoutMinutes"). 0 — the shipped default — means no orchestrator-level
    /// limit: a run is then bounded by the per-phase <see cref="PhaseBudgetsOptions"/>
    /// wall-clock ceilings, which are sized to the heaviest legitimate run of each phase
    /// rather than to one flat number.
    ///
    /// It used to be a hard 30 minutes, which quietly overrode every phase budget above it —
    /// a Developing run allowed 90 minutes died at 30 with its whole spend thrown away. A
    /// project that genuinely wants a ceiling now asks for one by name.
    /// </summary>
    private readonly int _fallbackRunTimeoutMinutes =
        configuration.GetValue<int>("Worker:AgentTimeoutMinutes", 0);

    /// <summary>
    /// The wall-clock ceiling for one agent run of this project, or null when nothing caps it
    /// at this level. The project's own "Task runtime limit" wins; otherwise the deployment
    /// fallback applies, and normally there is neither.
    /// </summary>
    private TimeSpan? RunTimeoutFor(ProjectSettingsDto? settings) =>
        settings?.TaskRuntimeLimit
        ?? (_fallbackRunTimeoutMinutes > 0 ? TimeSpan.FromMinutes(_fallbackRunTimeoutMinutes) : null);

    /// <summary>Feature flag for the pipeline project-context tool server. On by default;
    /// staged-rollout escape hatch ("Worker:PipelineTools:Enabled" = false).</summary>
    private readonly bool _pipelineToolsEnabled =
        configuration.GetValue<bool>("Worker:PipelineTools:Enabled", true);

    /// <summary>Appended to the system prompt only when the run actually carries the tool
    /// server — advertising tools a run doesn't have teaches the agent its tools are broken.</summary>
    private const string PipelineToolsNotice = """


        ## Project context tools
        The project's documentation, repositories, board and history are reachable through
        your mcp__orkka tools: list_documents (titles only — cheap), get_document,
        list_repos, list_tasks, get_task, get_task_history, list_milestones. Call
        list_documents early and read whatever is relevant to this task — nobody attaches
        documents by hand. Use list_tasks/get_task_history when knowing what was already
        built would change your approach. The project memory is reachable too: `recall`
        searches it, `remember` adds to it (see the Project memory section).
        """;

    /// <summary>
    /// Developing-only addition to the tools notice. Other lanes get the tool too — it is one
    /// server — but only a lane that writes code can act on what it finds, and telling a
    /// reviewer to merge the base would have it rewriting the branch it was asked to read.
    /// </summary>
    private const string BaseUpdatesNotice = """


        ## Staying current with the base branch
        You are not the only task running against this repository. Others merge their work into
        the base branch while you write, and anything you build against a stale base collides
        later — after review, in front of a resolver that has none of your context.

        Call `check_base_updates` (mcp__orkka) to pull the base branch into your worktree:
        - Before you finish, after your last commit. Always.
        - Before you start editing anything central — shared types, config, migrations, routing.
        Commit first; it will not run against uncommitted changes.

        If it reports a conflict, the merge is already staged: resolve those files keeping both
        sides' intent, `git commit --no-edit`, then re-check your own work against what arrived.
        Treat incoming changes as finished work by a teammate, not as a stale draft to overwrite.
        """;

    private static string WithPipelineToolsNotice(
        string systemPrompt, ih0.Claude.Agent.SDK.ISdkMcpServer? toolServer,
        bool includeBaseUpdates = false) =>
        toolServer is null
            ? systemPrompt
            : systemPrompt + PipelineToolsNotice + (includeBaseUpdates ? BaseUpdatesNotice : "");

    /// <summary>
    /// The read-only project-context tool server for one pipeline run, or null when this
    /// run cannot carry one: proxied to a runner process (the live object cannot cross),
    /// a non-Claude provider (only the Claude invoker registers in-process MCP servers),
    /// or the feature flag is off.
    /// </summary>
    private ih0.Claude.Agent.SDK.ISdkMcpServer? BuildPipelineToolServer(
        TaskSnapshot task, AgentSummary? agent, ExecutionRoute route, AiProvider runProvider)
    {
        if (!_pipelineToolsEnabled
            || !route.Invoker.SupportsInProcessToolServer
            || runProvider != AiProvider.Anthropic)
            return null;

        return new TaskPipelineToolServer(
            task.OrgId, task.ProjectId, task.Id, agent?.Id, scopeFactory, logger);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    [GeneratedRegex(@"<question>\s*(.+?)\s*</question>", RegexOptions.Singleline)]
    private static partial Regex QuestionBlockRegex();

    public async Task ExecuteAsync(Ulid taskId, CancellationToken ct = default)
    {
        logger.LogInformation("Starting orchestration for task {TaskId}", taskId);

        try
        {
            using var scope = scopeFactory.CreateScope();
            var tasksApi = scope.ServiceProvider.GetRequiredService<ITasksApi>();
            var task = await tasksApi.GetTaskSnapshotAsync(taskId, ct);

            if (task is null)
            {
                logger.LogWarning("Task {TaskId} not found during orchestration", taskId);
                return;
            }

            logger.LogInformation("Task {TaskId} is in lane {Lane} — routing to appropriate phase", taskId, task.CurrentLane);

            // The task spend limit is checked before every pipeline phase starts.
            if (task.CurrentLane is Lane.Ready or Lane.Planning or Lane.Developing or Lane.Testing or Lane.CodeReview
                && await PauseIfSpendLimitReachedAsync(scope, tasksApi, task, ct))
                return;

            switch (task.CurrentLane)
            {
                case Lane.Ready:
                {
                    // The PM skips reviewing descriptions an agent already wrote: its own
                    // chat-authored tasks, and system-generated ones (seeds, deploy fixes).
                    var skipPmReview = task.SkipPmReview || task.SourceMessageId != null;
                    if (skipPmReview)
                        logger.LogInformation("Task {TaskId} has an agent-written description — skipping PM review", taskId);
                    else if (!await RunPmReviewPhaseAsync(taskId, ct))
                        break;  // parked in PendingQuestions, or the review failed

                    if (task.SkipPlanning)
                    {
                        // No planning lane and no plan-approval gate — the PM's refined
                        // description becomes a synthetic approved plan.
                        logger.LogInformation("Task {TaskId} has SkipPlanning=true — going straight to developing", taskId);
                        await SetupWorkspaceAndSkipPlanningAsync(taskId, ct);
                        await RunDevelopingPhaseAsync(taskId, ct);
                    }
                    else
                    {
                        await RunPlanningPhaseAsync(taskId, ct);
                    }
                    break;
                }

                case Lane.Planning:
                    await RunPlanningPhaseAsync(taskId, ct);
                    break;

                case Lane.Developing:
                    await RunDevelopingPhaseAsync(taskId, ct);
                    break;

                case Lane.Testing:
                    await RunTestingPhaseAsync(taskId, ct);
                    break;

                case Lane.CodeReview:
                    await RunCodeReviewPhaseAsync(taskId, ct);
                    break;

                default:
                    logger.LogWarning("Task {TaskId} is in lane {Lane} which has no automated handler — marking as blocked", taskId, task.CurrentLane);
                    await SetTaskErrorAsync(taskId, $"No automated handler for lane '{task.CurrentLane}'. Use retry to re-queue.");
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Task {TaskId} orchestration cancelled", taskId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Task {TaskId} orchestration failed", taskId);
            await SetTaskErrorAsync(taskId, ex.Message);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Phase: PM review (Ready lane)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// PM agent reviews the task for clarity: improves title/description or asks
    /// clarifying questions. Returns true when the task should proceed to planning.
    /// </summary>
    private async Task<bool> RunPmReviewPhaseAsync(Ulid taskId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var tasksApi = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var projectsApi = scope.ServiceProvider.GetRequiredService<IProjectsApi>();
        var agentsApi = scope.ServiceProvider.GetRequiredService<IAgentsApi>();

        var task = await tasksApi.GetTaskSnapshotAsync(taskId, ct);

        if (task is null) return false;

        var route = executionRouter.Resolve(task);

        var project = await projectsApi.GetProjectSummaryAsync(task.ProjectId, ct);
        var repo = task.RepoId is { } repoId ? await projectsApi.GetRepoAsync(repoId, ct) : null;

        var agent = await agentAssigner.AssignAgentAsync(agentsApi, task, Lane.Ready, ct);
        if (agent is null)
        {
            logger.LogWarning("No PM agent for task {TaskId} — skipping PM review", taskId);
            return true;
        }

        // Status = Active, StartedAt ??= now, CurrentAgentId + Running execution — one save.
        var start = await tasksApi.StartExecutionAsync(
            taskId, Lane.Ready, agent.Id, moveTaskToLane: false, setStartedAt: true, ct);
        if (start is null) return false;
        task = start.Task;
        var executionId = start.ExecutionId;

        await SendProgressAsync(taskId, agent, $"{agent.Name} (PM) is reviewing the task...");

        // PM reads the repo to ground its review
        var workspace = await SetupGitWorkspaceAsync(scope, tasksApi, task, repo, route, ct);
        if (workspace.WorktreePath is null)
        {
            await FailExecutionAsync(scope, tasksApi, task, executionId, project, agent,
                workspace.Error ?? "Could not set up repository workspace.", ct: ct);
            return false;
        }
        task = task with { BranchName = workspace.BranchName ?? task.BranchName, ReviewBaseSha = workspace.ReviewBaseSha ?? task.ReviewBaseSha };

        var conversation = await BuildConversationSectionAsync(tasksApi, taskId, since: null, ct);
        var contextFiles = ConcatSections(
            await BuildContextFilesSectionAsync(task, repo, route,
                _context.MaxFileChars, _context.PmReviewFilePathsOnly, ct),
            await BuildContextDocumentsSectionAsync(scope, task, _context.PmReviewMaxDocChars, route, ct));
        contextFiles = ConcatSections(
            contextFiles,
            await BuildTaskAttachmentsSectionAsync(scope, tasksApi, task, ct));
        contextFiles = ConcatSections(
            contextFiles,
            await BuildStoryContextSectionAsync(scope, tasksApi, task, ct));
        var memories = await LoadMemoriesSectionAsync(agentsApi, agent, task, _pipelineToolsEnabled, ct)
            + await LoadSkillsSectionAsync(agentsApi, agent, task, repo, route, ct);

        var system = $$"""
            {{BuildPersonaSection(agent)}}
            {{memories}}
            ## Current Assignment
            A human created the task below. Review it BEFORE it goes to a developer agent.
            1. Briefly explore the repository (Read, Glob, Grep) so your review reflects the actual codebase.
            2. Decide whether the task is clear enough for a developer agent to plan and implement.
            3. If clear (or you can make it clear yourself): improve the title/description — add context,
               acceptance criteria, and edge cases. Don't over-engineer simple bug fixes.
            4. If genuinely ambiguous: ask the human clarifying questions (grouped together).
            If the conversation below already contains answers to earlier questions, use them and do NOT re-ask.
            {{ReadOnlyPhaseNotice}}
            {{AutonomyInstruction(EffectiveTaskPolicy.For(task, project?.Settings), "in \"reviewNote\"")}}

            ## Response format
            Respond with ONLY a valid JSON object (no markdown fences, no extra text):
            {
              "decision": "ready" | "needs_clarification",
              "improvedTitle": "optional improved title",
              "improvedDescription": "full replacement task description in markdown, including acceptance criteria (required when decision=ready). Keep it under 9,000 characters — it replaces the stored task description, which is capped at 10,000. Be thorough but not exhaustive; the developer agent explores the repo itself.",
              "questions": [{"question": "question 1", "options": ["short option A", "short option B"], "recommended": "short option A", "allowCustom": true, "multiSelect": false}],
              "reviewNote": "1-2 sentences summarizing what you changed or why you're asking",
              "complexity": "trivial" | "small" | "standard" | "complex"
            }
            "complexity" is your honest read of the implementation effort, judged against the
            actual codebase you just explored: trivial = a few lines or a config/copy change;
            small = one focused change touching a handful of files; standard = typical feature
            work; complex = cross-cutting or architecturally risky. When unsure between two,
            pick the larger.
            Question entries: give 2-5 SHORT clickable "options" whenever natural choices exist;
            "recommended" names the option you would pick if the user gave no guidance (fold your
            one-line reason into the question text) — always include it when options exist;
            "allowCustom": true lets the human type their own answer; "multiSelect": true when
            several options can apply. A plain string is also accepted for a fully open-ended
            question.
            {{AgentLanguage.Instruction(LanguageFor(task, project))}}
            """;

        var user = BuildTaskContextSection(task, repo, contextFiles, conversation,
            await LoadSiblingReposSectionAsync(scope, project, repo, ct));

        var (runModel, runProvider) = await ResolveRunModelAsync(scope, tasksApi, task, agent, project, route, ct);
        var request = new AgentRequest(
            SystemPrompt: system,
            UserMessage: user,
            WorktreePath: workspace.WorktreePath,
            Provider: runProvider,
            Model: runModel,
            MaxTurns: _budgets.PmRefine.MaxTurns,
            MaxToolCalls: _budgets.PmRefine.MaxToolCalls,
            MaxWallClockSeconds: _budgets.PmRefine.MaxWallClockSeconds,
            // No per-run cost ceiling: runs execute on the developer's runner, which takes none —
            // the task spend limit is checked between phases (PauseIfSpendLimitReachedAsync).
            MaxCostUsd: 0m,
            Phase: AgentPhase.Planning,
            AgentName: agent?.Name ?? "agent",
            AgentRole: agent?.Role.ToString() ?? "pm",
            OrgId: task.OrgId,
            ProjectId: task.ProjectId,
            TaskId: task.Id,
            AgentId: agent?.Id,
            ExecutionId: executionId);

        LogPromptSections(taskId, Lane.Ready, system, user,
            ("memories", memories), ("contextFiles", contextFiles), ("conversation", conversation));

        var outcome = await StreamAgentRunAsync(
            taskId, agent, request, route, persistText: false, RunTimeoutFor(project?.Settings), ct);
        if (outcome.Error is not null)
        {
            await FailExecutionAsync(scope, tasksApi, task, executionId, project, agent, outcome.Error, outcome, ct);
            return false;
        }

        if (outcome.SpendLimitReached)
        {
            await CompleteExecutionAsync(scope, tasksApi, task, executionId, project, agent, outcome, agentLog: null, route, ct);
            await PauseForSpendLimitAsync(scope, tasksApi, task, project, agent, Lane.Ready, ct);
            return false;
        }

        // Same parse ladder as code review: final result message first, whole stream last.
        var result = ParseAgentVerdict<PmReviewResult>(outcome.ResultText, outcome.FullText, r => r.Decision is not null);
        if (result is null)
            logger.LogWarning("Failed to parse PM review JSON for task {TaskId} — proceeding to planning", taskId);

        if (result?.Decision?.Equals("needs_clarification", StringComparison.OrdinalIgnoreCase) == true
            && result.Questions is { Count: > 0 }
            && NormalizeQuestionElements(result.Questions) is { Count: > 0 } questions)
        {
            await CompleteExecutionAsync(scope, tasksApi, task, executionId, project, agent, outcome, agentLog: null, route, ct);

            // Under autopilot, answer with the PM's own recommendations instead of parking a
            // task nobody is around to unpark — the re-queued PM run reads the answers from
            // the conversation.
            if (await TryAutoAnswerQuestionsAsync(tasksApi, task, project, agent, questions, Lane.Ready, ct))
                return false;

            await MoveToPendingQuestionsAsync(tasksApi, task, agent, BuildQuestionsMessage(questions), Lane.Ready, ct);
            return false;
        }

        // decision == ready (or unparseable — best effort)
        var hasImprovedDescription = !string.IsNullOrWhiteSpace(result?.ImprovedDescription);
        if (hasImprovedDescription)
        {
            await PersistAgentMessageAsync(tasksApi, taskId, agent,
                $"Original task (before PM refinement):\n\n**{task.Title}**\n\n{task.Description ?? "_no description_"}",
                MessageType.SystemEvent, role: "system", ct: ct);
        }

        // Always leave a phase summary in the conversation, even when the model
        // didn't produce a usable review note.
        var pmSummary = !string.IsNullOrWhiteSpace(result?.ReviewNote)
            ? result!.ReviewNote!
            : hasImprovedDescription
                ? "I reviewed the task and refined its description — sending it to planning."
                : "I reviewed the task — it's clear enough as written. Sending it to planning.";

        // No cheaper-model suggestion: every run is on the developer's own subscription, where
        // ModelRecommendation never recommends (it only ever answered a billing question).
        string? recommendedModel = null;

        // Title/description replacement + PM summary message land in ONE save (the same
        // atomic unit the former tracked-context flush had).
        var summaryMessage = await tasksApi.ApplyPmReviewAsync(
            taskId, result?.ImprovedTitle, result?.ImprovedDescription, pmSummary,
            BuildAgentMetadataJson(agent), recommendedModel, ct);

        if (recommendedModel is not null)
        {
            await PersistAgentMessageAsync(tasksApi, taskId, agent,
                $"This looks like a {result!.Complexity!.ToLowerInvariant()} task — "
                + $"**{Orkka.Modules.Agents.Contracts.ModelRecommendation.DisplayName(recommendedModel)}** "
                + "should handle it well at a fraction of the cost. Apply the suggestion from "
                + "the task page if you agree; your agent's own model configuration is untouched "
                + "either way.",
                MessageType.SystemEvent, "system", ct);
        }

        if (hasImprovedDescription)
        {
            if (!string.IsNullOrWhiteSpace(result?.ImprovedTitle))
                task = task with { Title = result!.ImprovedTitle! };
            task = task with { Description = result?.ImprovedDescription };

            await taskNotifier.SendTaskUpdatedAsync(task.ProjectId, new TaskUpdatedEvent(
                taskId, task.Title, task.Priority.ToString(), task.Status.ToString(),
                task.CurrentLane.ToString(), task.IsArchived, DateTimeOffset.UtcNow));
        }

        if (summaryMessage is not null)
        {
            await taskNotifier.SendNewMessageAsync(task.Id, new NewMessageEvent(
                task.Id, summaryMessage.Id, summaryMessage.Role, summaryMessage.Content,
                summaryMessage.MessageType.ToString(), summaryMessage.SenderId, summaryMessage.CreatedAt));

            await taskNotifier.SendTaskLastMessageChangedAsync(task.ProjectId,
                new TaskLastMessageChangedEvent(
                    task.Id, summaryMessage.Role,
                    TaskLastMessageChangedEvent.BuildPreview(summaryMessage.Content),
                    summaryMessage.MessageType.ToString(), agent?.Name, summaryMessage.CreatedAt));
        }

        await CompleteExecutionAsync(scope, tasksApi, task, executionId, project, agent, outcome, agentLog: null, route, ct);

        logger.LogInformation("PM review complete for task {TaskId} — proceeding to planning", taskId);
        return true;
    }

    /// <summary>
    /// Extra standing order for the PM when nobody is there to answer it.
    ///
    /// Not "never ask": a task genuinely missing information should not be built either, and a
    /// confident guess at what the user wanted is worse than a parked task they can unblock in
    /// ten seconds. What changes is the threshold — resolve what the brief, the documents and
    /// the codebase can resolve, say out loud what you assumed, and reserve the question for
    /// what none of them answer.
    /// </summary>
    private static string AutonomyInstruction(EffectiveTaskPolicy policy, string recordWhere) =>
        policy.PmResolvesOwnQuestions
            ? $"""

              ## Autopilot is running — nobody will answer a question right now
              Prefer resolving ambiguity yourself: read the project brief, the linked documents,
              and the codebase, and follow the conventions already there. When you settle a point
              that way, proceed and record the choice {recordWhere} as an explicit assumption,
              e.g. "Assumed X because the brief specifies Y."
              Stop and ask only when the answer is genuinely unavailable — a product decision
              nothing in the project implies. That parks the task until a human sees it, which is
              correct for work nobody can act on, and wasteful for anything you could have
              settled by reading.
              {(policy.AnswerEveryQuestion ? AnswerEveryQuestionNotice : "")}
              """
            : "";

    /// <summary>
    /// Appended to <see cref="AutonomyInstruction"/> when the autopilot session answers every
    /// question itself. The agent needs to know two things: that asking never reaches a
    /// person, and that its own "recommended" pick IS the answer — so the recommendation is
    /// the decision, not a suggestion, and it had better be one the agent would defend.
    /// </summary>
    private const string AnswerEveryQuestionNotice = """

        This Autopilot session answers every question automatically, so asking never reaches a
        person. If you do ask, the reply is your own "recommended" option — the first option
        when you name none, and "proceed on your best assumption" for an open question or a
        <question> tag. Treat the recommendation as the decision: name the choice you would
        defend, and do not ask the same thing twice.
        """;

    /// <summary>
    /// What a developer hears back when the session answers every question and it stopped on a
    /// free-text <c>&lt;question&gt;</c>: there is no option to pick, so the answer is an order to
    /// decide. Deliberately does NOT end with "stop if the answer is unavailable" — the whole
    /// point of the choice is that the agent's judgement stands; the auto-answer cap is what
    /// ends a genuine loop.
    /// </summary>
    private const string ProceedOnOwnAssumptionAnswer =
        "This Autopilot session answers every question automatically — nobody will weigh in. "
        + "Make the most reasonable choice yourself, record it as an explicit assumption in your "
        + "commit message and final message, and continue the task from the work already on the "
        + "branch. Do not ask this again: if the project truly does not decide it, pick what a "
        + "careful engineer would and say so.";

    /// <summary>
    /// A refused tool call is not a blocker.
    ///
    /// Applies in every phase, autopilot or not. A backend agent had three `go env` calls
    /// refused by an over-broad blocklist, concluded that every tool including `echo hello` was
    /// dead, and parked the task asking a human to unstick a sandbox that was never involved.
    /// Nothing about that was answerable — the run simply stopped. Naming the possibility is
    /// what stops an agent inventing an explanation for it.
    /// </summary>
    private const string RefusedToolNotice = """

        A refused command is NOT a blocker. A few commands are restricted by policy and the
        refusal says so — your other tools are unaffected. Work around it, or note it and move
        on. Never stop to ask whether a sandbox, permission prompt, or approval is stuck: there
        is no prompt waiting for anyone, and nobody can clear it.
        """;

    /// <summary>
    /// Extracts and stores the interface this task exposes to other repositories.
    ///
    /// The problem it solves: dependency edges put the API task before the client task, but
    /// merging a backend PR puts nothing into the frontend repo's worktree. Without this the
    /// client agent starts correctly ordered and still cannot read one route signature — so it
    /// invents the endpoint it wanted, and the ordering bought nothing.
    ///
    /// Gated on having a cross-repo dependent, so the extraction cost lands only where somebody
    /// will actually read the result. Best-effort throughout: the task has already shipped its
    /// code, and failing it over a context artifact would be absurd.
    /// </summary>
    private async Task PublishApiSurfaceAsync(
        IServiceScope scope, ITasksApi tasksApi, TaskSnapshot task, RepoSummary? repo,
        ExecutionRoute route, string baseBranch, CancellationToken ct)
    {
        if (repo is null || string.IsNullOrEmpty(task.BranchName))
            return;

        try
        {
            // Two reasons a surface gets read, and the cost only lands where one of them holds.
            //
            // A cross-repo DEPENDENT is the original case. Group membership is the second: the
            // siblings are being written against a contract agreed before anyone started, and
            // the one that defines the interface usually has no dependent edge at all — it is
            // the blocker, not the blocked. Without this it would ship a shape nobody records,
            // and a sibling that has not begun yet would never learn the agreed one moved.
            var group = await tasksApi.GetGroupContractAsync(task.Id, ct);
            if (group is null && !await tasksApi.HasCrossRepoDependentsAsync(task.Id, ct))
                return;

            var diffResult = await route.Git.GetDiffAsync(
                task.OrgId, repo.Id, task.Id, baseBranch,
                headBranch: task.BranchName, ct: ct);

            if (diffResult.IsFailure || string.IsNullOrWhiteSpace(diffResult.Value))
                return;

            var runnerUtility = scope.ServiceProvider
                .GetRequiredService<Orkka.SharedKernel.Ai.IUserRunnerUtility>();
            var options = scope.ServiceProvider
                .GetRequiredService<IOptions<Orkka.SharedKernel.Ai.AnthropicOptions>>().Value;

            var request = new Orkka.SharedKernel.Ai.AnthropicRequest
            {
                Model = options.FastUtilityModel,
                MaxTokens = 1500,
                System =
                    "You extract the interface a code change exposes to OTHER services, for a "
                    + "developer in a different repository who cannot see this code.\n\n"
                    + "List only what a caller needs: HTTP endpoints (method and full path), "
                    + "request and response shapes with field names and types, status codes, "
                    + "auth requirements, and any message or event contracts. Use the exact "
                    + "names and paths from the diff — a caller cannot guess them.\n\n"
                    + "Ignore internal implementation: private helpers, database schema, "
                    + "refactors, tests, and anything not reachable from outside the service.\n\n"
                    + "Reply in terse markdown under a '### Endpoints' heading. If the change "
                    + "exposes nothing callable from another service, reply with exactly NONE.",
                Messages = [new Orkka.SharedKernel.Ai.AnthropicMessage("user",
                    $"Task: {task.Title}\n\nDiff:\n{diffResult.Value}")],
            };

            // On the task's own runner (their subscription, $0) — the same machine that just
            // produced this diff. No platform-key fallback: a miss leaves the surface
            // unpublished, and dependents work from the task's description instead.
            var response = await runnerUtility.TryCompleteAsync(
                task.OrgId, task.RunnerUserId ?? task.CreatedByUserId, request, ct);
            if (response is null)
            {
                logger.LogInformation(
                    "API surface for task {TaskId} not published — {Reason} Dependents fall back to "
                    + "its description.", task.Id,
                    Orkka.SharedKernel.Ai.RunnerUtilityUnavailableException.MessageFor(
                        "publish this task's API surface"));
                return;
            }

            // Recorded at $0 (runner-served) so the work is still visible in the org's usage.
            if (response.InputTokens > 0 || response.OutputTokens > 0)
            {
                var usageRecorder = scope.ServiceProvider.GetRequiredService<IUsageRecorder>();
                await usageRecorder.RecordUsageAsync(new RecordUsageCommand(
                    task.OrgId, task.ProjectId, task.Id, AgentId: null, ExecutionId: null,
                    options.FastUtilityModel, response.InputTokens, response.OutputTokens,
                    response.CacheReadTokens, response.CacheWriteTokens,
                    FlatCostBase: 0m,
                    UserId: task.CreatedByUserId), ct);
            }

            var surface = response.Text?.Trim();
            if (string.IsNullOrWhiteSpace(surface)
                || surface.Equals("NONE", StringComparison.OrdinalIgnoreCase))
                return;

            await tasksApi.SetApiSurfaceAsync(task.Id, surface, ct);

            // Recorded alongside the agreed contract, never over it. The other members were
            // given the agreed text and may already have built against it; overwriting would
            // move the target under them silently. Two sections that disagree is a divergence
            // somebody can see and settle — one section that quietly changed is not.
            if (group is not null)
            {
                await tasksApi.AppendGroupAsBuiltAsync(
                    group.GroupId, task.Number, task.Title, surface, ct);
            }

            logger.LogInformation(
                "Published API surface for task {TaskId} — {Chars} chars", task.Id, surface.Length);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Could not publish the API surface for task {TaskId} — dependents fall back to "
                + "its description", task.Id);
        }
    }

    /// <summary>
    /// The agreed contract for a cross-repo group, as a prompt section.
    ///
    /// Rendered for EVERY member, including the one that defines the interface and waits on
    /// nobody — a contract only the callers were shown constrains only the callers, and the
    /// provider is then free to ship something else entirely.
    ///
    /// Unlike a blocker's API surface, this exists before any member has run. That is the whole
    /// reason the halves can be built at the same time, so the wording has to be binding rather
    /// than informative: an agent that reads it as a suggestion will "improve" the shape and
    /// break the sibling that already committed to it.
    /// </summary>
    private async Task<string> BuildGroupContractSectionAsync(
        ITasksApi tasksApi, Ulid taskId, CancellationToken ct)
    {
        try
        {
            var group = await tasksApi.GetGroupContractAsync(taskId, ct);
            if (group is null || string.IsNullOrWhiteSpace(group.Contract))
                return "";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("## Agreed contract — BINDING").AppendLine();
            sb.AppendLine(
                $"This task is one repository's half of \"{group.Title}\". The other halves are "
                + "being written RIGHT NOW by other agents, in repositories that are NOT checked "
                + "out here — you cannot read their code, and it does not exist yet.")
              .AppendLine();
            sb.AppendLine(
                "The contract below was agreed before any of you started. Implement your side of "
                + "it exactly: same paths, same method names, same field names, same types, same "
                + "status codes. Do NOT redesign it, rename anything, add required fields, or "
                + "change a shape because a better one occurred to you — the other half has "
                + "already committed to this one, and a unilateral improvement is indistinguishable "
                + "from a bug. If it is genuinely impossible to implement, stop and say so via the "
                + "question protocol rather than deviating.")
              .AppendLine();

            if (group.MemberSummaries.Count > 0)
            {
                sb.AppendLine("Handled by the other halves — do not build these here:");
                foreach (var member in group.MemberSummaries)
                    sb.AppendLine($"- {member}");
                sb.AppendLine();
            }

            sb.AppendLine(group.Contract).AppendLine();

            return sb.ToString();
        }
        catch (Exception ex)
        {
            // Degrades to the pre-existing behaviour: the agent works from its description.
            logger.LogWarning(ex, "Could not load the group contract for task {TaskId}", taskId);
            return "";
        }
    }

    /// <summary>
    /// The contracts this task's cross-repo blockers published, as a prompt section.
    ///
    /// This is the payload the dependency edge exists to deliver. The heading is explicit that
    /// the code is unreachable, because an agent told about an endpoint will otherwise go
    /// looking for it and conclude the task is not ready when it cannot find it.
    /// </summary>
    private async Task<string> BuildBlockerApiContractsSectionAsync(
        ITasksApi tasksApi, Ulid taskId, CancellationToken ct)
    {
        try
        {
            var contracts = await tasksApi.ListBlockerApiContractsAsync(taskId, ct);
            if (contracts.Count == 0)
                return "";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("## APIs this task depends on").AppendLine();
            sb.AppendLine(
                "These interfaces were built by other tasks, in repositories NOT checked out "
                + "here. Their code is already merged and running — you cannot read it, so use "
                + "the contracts below exactly as written. Do not invent endpoints, rename "
                + "fields, or assume shapes that are not listed.")
              .AppendLine();

            foreach (var contract in contracts)
            {
                sb.AppendLine($"### From #{contract.Number} — {contract.Title}");
                sb.AppendLine(contract.ApiSurface).AppendLine();
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            // Degrades to the pre-existing behaviour: the agent works from descriptions.
            logger.LogWarning(ex,
                "Could not load blocker API contracts for task {TaskId}", taskId);
            return "";
        }
    }

    private sealed class PmReviewResult
    {
        public string? Decision { get; set; }
        public string? ImprovedTitle { get; set; }
        public string? ImprovedDescription { get; set; }
        // String or {question, options, allowCustom, multiSelect} entries — normalized
        // via NormalizeQuestionElements.
        public List<JsonElement>? Questions { get; set; }
        public string? ReviewNote { get; set; }
        // "trivial" | "small" | "standard" | "complex" — feeds the advisory cheaper-model
        // suggestion. Absent/unknown grades simply produce no recommendation.
        public string? Complexity { get; set; }
    }

    private sealed record TaskQuestion(
        string Question, List<string> Options, bool AllowCustom, bool MultiSelect,
        /// <summary>The option the asking agent would pick absent guidance. What autopilot
        /// answers with; the UI renders it as a "recommended" chip. Null when the agent gave
        /// none or named something that isn't one of the options.</summary>
        string? Recommended);

    /// <summary>Accepts both plain-string and structured question entries from agent JSON.</summary>
    private static List<TaskQuestion> NormalizeQuestionElements(IEnumerable<JsonElement> elements)
    {
        var questions = new List<TaskQuestion>();
        foreach (var el in elements)
        {
            if (el.ValueKind == JsonValueKind.String)
            {
                var text = el.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                    questions.Add(new TaskQuestion(
                        text.Trim(), [], AllowCustom: true, MultiSelect: false, Recommended: null));
                continue;
            }

            if (el.ValueKind != JsonValueKind.Object
                || !el.TryGetProperty("question", out var questionEl)
                || questionEl.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(questionEl.GetString()))
                continue;

            var options = el.TryGetProperty("options", out var optionsEl) && optionsEl.ValueKind == JsonValueKind.Array
                ? optionsEl.EnumerateArray()
                    .Where(o => o.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(o.GetString()))
                    .Select(o => o.GetString()!.Trim())
                    .ToList()
                : [];

            var allowCustom = !el.TryGetProperty("allowCustom", out var allowEl)
                || allowEl.ValueKind != JsonValueKind.False;
            var multiSelect = el.TryGetProperty("multiSelect", out var multiEl)
                && multiEl.ValueKind == JsonValueKind.True;

            // Only honoured when it names an actual option: a "recommended" the buttons don't
            // show is not a choice anyone (human or autopilot) can be said to have made.
            var recommended = el.TryGetProperty("recommended", out var recEl)
                    && recEl.ValueKind == JsonValueKind.String
                    && recEl.GetString() is { } rec
                ? options.FirstOrDefault(o => string.Equals(o, rec.Trim(), StringComparison.OrdinalIgnoreCase))
                : null;

            questions.Add(new TaskQuestion(
                questionEl.GetString()!.Trim(), options,
                // Without options the user can only type
                AllowCustom: allowCustom || options.Count == 0, multiSelect, recommended));
        }
        return questions;
    }

    /// <summary>Serializes questions as a ```questions block — the task conversation UI
    /// renders it as clickable option cards (same contract as agent chat).</summary>
    private static string BuildQuestionsMessage(List<TaskQuestion> questions) =>
        "```questions\n"
        + JsonSerializer.Serialize(new { questions }, JsonOptions)
        + "\n```";

    // ─────────────────────────────────────────────────────────────────────────
    // Phase: Planning
    // ─────────────────────────────────────────────────────────────────────────

    private async Task RunPlanningPhaseAsync(Ulid taskId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var tasksApi = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var projectsApi = scope.ServiceProvider.GetRequiredService<IProjectsApi>();
        var agentsApi = scope.ServiceProvider.GetRequiredService<IAgentsApi>();

        var task = await tasksApi.GetTaskSnapshotAsync(taskId, ct);

        if (task is null)
        {
            logger.LogWarning("Task {TaskId} not found during orchestration", taskId);
            return;
        }

        var route = executionRouter.Resolve(task);

        var project = await projectsApi.GetProjectSummaryAsync(task.ProjectId, ct);
        var repo = task.RepoId is { } repoId ? await projectsApi.GetRepoAsync(repoId, ct) : null;

        var agent = await agentAssigner.AssignAgentAsync(agentsApi, task, Lane.Planning, ct);

        // Move to Planning lane + Active + StartedAt ??= now + Running execution — one save.
        var start = await tasksApi.StartExecutionAsync(
            taskId, Lane.Planning, agent?.Id, moveTaskToLane: true, setStartedAt: true, ct);
        if (start is null)
        {
            logger.LogWarning("Task {TaskId} not found during orchestration", taskId);
            return;
        }
        task = start.Task;
        var executionId = start.ExecutionId;

        await taskNotifier.SendLaneChangedAsync(task.ProjectId, new LaneChangedEvent(
            taskId, task.Title, start.FromLane.ToString(), Lane.Planning.ToString(),
            task.Status.ToString(), DateTimeOffset.UtcNow));

        await SendProgressAsync(taskId, agent,
            $"{agent?.Name ?? "Agent"} is exploring the codebase and creating a plan...");

        var workspace = await SetupGitWorkspaceAsync(scope, tasksApi, task, repo, route, ct);
        if (workspace.WorktreePath is null)
        {
            await FailExecutionAsync(scope, tasksApi, task, executionId, project, agent,
                workspace.Error ?? "Could not set up repository workspace.", ct: ct);
            return;
        }
        task = task with { BranchName = workspace.BranchName ?? task.BranchName, ReviewBaseSha = workspace.ReviewBaseSha ?? task.ReviewBaseSha };

        var conversation = await BuildConversationSectionAsync(tasksApi, taskId, since: null, ct);
        var contextFiles = ConcatSections(
            await BuildContextFilesSectionAsync(task, repo, route,
                _context.MaxFileChars, _context.PlanningFilePathsOnly, ct),
            await BuildContextDocumentsSectionAsync(scope, task, _context.PlanningMaxDocChars, route, ct));
        // A plan that never mentions the brand produces steps that never mention it either,
        // and the dev agent follows the plan.
        contextFiles = ConcatSections(
            contextFiles,
            await BuildBrandKitSectionAsync(scope, task, ct));
        contextFiles = ConcatSections(
            contextFiles,
            await BuildTaskAttachmentsSectionAsync(scope, tasksApi, task, ct));
        // The plan has to be written against the real endpoints, not invented ones — this is
        // the phase where a wrong assumption gets baked into every step that follows. The group's
        // own contract comes first: it is binding on this task, where a blocker's surface only
        // describes what somebody else already shipped.
        contextFiles = ConcatSections(
            contextFiles,
            await BuildGroupContractSectionAsync(tasksApi, taskId, ct));
        contextFiles = ConcatSections(
            contextFiles,
            await BuildStoryContextSectionAsync(scope, tasksApi, task, ct));
        contextFiles = ConcatSections(
            contextFiles,
            await BuildBlockerApiContractsSectionAsync(tasksApi, taskId, ct));
        var memories = agent is not null
            ? await LoadMemoriesSectionAsync(agentsApi, agent, task, _pipelineToolsEnabled, ct)
              + await LoadSkillsSectionAsync(agentsApi, agent, task, repo, route, ct)
            : "";

        var planningPrompt = BuildPlanningPrompt(task, project, repo, agent, memories, contextFiles, conversation,
            await LoadSiblingReposSectionAsync(scope, project, repo, ct));

        var (runModel, runProvider) = await ResolveRunModelAsync(scope, tasksApi, task, agent, project, route, ct);
        var planningTools = BuildPipelineToolServer(task, agent, route, runProvider);
        var request = new AgentRequest(
            SystemPrompt: WithPipelineToolsNotice(planningPrompt.System, planningTools),
            UserMessage: planningPrompt.User,
            WorktreePath: workspace.WorktreePath,
            Provider: runProvider,
            Model: runModel,
            MaxTurns: _budgets.Planning.MaxTurns,
            MaxToolCalls: _budgets.Planning.MaxToolCalls,
            MaxWallClockSeconds: _budgets.Planning.MaxWallClockSeconds,
            // No per-run cost ceiling: runs execute on the developer's runner, which takes none —
            // the task spend limit is checked between phases (PauseIfSpendLimitReachedAsync).
            MaxCostUsd: 0m,
            Phase: AgentPhase.Planning,
            AgentName: agent?.Name ?? "agent",
            AgentRole: agent?.Role.ToString() ?? "developer",
            OrgId: task.OrgId,
            ProjectId: task.ProjectId,
            TaskId: task.Id,
            AgentId: agent?.Id,
            ExecutionId: executionId)
        { ToolServer = planningTools };

        logger.LogInformation("Starting AI planning for task {TaskId} — Agent: {Agent}, Model: {Model}",
            taskId, agent?.Name ?? "anonymous", request.Model ?? "default");

        LogPromptSections(taskId, Lane.Planning, planningPrompt.System, planningPrompt.User,
            ("memories", memories), ("contextFiles", contextFiles), ("conversation", conversation));

        var outcome = await StreamAgentRunAsync(
            taskId, agent, request, route, persistText: false, RunTimeoutFor(project?.Settings), ct);
        if (outcome.Error is not null)
        {
            await FailExecutionAsync(scope, tasksApi, task, executionId, project, agent, outcome.Error, outcome, ct);
            return;
        }

        if (outcome.SpendLimitReached)
        {
            await CompleteExecutionAsync(scope, tasksApi, task, executionId, project, agent, outcome, agentLog: null, route, ct);
            await PauseForSpendLimitAsync(scope, tasksApi, task, project, agent, Lane.Planning, ct);
            return;
        }

        // The final result message is the model's last response; FullText is the whole
        // stream and may contain narration or tool-call JSON echoed as text.
        var rawResult = StripCodeFences(outcome.ResultText ?? "");
        var rawFull = StripCodeFences(outcome.FullText);

        // The agent may ask clarifying questions instead of producing a plan
        var questions = TryExtractQuestions(rawResult) ?? TryExtractQuestions(rawFull);
        if (questions is not null)
        {
            await CompleteExecutionAsync(scope, tasksApi, task, executionId, project, agent, outcome, agentLog: null, route, ct);

            // Under autopilot, answer with the planner's own recommendations instead of
            // parking — the re-queued planning run reads the answers from the conversation.
            if (await TryAutoAnswerQuestionsAsync(tasksApi, task, project, agent, questions, Lane.Planning, ct))
                return;

            await MoveToPendingQuestionsAsync(tasksApi, task, agent, BuildQuestionsMessage(questions), Lane.Planning, ct);
            return;
        }

        var parsedContent = TryParsePlanContent(rawResult) ?? TryParsePlanContent(rawFull);
        if (parsedContent is null)
        {
            logger.LogWarning("Failed to parse plan JSON for task {TaskId}, wrapping cleaned raw content as approach", taskId);
            var prose = StripToolCallArtifacts(rawFull);
            parsedContent = new PlanDraft
            {
                Summary = "Plan generated (unstructured)",
                Approach = string.IsNullOrWhiteSpace(prose) ? rawFull : prose
            };
        }

        // Plan insert (next version, PendingReview) + execution completion + task token
        // counters — ONE save, matching the former tracked-context planning flush.
        var planSave = await tasksApi.SavePlanAndCompleteExecutionAsync(
            taskId, executionId, outcome.PromptTokens, outcome.CompletionTokens,
            JsonSerializer.Serialize(parsedContent, JsonOptions), ct);
        if (planSave is null)
        {
            logger.LogWarning("Task {TaskId} disappeared while saving plan", taskId);
            return;
        }

        await RecordRunResultsAsync(scope, task, project, agent, outcome, planSave.Execution, route, ct);

        // Autopilot — or a project that turned plan review off — approves the plan and
        // re-queues for Developing. The plan is still written and the dev agent still works
        // from it — only the human confirmation is gone.
        var planPolicy = EffectiveTaskPolicy.For(task, project?.Settings);
        if (planPolicy.AutoApprovePlan
            && await tasksApi.AutoApprovePlanAsync(taskId, ct))
        {
            await PersistAgentMessageAsync(tasksApi, taskId, agent,
                $"I created execution plan v{planSave.PlanVersion}: {parsedContent.Summary}\n\n"
                + (planPolicy.IsAutopilot
                    ? "Autopilot approved it — starting implementation."
                    : "Plan review is off for this project, so it is approved — starting implementation."),
                MessageType.Text, "agent", ct);

            await taskNotifier.SendLaneChangedAsync(task.ProjectId, new LaneChangedEvent(
                taskId, task.Title, Lane.Planning.ToString(), Lane.Developing.ToString(),
                TaskStatus.InQueue.ToString(), DateTimeOffset.UtcNow));

            logger.LogInformation(
                "Planning phase complete for task {TaskId} — plan approved automatically ({Source}), re-queued for developing",
                taskId, planPolicy.IsAutopilot ? "autopilot" : "project setting");
            return;
        }

        // Phase summary in the conversation
        await PersistAgentMessageAsync(tasksApi, taskId, agent,
            $"I created execution plan v{planSave.PlanVersion}: {parsedContent.Summary}\n\nWaiting for your approval before I start implementing.",
            MessageType.Text, "agent", ct);

        // Wait for human plan review
        await tasksApi.MoveTaskToPlanReadyAsync(taskId, ct);

        await taskNotifier.SendLaneChangedAsync(task.ProjectId, new LaneChangedEvent(
            taskId, task.Title, Lane.Planning.ToString(), Lane.PlanReady.ToString(),
            TaskStatus.WaitingPlanApproval.ToString(), DateTimeOffset.UtcNow));

        logger.LogInformation("Planning phase complete for task {TaskId}, now in PlanReady waiting for review", taskId);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Phase: Developing
    // ─────────────────────────────────────────────────────────────────────────

    private async Task RunDevelopingPhaseAsync(Ulid taskId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var tasksApi = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var projectsApi = scope.ServiceProvider.GetRequiredService<IProjectsApi>();
        var agentsApi = scope.ServiceProvider.GetRequiredService<IAgentsApi>();

        var task = await tasksApi.GetTaskSnapshotAsync(taskId, ct);

        if (task is null)
        {
            logger.LogWarning("Task {TaskId} not found during developing phase", taskId);
            return;
        }

        var route = executionRouter.Resolve(task);

        var project = await projectsApi.GetProjectSummaryAsync(task.ProjectId, ct);
        var repo = task.RepoId is { } repoId ? await projectsApi.GetRepoAsync(repoId, ct) : null;

        var plan = await tasksApi.GetLatestApprovedPlanAsync(taskId, ct);

        if (plan is null)
        {
            logger.LogError("Task {TaskId} has no approved plan — cannot start developing", taskId);
            await SetTaskErrorAsync(taskId, "No approved plan found for this task.");
            return;
        }

        var agent = await agentAssigner.AssignAgentAsync(agentsApi, task, Lane.Developing, ct);

        // Move to Developing lane + Active + CurrentAgentId + Running execution — one save
        // (developing does not touch StartedAt, matching the original phase).
        var start = await tasksApi.StartExecutionAsync(
            taskId, Lane.Developing, agent?.Id, moveTaskToLane: true, setStartedAt: false, ct);
        if (start is null)
        {
            logger.LogWarning("Task {TaskId} not found during developing phase", taskId);
            return;
        }
        task = start.Task;
        var executionId = start.ExecutionId;

        await taskNotifier.SendLaneChangedAsync(task.ProjectId, new LaneChangedEvent(
            taskId, task.Title, start.FromLane.ToString(), Lane.Developing.ToString(),
            task.Status.ToString(), DateTimeOffset.UtcNow));

        await SendProgressAsync(taskId, agent,
            $"{agent?.Name ?? "Agent"} is implementing the approved plan...");

        var workspace = await SetupGitWorkspaceAsync(
            scope, tasksApi, task, repo, route, ct, BaseSyncMode.AgentResolves);
        if (workspace.WorktreePath is null)
        {
            await FailExecutionAsync(scope, tasksApi, task, executionId, project, agent,
                workspace.Error ?? "No worktree available — cannot develop without a repository.", ct: ct);
            return;
        }
        task = task with { BranchName = workspace.BranchName ?? task.BranchName, ReviewBaseSha = workspace.ReviewBaseSha ?? task.ReviewBaseSha };

        // What the worktree was actually forked from — the milestone's integration branch, or
        // the repo default for Backlog work. Every diff, snapshot and PR below targets this, so
        // "the base" means one thing for the whole phase.
        var baseBranch = workspace.BaseBranch ?? repo?.DefaultBranch ?? "main";

        var conversation = await BuildConversationSectionAsync(tasksApi, taskId, since: null, ct);
        var contextFiles = ConcatSections(
            await BuildContextFilesSectionAsync(task, repo, route,
                _context.MaxFileChars, _context.DevelopingFilePathsOnly, ct),
            await BuildContextDocumentsSectionAsync(scope, task, _context.DevelopingMaxDocChars, route, ct));
        // Brand before assets: the palette governs everything the agent styles, the asset list
        // only matters for the subset of tasks that place one.
        contextFiles = ConcatSections(
            contextFiles,
            await BuildBrandKitSectionAsync(scope, task, ct));
        contextFiles = ConcatSections(
            contextFiles,
            await BuildDesignAssetsSectionAsync(scope, agentsApi, task, agent, ct));
        contextFiles = ConcatSections(
            contextFiles,
            await BuildTaskAttachmentsSectionAsync(scope, tasksApi, task, ct));
        // Binding contract first, then whatever earlier tasks happen to have published.
        contextFiles = ConcatSections(
            contextFiles,
            await BuildGroupContractSectionAsync(tasksApi, taskId, ct));
        contextFiles = ConcatSections(
            contextFiles,
            await BuildStoryContextSectionAsync(scope, tasksApi, task, ct));
        contextFiles = ConcatSections(
            contextFiles,
            await BuildBlockerApiContractsSectionAsync(tasksApi, taskId, ct));
        var memories = agent is not null
            ? await LoadMemoriesSectionAsync(agentsApi, agent, task, _pipelineToolsEnabled, ct)
              + await LoadSkillsSectionAsync(agentsApi, agent, task, repo, route, ct)
            : "";

        var planJson = plan.ContentJson;

        // Plan-as-file: the full plan JSON is otherwise re-sent on every dev cycle.
        var planPromptText = planJson;
        if (_context.PlanAsFile && task.RepoId is { } planRepoId)
        {
            var planWrite = await route.Git.WriteWorktreeContextAsync(task.OrgId, planRepoId, taskId,
                [new WorktreeContextFile("plan.json", planJson)], ct);
            if (planWrite.IsSuccess)
                planPromptText = "The approved plan is saved at `.orkka/context/plan.json` in your worktree. " +
                                 "Read it FIRST with the Read tool — it is your map for this task.";
            else
                logger.LogInformation("Plan-as-file unavailable for task {TaskId} ({Error}) — inlining plan",
                    taskId, planWrite.Error);
        }

        var developingPrompt = BuildDevelopingPrompt(task, project, repo, agent, memories, planPromptText, contextFiles, conversation,
            await LoadSiblingReposSectionAsync(scope, project, repo, ct));

        // A previous Developing run left a resumable claude session — either a completed one
        // re-entered (QA fail, review changes, verification feedback, answered question) or one
        // that was cut off mid-flight. Resume it and send only what's new instead of re-paying
        // the full context and making the agent redo work it already did.
        ExecutionSessionInfo? resumeSession = null;
        if (_context.EnableDevelopSessionResume
            && ResolveProvider(task, agent, project) == AiProvider.Anthropic
            && route.Invoker.SupportsSessionResume)
        {
            resumeSession = await tasksApi.GetLatestExecutionSessionAsync(taskId, Lane.Developing, ct);

            // One resume gets to try — the no-commit nudge usually works, and resuming is the
            // cheap path this pipeline deliberately prefers. But a session whose last run
            // COMPLETED in seconds has just demonstrated that resuming it produces an instant
            // empty reply (the signature of a mid-response connection drop poisoning the
            // session: observed bouncing at 15-second intervals, burning a review-cycle per
            // bounce). That one runs cold — the cold start's cost is justified precisely when
            // resuming has stopped producing work. Interrupted runs are exempt: continuing
            // cut-off work is what resuming exists for.
            if (resumeSession is not null && ResumeWouldRepeatAnEmptyRun(resumeSession))
            {
                logger.LogWarning(
                    "Task {TaskId}: last developing run completed in seconds with a resumable "
                    + "session — running cold instead of resuming a run that did no work", taskId);
                resumeSession = null;
            }
        }

        var userMessage = developingPrompt.User;
        if (resumeSession is not null)
        {
            var newConversation = await BuildConversationSectionAsync(
                tasksApi, taskId, resumeSession.CompletedAt, ct,
                heading: "New feedback since your last run");
            userMessage = BuildResumeDevelopingUserMessage(
                task, newConversation, interrupted: resumeSession.Interrupted);
        }

        // A conflicted base merge is left staged in the worktree, so it is the first thing this
        // run has to finish — ahead of the task's own work, and on the resume path too, where it
        // is the one thing the agent's session has never seen.
        if (workspace.BaseSync is { Status: BaseSyncStatus.Conflicted } conflictedSync
            && repo is not null)
        {
            userMessage = BuildBaseSyncConflictSection(repo, conflictedSync, baseBranch)
                + "\n\n" + userMessage;
        }

        var developBudget = _budgets.Developing;

        var (runModel, runProvider) = await ResolveRunModelAsync(scope, tasksApi, task, agent, project, route, ct);
        var developingTools = BuildPipelineToolServer(task, agent, route, runProvider);
        var request = new AgentRequest(
            SystemPrompt: WithPipelineToolsNotice(
                developingPrompt.System, developingTools, includeBaseUpdates: true),
            UserMessage: userMessage,
            WorktreePath: workspace.WorktreePath,
            Provider: runProvider,
            Model: runModel,
            MaxTurns: developBudget.MaxTurns,
            MaxToolCalls: developBudget.MaxToolCalls,
            MaxWallClockSeconds: developBudget.MaxWallClockSeconds,
            // No per-run cost ceiling: runs execute on the developer's runner, which takes none —
            // the task spend limit is checked between phases (PauseIfSpendLimitReachedAsync).
            MaxCostUsd: 0m,
            Phase: AgentPhase.Developing,
            AgentName: agent?.Name ?? "agent",
            AgentRole: agent?.Role.ToString() ?? "developer",
            OrgId: task.OrgId,
            ProjectId: task.ProjectId,
            TaskId: task.Id,
            AgentId: agent?.Id,
            ExecutionId: executionId,
            ResumeSessionId: resumeSession?.SessionId)
        { ToolServer = developingTools };

        logger.LogInformation("Starting AI developing for task {TaskId} — Agent: {Agent}, Resume: {Resume}",
            taskId, agent?.Name ?? "anonymous", resumeSession is not null);

        LogPromptSections(taskId, Lane.Developing, developingPrompt.System, userMessage,
            ("memories", memories), ("contextFiles", contextFiles), ("plan", planPromptText),
            ("conversation", conversation));

        var liveDiff = repo is not null && !string.IsNullOrEmpty(task.BranchName)
            ? new LiveDiffContext(task.OrgId, repo.Id, taskId, baseBranch, task.BranchName, Lane.Developing, executionId, task.ReviewBaseSha)
            : null;

        var outcome = await StreamAgentRunAsync(
            taskId, agent, request, route, persistText: true, RunTimeoutFor(project?.Settings), ct, liveDiff);

        // Resume failure before any output (session file gone — different machine, cleanup):
        // rerun cold with the full context-injected prompt, exactly the pre-resume path.
        if (resumeSession is not null && outcome.Error is not null && !outcome.ReceivedOutput)
        {
            logger.LogInformation(
                "Claude session {SessionId} could not be resumed for task {TaskId} ({Error}) — running cold with full context",
                resumeSession.SessionId, taskId, outcome.Error);

            request = request with { UserMessage = developingPrompt.User, ResumeSessionId = null };
            outcome = await StreamAgentRunAsync(
            taskId, agent, request, route, persistText: true, RunTimeoutFor(project?.Settings), ct, liveDiff);
        }

        if (outcome.Error is not null)
        {
            await FailExecutionAsync(scope, tasksApi, task, executionId, project, agent, outcome.Error, outcome, ct);
            return;
        }

        // Keep the tail — recent activity matters most, and unbounded logs bloat the row
        const int maxAgentLogChars = 100_000;
        var agentLog = outcome.FullText.Length > maxAgentLogChars
            ? "... (truncated)\n" + outcome.FullText[^maxAgentLogChars..]
            : outcome.FullText;
        await CompleteExecutionAsync(scope, tasksApi, task, executionId, project, agent, outcome, agentLog, route, ct);

        // Stopped at the task's spend checkpoint: the branch holds what was done (the runner
        // pushed it) and the hand-off is the last message. Ask before spending more — and never
        // let the no-commit / diff / review ladder below treat a paused run as a finished one.
        if (outcome.SpendLimitReached)
        {
            await PauseForSpendLimitAsync(scope, tasksApi, task, project, agent, Lane.Developing, ct);
            return;
        }

        // The dev agent may have stopped to ask the human a question
        var questionMatch = QuestionBlockRegex().Match(outcome.FullText);
        if (questionMatch.Success)
        {
            var questionText = questionMatch.Groups[1].Value.Trim();

            // A tagged question has no options to pick from, so ordinary autopilot parks it —
            // unless its session answers every question, in which case the developer is told
            // to decide and the phase re-runs on the branch it already pushed.
            if (await TryAutoAnswerDevQuestionAsync(tasksApi, task, project, agent, questionText, ct))
                return;

            await MoveToPendingQuestionsAsync(tasksApi, task, agent, questionText, Lane.Developing, ct);
            return;
        }

        // Nothing committed: the run finished without error, said it was done, and left the
        // branch exactly where it started. Advancing anyway is how a task reaches code review
        // with an empty PR, burns a reviewer run, and parks on a human who cannot see the
        // worktree — the phase that produced nothing is the phase that should hear about it.
        //
        // Checked here rather than in code review because this is the last point where a
        // re-run is cheap: no PR has been opened and no reviewer has been paid for.
        //
        // The same diff answers a second question — "what did this run change" — so a non-empty
        // result is persisted as a permanent code-change snapshot, while the runner that just
        // executed the phase is still connected and the branch still has a diff to read.
        // Fails OPEN, as HasNoCommits did: a git error neither loops the task nor blocks it.
        if (repo is not null && !string.IsNullOrEmpty(task.BranchName))
        {
            var devDiff = await TaskDiffAsync(route, task, repo.Id, baseBranch, CodeChangeSnapshotRules.MaxDiffChars, ct);

            // No commits — but is there WORK? The agent's commit is a courtesy; the platform's is
            // the rule (it already owns the push). A run that ended with its work on disk and no
            // commit (prod, 2026-09-16: nineteen minutes of frontend work, then "no commits on
            // the branch", twice in one evening) used to buy a whole second run whose only act
            // was `git commit`. Commit what the run left — the same leftovers the next phase
            // entry would have committed anyway — and carry on as if the agent had.
            if (devDiff.IsSuccess && string.IsNullOrWhiteSpace(devDiff.Value))
            {
                var onDisk = await route.Git.GetWorkingTreeDiffAsync(
                    task.OrgId, repo.Id, taskId, baseBranch, maxChars: 200, ct: ct);
                if (onDisk.IsSuccess && !string.IsNullOrWhiteSpace(onDisk.Value))
                {
                    var committed = await route.Git.CommitAndPushAsync(
                        route.Git.GetWorktreePath(task.OrgId, repo.Id, taskId),
                        "Commit work the run left uncommitted", task.BranchName, ct);
                    if (committed.IsSuccess)
                    {
                        logger.LogInformation(
                            "Task {TaskId}: the run left its work uncommitted — committed it on {Branch}",
                            taskId, task.BranchName);
                        devDiff = await TaskDiffAsync(route, task, repo.Id, baseBranch, CodeChangeSnapshotRules.MaxDiffChars, ct);
                    }
                    else
                    {
                        logger.LogWarning(
                            "Task {TaskId}: could not commit the run's uncommitted work on {Branch}: {Error}",
                            taskId, task.BranchName, committed.Error);
                    }
                }
            }

            if (devDiff.IsSuccess && string.IsNullOrWhiteSpace(devDiff.Value))
            {
                // An empty branch plus a final message that asks the human something is a
                // blocked agent that forgot the <question> tag, not an empty run. Re-running
                // it would just repeat the question; route it where an answer can arrive.
                //
                // Except under autopilot: the detector is a Haiku call with real false
                // positives, and each one parked a task nobody was around to unpark — the
                // slow drip that drained sessions. There, surface the question in the
                // conversation and let the no-commit path below re-run the developer, which
                // reads it alongside the standing make-an-assumption-and-proceed order.
                var proseQuestion = await DetectProseQuestionAsync(scope, task, agent, executionId, outcome.FullText, ct);
                if (proseQuestion is not null)
                {
                    if (EffectiveTaskPolicy.For(task, project?.Settings).AutoAnswerQuestions)
                    {
                        logger.LogInformation(
                            "Task {TaskId}: untagged question on an empty run under autopilot — surfacing it and re-running",
                            taskId);
                        await PersistAgentMessageAsync(tasksApi, taskId, agent,
                            proseQuestion, MessageType.Question, "agent", ct);
                        await tasksApi.AddTaskMessageAsync(
                            taskId, "user",
                            "Autopilot is running — nobody can answer right now. Make the most "
                            + "reasonable choice yourself, record it as an explicit assumption, and "
                            + "implement the task. Only stop if the answer is genuinely unavailable "
                            + "in the project.",
                            MessageType.Answer, metadataJson: """{"autoAnswered":true}""", ct: ct);
                    }
                    else
                    {
                        logger.LogInformation(
                            "Task {TaskId}: no commits, but the run ended on an untagged question — moving to PendingQuestions",
                            taskId);
                        await MoveToPendingQuestionsAsync(tasksApi, task, agent, proseQuestion, Lane.Developing, ct);
                        return;
                    }
                }

                var devReviewCycles = MaxReviewCyclesFor(task, project?.Settings);
                var noWork = await tasksApi.ApplyNoCommitOutcomeAsync(taskId, devReviewCycles, ct);

                if (noWork is not null)
                {
                    await PersistAgentMessageAsync(tasksApi, taskId, agent,
                        noWork.EscalatedToHuman
                            ? $"`{task.BranchName}` still has no commits after {devReviewCycles} attempts. "
                              + "A human needs to look at this — reply here with direction and the "
                              + "developer will run again with your message in its context."
                            : $"Nothing was committed to `{task.BranchName}`, so there is no work to review. "
                              + "Implement the task and commit your changes. If you are blocked on a "
                              + "question or decision, end your run with it inside <question>...</question> "
                              + "tags — that is the only way it reaches a human.",
                        MessageType.SystemEvent, "system", ct);

                    logger.LogWarning("Task {TaskId}: developing produced no commits on {Branch} — {Action}",
                        taskId, task.BranchName,
                        noWork.EscalatedToHuman ? "escalated to a human" : "re-running developing");

                    await taskNotifier.SendStatusChangedAsync(task.ProjectId, new StatusChangedEvent(
                        taskId, noWork.NewStatus.ToString(), DateTimeOffset.UtcNow));
                }

                return;
            }

            if (devDiff.IsSuccess)
            {
                try
                {
                    await tasksApi.SaveCodeChangeSnapshotAsync(
                        taskId, executionId, repo.Id, Lane.Developing,
                        task.BranchName!, baseBranch, devDiff.Value!,
                        isTruncated: devDiff.Value!.Length >= CodeChangeSnapshotRules.MaxDiffChars, ct: ct);
                }
                catch (Exception ex)
                {
                    // Best-effort, like compute-usage recording: a lost snapshot degrades a
                    // history view; it must never fail a completed run.
                    logger.LogWarning(ex,
                        "Code-change snapshot capture failed for task {TaskId}", taskId);
                }
            }
        }

        // Mechanical gate: the repo's own lint/typecheck/tests against what was just produced.
        // Here for the same reason as the empty-diff check above — the last point where a re-run
        // is cheap, before a PR exists and a reviewer is paid for. Until this existed the prompt's
        // "Definition of Done" was the only enforcement, and an agent's own word that the tests
        // pass is not evidence. Fails OPEN when the check cannot run; CLOSED on a non-zero exit.
        if (repo is not null && !string.IsNullOrEmpty(task.BranchName))
        {
            var (checkFailure, checkCommand) = await RunDevelopingCheckAsync(task, repo, route, taskId, ct);
            if (checkFailure is not null)
            {
                await PersistAgentMessageAsync(tasksApi, taskId, agent, checkFailure, MessageType.QaReport, "system", ct);

                var check = await tasksApi.ApplyCheckFailureAsync(taskId, _maxCheckCycles, ct);
                if (check is not null)
                {
                    await PersistAgentMessageAsync(tasksApi, taskId, agent,
                        check.EscalatedToHuman
                            ? $"`{checkCommand}` still fails after {_maxCheckCycles} development passes. "
                              + "A human needs to look at this — reply here with direction and the "
                              + "developer will run again with your message in its context."
                            : $"Automated checks failed — sending the task back to {agent?.Name ?? "the developer"} with the output.",
                        MessageType.SystemEvent, "system", ct);

                    logger.LogWarning("Task {TaskId}: check command failed on {Branch} — {Action}",
                        taskId, task.BranchName,
                        check.EscalatedToHuman ? "escalated to a human" : "re-running developing");

                    await taskNotifier.SendStatusChangedAsync(task.ProjectId, new StatusChangedEvent(
                        taskId, check.NewStatus.ToString(), DateTimeOffset.UtcNow));
                }

                return;
            }
        }

        // The agent commits via Bash during development; the platform pushes the branch.
        // A failed push is a checkpoint, not a warning: the phase does not complete on it.
        var pushError = await EnsureBranchPushedAfterRunAsync(route, task, repo, ct);
        if (pushError is not null)
        {
            // "src refspec … does not match any" means the local branch never got a commit —
            // the same situation as an empty diff (the agent worked but never committed), so
            // it gets the same remedy: another development pass, then a human. Everything
            // else (credentials, network, a GC'd worktree) is environmental — park the task
            // where Retry can reach it instead of advancing on a branch the forge never saw.
            if (pushError.Contains("does not match any", StringComparison.OrdinalIgnoreCase))
            {
                var pushReviewCycles = MaxReviewCyclesFor(task, project?.Settings);
                var noCommit = await tasksApi.ApplyNoCommitOutcomeAsync(taskId, pushReviewCycles, ct);
                if (noCommit is not null)
                {
                    await PersistAgentMessageAsync(tasksApi, taskId, agent,
                        noCommit.EscalatedToHuman
                            ? $"`{task.BranchName}` still has no commits after {pushReviewCycles} attempts. "
                              + "A human needs to look at this — reply here with direction and the "
                              + "developer will run again with your message in its context."
                            : $"The run left changes in the worktree but never committed them, so `{task.BranchName}` "
                              + "could not be pushed. Sending the task back to the developer to commit its work.",
                        MessageType.SystemEvent, "system", ct);
                    logger.LogWarning("Task {TaskId}: branch {Branch} had no commits at push time — {Action}",
                        taskId, task.BranchName,
                        noCommit.EscalatedToHuman ? "escalated to a human" : "re-running developing");
                    await taskNotifier.SendStatusChangedAsync(task.ProjectId, new StatusChangedEvent(
                        taskId, noCommit.NewStatus.ToString(), DateTimeOffset.UtcNow));
                }
                return;
            }

            await SetTaskErrorAsync(taskId,
                $"The branch `{task.BranchName}` could not be pushed to the repository: {pushError} "
                + "The task stays here rather than moving to review without its code — fix the cause "
                + "(for a runner task, make sure the machine that ran it is online) and press Retry.");
            return;
        }

        // Finish-line base sync, platform-enforced. The prompt asks the agent to call
        // check_base_updates before finishing, but most runs don't (observed: 0 of 9 sessions
        // in one cycle) — and a branch built on a base its siblings moved collides after
        // review, in front of a resolver with neither side's context. Clean-only here: a
        // clean merge is taken and pushed; a conflict rolls back and re-queues Developing,
        // where the ENTRY sync re-stages the merge and briefs the agent through the existing
        // conflict section. Every failure to CHECK fails open — this is a stronger signal
        // when present, never a new precondition. Skipped at the cycle cap for the same
        // reason turn caps died: a finished run must complete, not park at the finish line;
        // merge-time resolution is the backstop.
        if (repo is not null && !string.IsNullOrEmpty(task.BranchName)
            && workspace.BaseBranch is { } finishBase
            && task.BaseSyncCycleCount < MaxBaseSyncCycles)
        {
            var (finishSync, finishSyncError) = await SyncBaseBranchAsync(
                task, repo, route, BaseSyncMode.CleanOnly, finishBase, ct);
            if (finishSyncError is not null)
                logger.LogWarning(
                    "Task {TaskId}: finish-line base check could not run ({Error}) — the merge sweep's update-branch pass covers it",
                    taskId, finishSyncError);

            if (finishSync is { Status: BaseSyncStatus.Aborted })
            {
                var requeueReason =
                    $"'{finishBase}' moved {finishSync.CommitsBehind} commit(s) ahead during this run "
                    + $"and no longer merges cleanly — {finishSync.ConflictedFiles.Count} file(s) collide: "
                    + string.Join(", ", finishSync.ConflictedFiles.Take(10))
                    + (finishSync.ConflictedFiles.Count > 10 ? ", …" : "");

                var requeue = await tasksApi.RequeueForBaseSyncAsync(taskId, MaxBaseSyncCycles, requeueReason, ct);
                if (requeue is not null)
                {
                    logger.LogInformation(
                        "Task {TaskId}: base {BaseBranch} moved during the run and conflicts — another dev pass (pass {Attempt}/{Max})",
                        taskId, finishBase, requeue.BaseSyncCycleCount, MaxBaseSyncCycles);
                    await taskNotifier.SendLaneChangedAsync(task.ProjectId, new LaneChangedEvent(
                        taskId, task.Title, requeue.FromLane.ToString(), requeue.NewLane.ToString(),
                        requeue.NewStatus.ToString(), DateTimeOffset.UtcNow));
                    return;
                }
                // Requeue refused (task settled elsewhere) — fall through and complete.
            }
            else if (finishSync is { Status: BaseSyncStatus.Merged })
            {
                // The merge commit is platform-made, after the agent's own push — ship it, or
                // the PR opens one commit behind the worktree. A failed push here is not a
                // checkpoint: the agent's work is already on the forge, and the merge sweep's
                // update-branch pass redoes this reconciliation on GitHub's side if needed.
                var mergePush = await route.Git.EnsurePushedAsync(
                    task.OrgId, repo.Id, taskId, task.BranchName!, ct);
                if (mergePush.IsFailure)
                    logger.LogWarning(
                        "Task {TaskId}: could not push the finish-line base merge ({Error}) — the branch on the forge is behind the worktree",
                        taskId, mergePush.Error);
            }
        }

        // A task sent back to development after its PR merged (or was closed) keeps the old
        // PrUrl — and that PR can never move again: the review page shows a merged PR with
        // Approve/Merge hidden, while the new commits on the branch sit in no PR at all. A
        // non-open PR counts as "no PR" here, so the new work gets a fresh one.
        var previousPrIsDone = repo is not null
            && await IsPullRequestDoneAsync(scope, task, repo, ct);
        if (previousPrIsDone)
        {
            await PersistAgentMessageAsync(tasksApi, taskId, agent,
                $"The previous pull request ({task.PrUrl}) was already merged or closed, so the new changes on this branch get a fresh pull request.",
                MessageType.SystemEvent, "system", ct);
        }

        // Orkka's own pull request — every repository gets one; it is what code review reads
        // and what the merge sweep later merges (a folder) or publishes to the forge (a remote).
        if (repo is not null && !string.IsNullOrEmpty(task.BranchName)
            && (string.IsNullOrEmpty(task.PrUrl) || previousPrIsDone))
        {
            await SendProgressAsync(taskId, agent, "Creating pull request...");

            try
            {
                var pullRequests = scope.ServiceProvider.GetRequiredService<IPullRequestService>();

                // Written for the developer's team: a remote repository's pull request is
                // published to their forge under the developer's own name.
                var prTitle = task.Title;
                var prBody = $"""
                    ## Task
                    **{task.Title}** ({task.Type})

                    {task.Description ?? "No description provided."}

                    ## Plan
                    {FormatPlanForPrBody(planJson)}
                    """;

                // The task named explicitly: a Story's members share one branch.
                var prResult = await pullRequests.CreateForTaskAsync(
                    repo.Id, taskId, task.BranchName,
                    baseBranch,
                    prTitle, prBody, task.ReviewBaseSha, ct);

                if (prResult.IsSuccess)
                {
                    var prUrl = prResult.Value!.HtmlUrl;
                    await tasksApi.SetTaskPrUrlAsync(taskId, prUrl, ct);
                    task = task with { PrUrl = prUrl };
                    logger.LogInformation("PR created for task {TaskId}: {PrUrl}", taskId, prUrl);
                    await auditLog.RecordAsync(new AgentAuditRecord(
                        task.OrgId, task.ProjectId, AgentAuditEventType.PullRequestCreated,
                        $"PR created for task {taskId}: {prUrl}",
                        TaskId: task.Id, Actor: "platform"), ct);
                }
                else
                {
                    logger.LogWarning("Failed to create PR for task {TaskId}: {Error}", taskId, prResult.Error);
                    // Non-fatal — CreateMissingPullRequestsAsync retries on the next queue cycle
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error creating PR for task {TaskId} — continuing without PR", taskId);
            }
        }

        // Publish what this task exposes to other repos, while its branch still has a diff to
        // read. Runs only when something in another repo actually depends on it.
        await PublishApiSurfaceAsync(scope, tasksApi, task, repo, route, baseBranch, ct);

        var policy = EffectiveTaskPolicy.For(task, project?.Settings);

        // QA runs only when the policy allows it (the project opted in, and this is not an
        // autopilot run), the task didn't opt out, and no local model override is active
        // (local models cannot drive Playwright MCP).
        var routeToTesting = !task.SkipCodeReview && !task.SkipTesting
            && policy.RunQaLane && !_localModelOverride;
        if (!task.SkipCodeReview && !task.SkipTesting
            && policy.RunQaLane && _localModelOverride)
        {
            logger.LogInformation(
                "Task {TaskId}: QA lane is enabled but a local model override is configured — skipping the Testing phase (no MCP support)",
                taskId);
        }

        // Manual verification parks the task for the requester to test. When automated QA
        // runs first, the park happens on the QA pass instead. Deliberately not gated on
        // provider readiness or the local model override — a human doesn't need MCP.
        var parkForVerification = !task.SkipTesting
            && policy.ParkForManualVerification && !routeToTesting;

        // Phase summary in the conversation
        var devSummary = new System.Text.StringBuilder();
        devSummary.Append("I finished implementing this task");
        if (!string.IsNullOrEmpty(task.BranchName))
            devSummary.Append($" on branch `{task.BranchName}`");
        devSummary.Append('.');
        if (!string.IsNullOrEmpty(task.PrUrl))
            devSummary.Append($"\n\nPull request: {task.PrUrl}");
        devSummary.Append(routeToTesting
            ? "\n\nHanding off to QA testing."
            : parkForVerification
                ? "\n\nThe change is ready for you to verify — check out the branch and test it, then approve or reply here with feedback."
                : task.SkipCodeReview
                    ? "\n\nCode review is skipped for this task — moving it to Done."
                    : "\n\nHanding off to code review.");
        await PersistAgentMessageAsync(tasksApi, taskId, agent, devSummary.ToString(), MessageType.Text, "agent", ct);

        var transition = await tasksApi.CompleteDevelopingPhaseAsync(
            taskId, task.SkipCodeReview, routeToTesting, parkForVerification, ct);
        if (transition is null)
        {
            logger.LogWarning("Task {TaskId} disappeared while completing developing phase", taskId);
            return;
        }

        if (transition.NewLane == Lane.Done)
        {
            if (agent is not null)
                await agentsApi.RecordAgentRunResultAsync(agent.Id, 0, 0, 1, 0, ct);

            logger.LogInformation("Task {TaskId} has SkipCodeReview=true — moving directly to Done", taskId);
        }

        await taskNotifier.SendLaneChangedAsync(task.ProjectId, new LaneChangedEvent(
            taskId, task.Title, Lane.Developing.ToString(), transition.NewLane.ToString(),
            transition.NewStatus.ToString(), DateTimeOffset.UtcNow));

        logger.LogInformation("Developing phase complete for task {TaskId}, now in {Lane}",
            taskId, transition.NewLane);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Phase: Testing (QA agent + Playwright MCP browser)
    // ─────────────────────────────────────────────────────────────────────────

    private async Task RunTestingPhaseAsync(Ulid taskId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var tasksApi = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var projectsApi = scope.ServiceProvider.GetRequiredService<IProjectsApi>();
        var agentsApi = scope.ServiceProvider.GetRequiredService<IAgentsApi>();

        var task = await tasksApi.GetTaskSnapshotAsync(taskId, ct);

        if (task is null) return;

        var route = executionRouter.Resolve(task);

        var project = await projectsApi.GetProjectSummaryAsync(task.ProjectId, ct);
        var repo = task.RepoId is { } repoId ? await projectsApi.GetRepoAsync(repoId, ct) : null;

        var policy = EffectiveTaskPolicy.For(task, project?.Settings);

        // Manual verification: the requester tests the change themselves. Not gated on
        // provider readiness or the local model override — a human doesn't need MCP.
        var parkForVerification = !task.SkipTesting && policy.ParkForManualVerification;

        // Manual-only projects can still land in Testing (drag, requeue) — there is no QA
        // agent to run, so park for the requester instead of mis-escalating to QA review.
        if (!policy.RunQaLane && parkForVerification)
        {
            logger.LogInformation("Task {TaskId}: manual verification only — parking for the requester", taskId);
            await tasksApi.SetTaskWaitingUserVerificationAsync(taskId, ct);
            await PersistAgentMessageAsync(tasksApi, taskId, null,
                "Waiting for you to verify this task — test the change on its branch, then approve or reply here with feedback.",
                MessageType.SystemEvent, "system", ct);
            await taskNotifier.SendStatusChangedAsync(task.ProjectId, new StatusChangedEvent(
                taskId, TaskStatus.WaitingUserVerification.ToString(), DateTimeOffset.UtcNow));
            return;
        }

        // A task can land here after the local model override was turned on (drag, or the
        // setting changed mid-flight) — local models cannot drive MCP, so pass it through.
        if (_localModelOverride)
        {
            logger.LogInformation("Task {TaskId}: local model override active — skipping QA, moving to code review", taskId);
            await PersistAgentMessageAsync(tasksApi, taskId, null,
                "QA testing skipped: the configured local model cannot drive browser (MCP) tools — " +
                (parkForVerification ? "waiting for you to verify manually." : "handing off to code review."),
                MessageType.SystemEvent, "system", ct);
            var passThrough = await tasksApi.ApplyQaVerdictAsync(taskId, passed: true, MaxQaCycles, parkForVerification, ct);
            if (passThrough is not null)
                await taskNotifier.SendLaneChangedAsync(task.ProjectId, new LaneChangedEvent(
                    taskId, task.Title, passThrough.FromLane.ToString(), passThrough.NewLane.ToString(),
                    passThrough.NewStatus.ToString(), DateTimeOffset.UtcNow));
            return;
        }

        // Browser (MCP) testing only runs on Claude. When it isn't connected (Orkka
        // Local on codex/gemini only), skip with a persisted explanation instead of
        // letting the run fail mid-flight. Hosted: the readiness default never blocks.
        if (route.Readiness.GetBlocker(AiProvider.Anthropic) is not null)
        {
            logger.LogInformation("Task {TaskId}: Claude not connected — skipping QA, moving to code review", taskId);
            await PersistAgentMessageAsync(tasksApi, taskId, null,
                "QA skipped — browser testing requires a connected Claude subscription. " +
                "Connect the claude CLI on the runner to enable it, or test manually." +
                (parkForVerification ? " Waiting for you to verify this task manually." : ""),
                MessageType.SystemEvent, "system", ct);
            var skip = await tasksApi.ApplyQaVerdictAsync(taskId, passed: true, MaxQaCycles, parkForVerification, ct);
            if (skip is not null)
                await taskNotifier.SendLaneChangedAsync(task.ProjectId, new LaneChangedEvent(
                    taskId, task.Title, skip.FromLane.ToString(), skip.NewLane.ToString(),
                    skip.NewStatus.ToString(), DateTimeOffset.UtcNow));
            return;
        }

        var agent = await agentAssigner.AssignAgentAsync(agentsApi, task, Lane.Testing, ct);
        if (agent is null)
        {
            logger.LogInformation("No QA agent for task {TaskId} — waiting for human testing", taskId);
            await EscalateToQaReviewAsync(tasksApi, task, null,
                "No QA agent is available for this project — a human should test this task and move it to code review.", ct);
            return;
        }

        // Repo first: a project with a frontend and a backend has two apps to start, and the
        // project-level setting can only ever describe one of them. Falling back to it keeps
        // single-repo projects configured before the per-repo field working untouched.
        var runCommand = repo?.QaRunCommand is { Length: > 0 } repoCommand
            ? repoCommand
            : project?.Settings?.QaRunCommand;

        // Nobody typed one: the project's run targets (the Run button's own knowledge, or the
        // repo's orkka.json) already say how to start this repo's app. Derived in bash terms
        // for the agent's Bash tool.
        IReadOnlyList<RunTargetDto>? runTargets = null;
        if (string.IsNullOrWhiteSpace(runCommand) && repo is not null)
        {
            runTargets = await TryGetRunTargetsAsync(scope, task, ct);
            runCommand = QaRunCommandDerivation.Derive(runTargets, repo.Id);
            if (runCommand is not null)
                logger.LogInformation(
                    "Task {TaskId}: QA run command derived from run targets: {Command}", taskId, runCommand);
        }

        if (string.IsNullOrWhiteSpace(runCommand))
        {
            // Cheaper to escalate before spending tokens than to let the agent discover
            // it has no way to start the app.
            logger.LogInformation("Task {TaskId}: QA lane enabled but no run command configured — escalating", taskId);
            await EscalateToQaReviewAsync(tasksApi, task, agent,
                "QA testing needs a run command so the QA agent can start the app — set one on the " +
                "repository (Repositories → workspace settings), or add an `orkka.json` run manifest " +
                "to the repo and press Detect in the Run panel. Then move this task back to Testing — " +
                "or move it to Code Review to skip QA.", ct);
            return;
        }

        // Status = Active + CurrentAgentId + Running execution — one save (the lane move
        // already happened when developing completed).
        var start = await tasksApi.StartExecutionAsync(
            taskId, Lane.Testing, agent.Id, moveTaskToLane: false, setStartedAt: false, ct);
        if (start is null) return;
        task = start.Task;
        var executionId = start.ExecutionId;

        await SendProgressAsync(taskId, agent, $"{agent.Name} is testing the feature end-to-end...");

        var workspace = await SetupGitWorkspaceAsync(scope, tasksApi, task, repo, route, ct);
        if (workspace.WorktreePath is null || repo is null)
        {
            await EscalateToQaReviewAsync(tasksApi, task, agent,
                $"Automated QA unavailable: {workspace.Error ?? "missing repository"}. A human should test this task.", ct);
            await tasksApi.MarkExecutionFailedAsync(taskId, executionId,
                workspace.Error ?? "missing repository", ct);
            return;
        }
        if (await BounceForBaseSyncAsync(tasksApi, task, repo, executionId, workspace.BaseSync,
                workspace.BaseBranch ?? repo.DefaultBranch, ct))
            return;
        task = task with { BranchName = workspace.BranchName ?? task.BranchName, ReviewBaseSha = workspace.ReviewBaseSha ?? task.ReviewBaseSha };

        // Same reasoning as the missing-run-command check above, one step earlier: if the
        // workspace never got its dependencies, the run command cannot start the app, and
        // letting the agent discover that costs a full QA run's tokens to learn nothing.
        if (workspace.SetupFailure is not null)
        {
            logger.LogInformation(
                "Task {TaskId}: setup failed — escalating instead of starting QA", taskId);
            await EscalateToQaReviewAsync(tasksApi, task, agent,
                $"{workspace.SetupFailure} The app can't start, so automated QA would fail. " +
                "Fix the setup command in the repository's settings and move this task back to " +
                "Testing — or test it manually and move it to Code Review.", ct);
            await tasksApi.MarkExecutionFailedAsync(taskId, executionId, workspace.SetupFailure, ct);
            return;
        }

        var baseBranch = workspace.BaseBranch ?? repo.DefaultBranch;

        // The diff scopes what to test; unlike code review an empty diff isn't fatal —
        // QA verifies behavior in the running app, not the patch itself.
        var diffResult = await TaskDiffAsync(route, task, repo.Id, baseBranch, ct: ct);
        var diff = diffResult.IsSuccess ? diffResult.Value : null;

        var plan = await tasksApi.GetLatestApprovedPlanAsync(taskId, ct);
        var conversation = await BuildConversationSectionAsync(tasksApi, taskId, since: null, ct);
        var memories = await LoadMemoriesSectionAsync(agentsApi, agent, task, _pipelineToolsEnabled, ct)
            + await LoadSkillsSectionAsync(agentsApi, agent, task, repo, route, ct);

        // A repo that serves no UI gets HTTP verification instead of a browser: same lane, same
        // verdict contract, different procedure. Writing the tests belongs to the dev agent —
        // this phase runs and asserts them, which is also why it stays read-only.
        var apiMode = repo is not null
            && QaTargetMode.IsApiOnly(runTargets ?? await TryGetRunTargetsAsync(scope, task, ct), repo.Id);

        var system = BuildQaSystemPrompt(
            task, project, agent, memories, runCommand, apiMode);

        var userBuilder = new System.Text.StringBuilder();
        userBuilder.AppendLine(PromptSafety.Notice);
        userBuilder.AppendLine();
        userBuilder.AppendLine("Task title:");
        userBuilder.AppendLine(PromptSafety.Wrap("task-title", task.Title));
        userBuilder.AppendLine("Task description:");
        userBuilder.AppendLine(PromptSafety.Wrap("task-description", task.Description ?? "No description provided."));
        if (plan is not null)
        {
            userBuilder.AppendLine();
            userBuilder.AppendLine($"Approved plan summary: {plan.Summary}");
        }
        if (!string.IsNullOrEmpty(conversation))
        {
            userBuilder.AppendLine();
            userBuilder.Append(conversation);
        }
        if (!string.IsNullOrWhiteSpace(diff))
        {
            userBuilder.AppendLine();
            userBuilder.AppendLine($"## Diff of this task's changes (origin/{baseBranch}...HEAD) — test what changed");
            if (await TryWriteDiffFileAsync(task, repo, diff, route, ct))
            {
                userBuilder.AppendLine(
                    "The diff is saved at `.orkka/context/diff.patch` in your checkout. Read it with " +
                    "the Read tool (use offset/limit for large diffs) to scope what to test.");
            }
            else
            {
                userBuilder.AppendLine("```diff");
                userBuilder.AppendLine(diff);
                userBuilder.AppendLine("```");
            }
        }

        var (runModel, runProvider) = await ResolveRunModelAsync(scope, tasksApi, task, agent, project, route, ct);
        var request = new AgentRequest(
            SystemPrompt: system,
            UserMessage: userBuilder.ToString(),
            WorktreePath: workspace.WorktreePath,
            Provider: runProvider,
            Model: runModel,
            // Browser interaction is turn-hungry: start app, navigate, click, assert.
            MaxTurns: _budgets.Testing.MaxTurns,
            MaxToolCalls: _budgets.Testing.MaxToolCalls,
            MaxWallClockSeconds: _budgets.Testing.MaxWallClockSeconds,
            // No per-run cost ceiling: runs execute on the developer's runner, which takes none —
            // the task spend limit is checked between phases (PauseIfSpendLimitReachedAsync).
            MaxCostUsd: 0m,
            Phase: AgentPhase.Testing,
            AgentName: agent.Name,
            AgentRole: agent.Role.ToString(),
            OrgId: task.OrgId,
            ProjectId: task.ProjectId,
            TaskId: task.Id,
            AgentId: agent.Id,
            ExecutionId: executionId,
            ApiTesting: apiMode);

        logger.LogInformation(
            "Starting AI QA testing for task {TaskId} — Agent: {Agent}, mode: {Mode}",
            taskId, agent.Name, apiMode ? "api" : "browser");

        LogPromptSections(taskId, Lane.Testing, system, request.UserMessage,
            ("memories", memories), ("diff", diff), ("conversation", conversation));

        var outcome = await StreamAgentRunAsync(
            taskId, agent, request, route, persistText: false, RunTimeoutFor(project?.Settings), ct);
        if (outcome.Error is not null)
        {
            await FailExecutionAsync(scope, tasksApi, task, executionId, project, agent, outcome.Error, outcome, ct);
            return;
        }

        if (outcome.SpendLimitReached)
        {
            await CompleteExecutionAsync(scope, tasksApi, task, executionId, project, agent, outcome, agentLog: null, route, ct);
            await PauseForSpendLimitAsync(scope, tasksApi, task, project, agent, Lane.Testing, ct);
            return;
        }

        var verdict = ParseAgentVerdict<QaVerdict>(outcome.ResultText, outcome.FullText, v => v.Decision is not null);
        if (verdict is null)
            (verdict, outcome) = await ReaskUnparseableVerdictAsync<QaVerdict>(
                taskId, agent, request, route, outcome, v => v.Decision is not null, ct);
        if (verdict is null)
            logger.LogWarning("Failed to parse QA verdict for task {TaskId}", taskId);

        await CompleteExecutionAsync(scope, tasksApi, task, executionId, project, agent, outcome, agentLog: null, route, ct);
        var qaPushError = await EnsureBranchPushedAfterRunAsync(route, task, repo, ct);
        if (qaPushError is not null)
        {
            // Not a checkpoint here — the developing phase already shipped the branch; what
            // may be missing is the QA run's own test commits, which the reader must know.
            await PersistAgentMessageAsync(tasksApi, taskId, agent,
                $"The QA run's changes could not be pushed ({qaPushError}) — the branch on the "
                + "forge may be missing the latest test commits.",
                MessageType.SystemEvent, "system", ct);
        }

        if (verdict?.Decision is null)
        {
            await EscalateToQaReviewAsync(tasksApi, task, agent,
                "The QA agent produced an unreadable verdict — a human should test this task and decide.", ct);
            return;
        }

        var passed = verdict.Decision.Equals("pass", StringComparison.OrdinalIgnoreCase);

        // Persist the report in the task conversation — on a fail, this is exactly what
        // the dev agent sees on the retry (BuildConversationSectionAsync includes it).
        var report = new System.Text.StringBuilder();
        report.AppendLine($"**QA testing — {(passed ? "Passed ✅" : "Failed ❌")}**");
        report.AppendLine();
        report.AppendLine(verdict.Summary ?? "");
        if (verdict.Failures is { Count: > 0 })
        {
            report.AppendLine();
            foreach (var f in verdict.Failures)
            {
                report.AppendLine($"- **{f.Criterion}**");
                report.AppendLine($"  - Steps: {f.Steps}");
                report.AppendLine($"  - Expected: {f.Expected}");
                report.AppendLine($"  - Actual: {f.Actual}");
            }
        }

        await PersistAgentMessageAsync(tasksApi, taskId, agent, report.ToString(), MessageType.QaReport, "qa", ct);

        var transition = await tasksApi.ApplyQaVerdictAsync(taskId, passed, MaxQaCycles, parkForVerification, ct);
        if (transition is null) return;

        if (passed)
        {
            await agentsApi.RecordAgentRunResultAsync(agent.Id, 0, 0, 1, 0, ct);
            logger.LogInformation("QA passed for task {TaskId} — {Next}",
                taskId, parkForVerification ? "waiting for user verification" : "moving to code review");
        }
        else if (!transition.EscalatedToHuman)
        {
            logger.LogInformation("QA failed for task {TaskId} (cycle {Cycle}) — re-queuing for dev",
                taskId, transition.QaCycleCount);
        }
        else
        {
            await PersistAgentMessageAsync(tasksApi, taskId, agent,
                $"Reached {MaxQaCycles} automated test cycles without a pass — a human needs to look at this task and decide.",
                MessageType.SystemEvent, "system", ct);

            logger.LogInformation("Task {TaskId} reached max QA cycles — escalated to human", taskId);
        }

        if (transition.NewLane != transition.FromLane)
        {
            await taskNotifier.SendLaneChangedAsync(task.ProjectId, new LaneChangedEvent(
                taskId, task.Title, transition.FromLane.ToString(), transition.NewLane.ToString(),
                transition.NewStatus.ToString(), DateTimeOffset.UtcNow));
        }
        else
        {
            await taskNotifier.SendStatusChangedAsync(task.ProjectId, new StatusChangedEvent(
                taskId, transition.NewStatus.ToString(), DateTimeOffset.UtcNow));
        }
    }

    private sealed class QaVerdict
    {
        public string? Decision { get; set; }
        public string? Summary { get; set; }
        public List<QaFailure>? Failures { get; set; }
    }

    private sealed class QaFailure
    {
        public string Criterion { get; set; } = "";
        public string Steps { get; set; } = "";
        public string Expected { get; set; } = "";
        public string Actual { get; set; } = "";
    }

    private async Task EscalateToQaReviewAsync(
        ITasksApi tasksApi, TaskSnapshot task, AgentSummary? agent, string reason, CancellationToken ct)
    {
        // Same reasoning as the code-review escalation: a QA run is long enough for the task to
        // be approved and merged underneath it, and an escalation posted after that is noise on
        // a task that already shipped.
        if (!await tasksApi.SetTaskWaitingQaReviewAsync(task.Id, ct))
        {
            logger.LogInformation(
                "Task {TaskId}: QA escalation skipped — the task was already settled while QA "
                + "ran ({Reason})", task.Id, reason);
            return;
        }

        await PersistAgentMessageAsync(tasksApi, task.Id, agent, reason, MessageType.SystemEvent, "system", ct);
        await taskNotifier.SendStatusChangedAsync(task.ProjectId, new StatusChangedEvent(
            task.Id, TaskStatus.WaitingQaReview.ToString(), DateTimeOffset.UtcNow));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Phase: Code review (reviewer agent)
    // ─────────────────────────────────────────────────────────────────────────

    private async Task RunCodeReviewPhaseAsync(Ulid taskId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var tasksApi = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var projectsApi = scope.ServiceProvider.GetRequiredService<IProjectsApi>();
        var agentsApi = scope.ServiceProvider.GetRequiredService<IAgentsApi>();

        var task = await tasksApi.GetTaskSnapshotAsync(taskId, ct);

        if (task is null) return;

        var route = executionRouter.Resolve(task);

        var project = await projectsApi.GetProjectSummaryAsync(task.ProjectId, ct);
        var repo = task.RepoId is { } repoId ? await projectsApi.GetRepoAsync(repoId, ct) : null;

        var agent = await agentAssigner.AssignAgentAsync(agentsApi, task, Lane.CodeReview, ct);
        if (agent is null)
        {
            // Says so in the thread rather than only in the logs. This park looks identical to
            // the one "Automatic code review approval" turns off, so a project whose reviewer
            // agent is missing reads as the setting being ignored — the message is the only
            // thing that tells the two apart.
            logger.LogInformation("No reviewer agent for task {TaskId} — waiting for human review", taskId);
            await EscalateToHumanReviewAsync(tasksApi, task, null,
                "This project has no Code Reviewer agent, so the review could not run automatically — "
                + "review this PR yourself, or add a Code Reviewer on the project's Team page. "
                + "\"Automatic code review approval\" cannot skip this step: it only applies once a "
                + "reviewer agent has actually approved.", ct);
            return;
        }

        // Status = Active + CurrentAgentId + Running execution — one save (no lane move,
        // no StartedAt, matching the original phase).
        var start = await tasksApi.StartExecutionAsync(
            taskId, Lane.CodeReview, agent.Id, moveTaskToLane: false, setStartedAt: false, ct);
        if (start is null) return;
        task = start.Task;
        var executionId = start.ExecutionId;

        await SendProgressAsync(taskId, agent, $"{agent.Name} is reviewing the pull request...");

        var workspace = await SetupGitWorkspaceAsync(scope, tasksApi, task, repo, route, ct);
        if (workspace.WorktreePath is null || repo is null || string.IsNullOrEmpty(workspace.BranchName ?? task.BranchName))
        {
            await EscalateToHumanReviewAsync(tasksApi, task, agent,
                $"Automated review unavailable: {workspace.Error ?? "missing repo/branch"}. A human should review this PR.", ct);
            await tasksApi.MarkExecutionFailedAsync(taskId, executionId,
                workspace.Error ?? "missing repo/branch", ct);
            return;
        }
        if (await BounceForBaseSyncAsync(tasksApi, task, repo, executionId, workspace.BaseSync,
                workspace.BaseBranch ?? repo.DefaultBranch, ct))
            return;
        task = task with { BranchName = workspace.BranchName ?? task.BranchName, ReviewBaseSha = workspace.ReviewBaseSha ?? task.ReviewBaseSha };

        var baseBranch = workspace.BaseBranch ?? repo.DefaultBranch;
        // Named and passed explicitly because the snapshot backfill below computes its
        // IsTruncated flag against this same number — as a silent default + a separate
        // literal, the two could drift and stamp truncation flags that lie.
        const int reviewDiffMaxChars = 60_000;
        var diffResult = await TaskDiffAsync(route, task, repo.Id, baseBranch, reviewDiffMaxChars, ct);
        if (diffResult.IsFailure)
        {
            // A git-level failure is not something re-running the developer fixes: the branch,
            // the worktree, or the remote is in a state nothing here can reason about.
            await EscalateToHumanReviewAsync(tasksApi, task, agent,
                $"Automated review unavailable (could not compute diff: {diffResult.Error}). A human should review this PR.", ct);
            await tasksApi.MarkExecutionFailedAsync(taskId, executionId, diffResult.Error!, ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(diffResult.Value))
        {
            // An empty diff means the branch has no commits — the developer wrote the code and
            // never committed it. That is a mechanical miss, not a judgement call, so it goes
            // back to the developer exactly as a rejected review would, bounded by the same
            // cycle cap that escalates once retrying has stopped being useful.
            //
            // This used to escalate immediately, which parked the task on a human who had
            // nothing to act on: the PR was empty, the implementation sat uncommitted in a
            // worktree they could not see, and only a manual commit-and-push cleared it. A
            // recoverable state must not be reported as a dead end.
            await tasksApi.MarkExecutionFailedAsync(taskId, executionId, "empty diff", ct);

            var reviewCycles = MaxReviewCyclesFor(task, project?.Settings);
            var emptyTransition = await tasksApi.ApplyNoCommitOutcomeAsync(taskId, reviewCycles, ct);
            if (emptyTransition is null) return;

            await PersistAgentMessageAsync(tasksApi, taskId, agent,
                emptyTransition.EscalatedToHuman
                    ? $"`{task.BranchName}` still has no commits after {reviewCycles} attempts — "
                      + "the implementation is uncommitted in the worktree. A human needs to look at "
                      + "this — reply here with direction and the developer will run again with your "
                      + "message in its context."
                    : $"There is nothing to review: `{task.BranchName}` has no commits, so the "
                      + "implementation is still uncommitted in the worktree. Commit and push it, "
                      + "then this comes back for review.",
                MessageType.SystemEvent, "system", ct);

            logger.LogWarning("Task {TaskId}: empty diff on {Branch} — {Action}",
                taskId, task.BranchName,
                emptyTransition.EscalatedToHuman ? "escalated to a human" : "returned to developing");

            if (emptyTransition.NewLane != emptyTransition.FromLane)
            {
                await taskNotifier.SendLaneChangedAsync(task.ProjectId, new LaneChangedEvent(
                    taskId, task.Title, emptyTransition.FromLane.ToString(),
                    emptyTransition.NewLane.ToString(), emptyTransition.NewStatus.ToString(),
                    DateTimeOffset.UtcNow));
            }
            else
            {
                await taskNotifier.SendStatusChangedAsync(task.ProjectId, new StatusChangedEvent(
                    taskId, emptyTransition.NewStatus.ToString(), DateTimeOffset.UtcNow));
            }

            return;
        }

        // Backfill: normally the Developing phase already stored a code-change snapshot, and
        // onlyIfMissing makes this a no-op. It exists for tasks already in flight when snapshots
        // shipped and for runs where the primary capture failed — the review diff (60k cap) is
        // the exact bytes the reviewer judges, better than no history at all.
        try
        {
            await tasksApi.SaveCodeChangeSnapshotAsync(
                taskId, executionId, repo.Id, Lane.CodeReview,
                task.BranchName!, baseBranch, diffResult.Value!,
                isTruncated: diffResult.Value!.Length >= reviewDiffMaxChars,
                onlyIfMissing: true, ct: ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Code-change snapshot backfill failed for task {TaskId}", taskId);
        }

        var plan = await tasksApi.GetLatestApprovedPlanAsync(taskId, ct);

        var conversation = await BuildConversationSectionAsync(tasksApi, taskId, since: null, ct);
        var memories = await LoadMemoriesSectionAsync(agentsApi, agent, task, _pipelineToolsEnabled, ct)
            + await LoadSkillsSectionAsync(agentsApi, agent, task, repo, route, ct);

        var maxReviewCycles = MaxReviewCyclesFor(task, project?.Settings);

        var system = BuildCodeReviewSystemPrompt(task, agent, memories, maxReviewCycles, project);

        var userBuilder = new System.Text.StringBuilder();
        userBuilder.AppendLine(PromptSafety.Notice);
        userBuilder.AppendLine();
        userBuilder.AppendLine("Task title:");
        userBuilder.AppendLine(PromptSafety.Wrap("task-title", task.Title));
        userBuilder.AppendLine("Task description:");
        userBuilder.AppendLine(PromptSafety.Wrap("task-description", task.Description ?? "No description provided."));
        if (plan is not null)
        {
            userBuilder.AppendLine();
            userBuilder.AppendLine($"Approved plan summary: {plan.Summary}");
        }
        if (await BuildStoryContextSectionAsync(scope, tasksApi, task, ct) is { Length: > 0 } storySection)
        {
            userBuilder.AppendLine();
            userBuilder.Append(storySection);
        }
        if (!string.IsNullOrEmpty(conversation))
        {
            userBuilder.AppendLine();
            userBuilder.Append(conversation);
        }
        userBuilder.AppendLine();
        // A Story member is reviewed on its own change: the earlier sub-tasks were reviewed already.
        userBuilder.AppendLine(task.ReviewBaseSha is { } reviewBase
            ? $"## Diff (this sub-task's change only: {reviewBase[..Math.Min(10, reviewBase.Length)]}..HEAD)"
            : $"## Diff (origin/{baseBranch}...HEAD)");
        if (await TryWriteDiffFileAsync(task, repo, diffResult.Value!, route, ct))
        {
            userBuilder.AppendLine(
                "The full diff is saved at `.orkka/context/diff.patch` in your checkout. Read it with " +
                "the Read tool (use offset/limit for large diffs). Your comments' line numbers must " +
                "reference the NEW file versions as shown in that diff.");
        }
        else
        {
            userBuilder.AppendLine("```diff");
            userBuilder.AppendLine(diffResult.Value);
            userBuilder.AppendLine("```");
        }

        var (runModel, runProvider) = await ResolveRunModelAsync(scope, tasksApi, task, agent, project, route, ct);
        var reviewTools = BuildPipelineToolServer(task, agent, route, runProvider);
        var request = new AgentRequest(
            SystemPrompt: WithPipelineToolsNotice(system, reviewTools),
            UserMessage: userBuilder.ToString(),
            WorktreePath: workspace.WorktreePath,
            Provider: runProvider,
            Model: runModel,
            MaxTurns: _budgets.Review.MaxTurns,
            MaxToolCalls: _budgets.Review.MaxToolCalls,
            MaxWallClockSeconds: _budgets.Review.MaxWallClockSeconds,
            // No per-run cost ceiling: runs execute on the developer's runner, which takes none —
            // the task spend limit is checked between phases (PauseIfSpendLimitReachedAsync).
            MaxCostUsd: 0m,
            Phase: AgentPhase.Review,
            AgentName: agent.Name,
            AgentRole: agent.Role.ToString(),
            OrgId: task.OrgId,
            ProjectId: task.ProjectId,
            TaskId: task.Id,
            AgentId: agent.Id,
            ExecutionId: executionId)
        { ToolServer = reviewTools };

        logger.LogInformation("Starting AI code review for task {TaskId} — Agent: {Agent}", taskId, agent.Name);

        LogPromptSections(taskId, Lane.CodeReview, system, request.UserMessage,
            ("memories", memories), ("diff", diffResult.Value), ("conversation", conversation));

        var outcome = await StreamAgentRunAsync(
            taskId, agent, request, route, persistText: false, RunTimeoutFor(project?.Settings), ct);
        if (outcome.Error is not null)
        {
            await FailExecutionAsync(scope, tasksApi, task, executionId, project, agent, outcome.Error, outcome, ct);
            return;
        }

        if (outcome.SpendLimitReached)
        {
            await CompleteExecutionAsync(scope, tasksApi, task, executionId, project, agent, outcome, agentLog: null, route, ct);
            await PauseForSpendLimitAsync(scope, tasksApi, task, project, agent, Lane.CodeReview, ct);
            return;
        }

        // Prefer the final result message — FullText is the whole stream and picks up
        // inter-tool narration whenever the reviewer uses Read/Glob/Grep before answering.
        var verdict = ParseAgentVerdict<ReviewVerdict>(outcome.ResultText, outcome.FullText, v => v.Decision is not null);
        if (verdict is null)
            (verdict, outcome) = await ReaskUnparseableVerdictAsync<ReviewVerdict>(
                taskId, agent, request, route, outcome, v => v.Decision is not null, ct);
        if (verdict is null)
            logger.LogWarning("Failed to parse review verdict for task {TaskId}", taskId);

        await CompleteExecutionAsync(scope, tasksApi, task, executionId, project, agent, outcome, agentLog: null, route, ct);

        if (verdict?.Decision is null)
        {
            await EscalateToHumanReviewAsync(tasksApi, task, agent,
                "The reviewer agent produced an unreadable verdict — a human should review this PR.", ct);
            return;
        }

        var isApprove = verdict.Decision.Equals("approve", StringComparison.OrdinalIgnoreCase);

        // Post the review to GitHub (best effort — GitHub may reject an app approving its own PR)
        await PostReviewAsync(scope, task, repo, verdict, isApprove, ct);

        // Persist the verdict in the task conversation
        var reviewMessage = new System.Text.StringBuilder();
        reviewMessage.AppendLine($"**Code review — {(isApprove ? "Approved ✅" : "Changes requested ❌")}**");
        reviewMessage.AppendLine();
        reviewMessage.AppendLine(verdict.Summary ?? "");
        if (verdict.Comments is { Count: > 0 })
        {
            reviewMessage.AppendLine();
            foreach (var c in verdict.Comments)
                reviewMessage.AppendLine($"- `{c.Path}:{c.Line}` — {c.Body}");
        }
        if (isApprove)
        {
            reviewMessage.AppendLine();
            reviewMessage.AppendLine("The pull request will be squash-merged to main automatically.");
        }

        await PersistAgentMessageAsync(tasksApi, taskId, agent, reviewMessage.ToString(), MessageType.CodeReview, "reviewer", ct);

        await taskNotifier.SendCodeReviewedAsync(taskId, new CodeReviewedEvent(
            taskId, isApprove ? "approve" : "request_changes", verdict.Summary, DateTimeOffset.UtcNow));

        // Automation is opt-in: unless the project enables AutoApproveCodeReview — or autopilot
        // submitted this task — an agent approval is recorded but a human confirms before the
        // task completes and merges. A rejection parks the task either way: the reviewer always
        // runs, and autopilot never merges over its objection.
        if (isApprove && !EffectiveTaskPolicy.For(task, project?.Settings).AutoApproveCodeReview)
        {
            // The resolved value, not just the outcome. "The setting is on but tasks still park"
            // is otherwise indistinguishable in the logs from the setting genuinely being off,
            // and this is the one line that separates them.
            logger.LogInformation(
                "Task {TaskId} parked for human confirmation of an agent approval — project "
                + "{ProjectId} AutoApproveCodeReview={AutoApprove} (settings loaded: {SettingsLoaded}, "
                + "autopilot: {Autopilot})",
                taskId, task.ProjectId, project?.Settings?.AutoApproveCodeReview,
                project?.Settings is not null, task.AutopilotSessionId is not null);

            await agentsApi.RecordAgentRunResultAsync(agent.Id, 0, 0, 1, 0, ct);
            const string parkMessage =
                "The reviewer approved this PR — review and approve (or merge) to complete the task. " +
                "Enable \"Automatic code review approval\" in project settings to skip this step.";
            await EscalateToHumanReviewAsync(tasksApi, task, agent, parkMessage, ct);
            return;
        }

        var transition = await tasksApi.ApplyCodeReviewVerdictAsync(taskId, isApprove, maxReviewCycles, ct);
        if (transition is null) return;

        if (isApprove)
        {
            await agentsApi.RecordAgentRunResultAsync(agent.Id, 0, 0, 1, 0, ct);

            logger.LogInformation("Reviewer approved task {TaskId} — moving to Done (merge processor will merge the PR)", taskId);
        }
        else if (!transition.EscalatedToHuman)
        {
            logger.LogInformation("Reviewer requested changes for task {TaskId} (cycle {Cycle}) — re-queuing for dev",
                taskId, transition.ReviewCycleCount);
        }
        else
        {
            await PersistAgentMessageAsync(tasksApi, taskId, agent,
                $"Reached {maxReviewCycles} automated review cycles without approval — a human needs "
                + "to review this PR and decide. Reply here with direction, or press Re-queue, to "
                + "send it back to the developer with a fresh review budget.",
                MessageType.SystemEvent, "system", ct);

            logger.LogInformation("Task {TaskId} reached max review cycles — escalated to human review", taskId);
        }

        if (transition.NewLane != transition.FromLane)
        {
            await taskNotifier.SendLaneChangedAsync(task.ProjectId, new LaneChangedEvent(
                taskId, task.Title, transition.FromLane.ToString(), transition.NewLane.ToString(),
                transition.NewStatus.ToString(), DateTimeOffset.UtcNow));
        }
        else
        {
            await taskNotifier.SendStatusChangedAsync(task.ProjectId, new StatusChangedEvent(
                taskId, transition.NewStatus.ToString(), DateTimeOffset.UtcNow));
        }
    }

    private sealed class ReviewVerdict
    {
        public string? Decision { get; set; }
        public string? Summary { get; set; }
        public List<ReviewComment>? Comments { get; set; }
    }

    private sealed class ReviewComment
    {
        public string Path { get; set; } = "";
        public int Line { get; set; }
        public string Body { get; set; } = "";
    }

    /// <summary>Records the reviewer agent's verdict and line comments on Orkka's pull request,
    /// where the review page reads them.</summary>
    private async Task PostReviewAsync(
        IServiceScope scope, TaskSnapshot task, RepoSummary? repo,
        ReviewVerdict verdict, bool isApprove, CancellationToken ct)
    {
        if (repo is null || string.IsNullOrEmpty(task.PrUrl))
            return;

        var prNumber = ParsePrNumber(task.PrUrl);
        if (prNumber is null) return;

        try
        {
            var pullRequests = scope.ServiceProvider.GetRequiredService<IPullRequestService>();

            var comments = verdict.Comments is { Count: > 0 }
                ? verdict.Comments
                    .Where(c => !string.IsNullOrWhiteSpace(c.Path) && c.Line > 0)
                    .Select(c => new GitHubNewReviewComment(c.Path, c.Line, c.Body))
                    .ToList()
                : null;

            // A COMMENT review: the verdict itself is state in Orkka (the lane it moves to); the
            // review is the readable record of why.
            var body = $"{(isApprove ? "✅ Orkka reviewer approved this PR." : "❌ Orkka reviewer requested changes.")}\n\n{verdict.Summary}";
            var result = await pullRequests.CreateReviewAsync(
                repo.Id, prNumber.Value,
                "COMMENT", body, comments, ct);

            if (result.IsFailure)
                logger.LogWarning("Review could not be recorded for task {TaskId}: {Error}", task.Id, result.Error);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to record the review for task {TaskId} — continuing on internal state", task.Id);
        }
    }

    private async Task EscalateToHumanReviewAsync(
        ITasksApi tasksApi, TaskSnapshot task, AgentSummary? agent, string reason, CancellationToken ct)
    {
        // Nothing written means the task settled while this reviewer was running — merged, or
        // already carried to Done. Stay silent rather than posting an escalation about a pull
        // request that has landed: the message is what turns a harmless lost race into a user
        // staring at "a human should review this PR" on a task that shipped.
        if (!await tasksApi.SetTaskWaitingCodeReviewAsync(task.Id, ct))
        {
            logger.LogInformation(
                "Task {TaskId}: review escalation skipped — the task was already settled while "
                + "the reviewer ran ({Reason})", task.Id, reason);
            return;
        }

        await PersistAgentMessageAsync(tasksApi, task.Id, agent, reason, MessageType.SystemEvent, "system", ct);
        await taskNotifier.SendStatusChangedAsync(task.ProjectId, new StatusChangedEvent(
            task.Id, TaskStatus.WaitingCodeReview.ToString(), DateTimeOffset.UtcNow));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Task spend limit (pause-and-ask ceilings)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>The task's estimated list-price spend so far (<see cref="IUsageQuery.GetTaskEstimatedSpendAsync"/>,
    /// US dollars) and the spend it may reach: the project limit, or the larger allowance the
    /// requester approved at a checkpoint (0 = no limit).</summary>
    private static async Task<(decimal SpentUsd, decimal AllowanceUsd)> ReadTaskSpendAsync(
        IServiceScope scope, TaskSnapshot task, ProjectSummary? project, CancellationToken ct)
    {
        var usageQuery = scope.ServiceProvider.GetRequiredService<IUsageQuery>();
        var spend = await usageQuery.GetTaskEstimatedSpendAsync(task.OrgId, task.ProjectId, [task.Id], ct);
        var spentUsd = Math.Round(spend.GetValueOrDefault(task.Id), 4, MidpointRounding.AwayFromZero);
        var allowance = project?.Settings?.TaskSpendLimit is { } limit
            ? Math.Max(limit, task.SpendApprovedUsd ?? 0m)
            : 0m;
        return (spentUsd, allowance);
    }

    /// <summary>
    /// The phase gate of the task spend limit: with the project's limit on, a pipeline phase does
    /// not start once the task's estimated list-price spend has reached its allowance — the task
    /// parks at a spend checkpoint instead (<see cref="PauseForSpendLimitAsync"/>). Checked here,
    /// between phases, because runs execute on the developer's runner and the runner protocol
    /// carries no per-run cost ceiling. True = parked; the phase must not run.
    /// </summary>
    private async Task<bool> PauseIfSpendLimitReachedAsync(
        IServiceScope scope, ITasksApi tasksApi, TaskSnapshot task, CancellationToken ct)
    {
        var projectsApi = scope.ServiceProvider.GetRequiredService<IProjectsApi>();
        var project = await projectsApi.GetProjectSummaryAsync(task.ProjectId, ct);
        if (project?.Settings?.TaskSpendLimit is null)
            return false;

        var (spentUsd, allowanceUsd) = await ReadTaskSpendAsync(scope, task, project, ct);
        if (allowanceUsd <= 0m || spentUsd < allowanceUsd)
            return false;

        await PauseForSpendLimitAsync(scope, tasksApi, task, project, agent: null, task.CurrentLane, ct, beforePhase: true);
        return true;
    }

    /// <summary>
    /// The task reached its spend checkpoint — before a phase started, or (when a run reports it)
    /// at the end of a run: park the task in Pending Questions with a question only the
    /// requester answers — Continue (the task gets another allotment and the phase runs, from
    /// the hand-off when there is one) or Stop. The work done so far is on the branch.
    /// Deliberately never advances the pipeline: pushing a half-finished branch into review as
    /// "done" is exactly what a budget stop must not do. The metadata is what the task page
    /// renders its Continue / Stop card from (<c>spentUsd</c>, <c>limitUsd</c>,
    /// <c>allotmentUsd</c>: estimated list-price spend in US dollars).
    /// </summary>
    private async Task PauseForSpendLimitAsync(
        IServiceScope scope, ITasksApi tasksApi, TaskSnapshot task, ProjectSummary? project,
        AgentSummary? agent, Lane sourceLane, CancellationToken ct, bool beforePhase = false)
    {
        var (spentUsd, allowanceUsd) = await ReadTaskSpendAsync(scope, task, project, ct);
        var allotment = project?.Settings?.TaskSpendLimit;

        var metadata = new Dictionary<string, object?>
        {
            ["spendCheckpoint"] = new
            {
                spentUsd,
                limitUsd = allowanceUsd > 0m ? allowanceUsd : (decimal?)null,
                allotmentUsd = allotment,
                phase = sourceLane.ToString(),
            },
        };
        if (agent is not null)
        {
            metadata["agentId"] = agent.Id.ToString();
            metadata["agentName"] = agent.Name;
            metadata["agentRole"] = agent.Role.ToString();
        }

        var content = SpendCheckpointText(spentUsd, allowanceUsd, allotment, task.BranchName, beforePhase);

        await tasksApi.AddTaskMessageAsync(
            task.Id, "agent", content, MessageType.Question,
            JsonSerializer.Serialize(metadata, JsonOptions), ct);

        var fromLane = await tasksApi.MoveTaskToPendingQuestionsAsync(task.Id, sourceLane, ct);
        if (fromLane is null) return;

        await taskNotifier.SendLaneChangedAsync(task.ProjectId, new LaneChangedEvent(
            task.Id, task.Title, fromLane.Value.ToString(), Lane.PendingQuestions.ToString(),
            TaskStatus.WaitingAnswer.ToString(), DateTimeOffset.UtcNow));

        logger.LogWarning(
            "Task {TaskId} paused at its spend checkpoint in {Lane}: estimated US${Spent} of US${Limit} — waiting for the requester",
            task.Id, sourceLane, spentUsd, allowanceUsd);
    }

    /// <summary>The checkpoint question's text (culture-invariant US dollars).</summary>
    internal static string SpendCheckpointText(
        decimal spentUsd, decimal allowanceUsd, decimal? allotmentUsd, string? branchName, bool beforePhase)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var limitText = allowanceUsd > 0m
            ? string.Create(inv, $"at or above the US${allowanceUsd:N2} limit set for this task")
            : "at the platform's per-run spend limit";
        var branchText = string.IsNullOrEmpty(branchName) ? "" : $" Everything done so far is pushed to `{branchName}`.";
        var stopped = beforePhase
            ? "The next step has not started."
            : "I have stopped here rather than finish without a budget.";
        var continueText = allotmentUsd is { } more
            ? string.Create(inv, $"**Continue** approves another US${more:N2} and picks up where the task paused.")
            : "**Continue** picks up where the task paused.";
        return string.Create(inv, $"**Spend checkpoint.** This task's estimated list-price spend is US${spentUsd:N2}, {limitText}. ")
            + $"{stopped}{branchText} (Orkka bills nothing: this prices each run at the cost your AI CLI reported, or at the "
            + "model's list price for its tokens — real money on an API key or Bedrock, an estimate on a subscription.)\n\n"
            + $"{continueText} **Stop** leaves the task parked here until you decide.";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Shared phase plumbing
    // ─────────────────────────────────────────────────────────────────────────

    private sealed record AgentRunOutcome(
        string FullText, int PromptTokens, int CompletionTokens,
        int CacheReadTokens, int CacheWriteTokens, string? Error,
        string? ResultText = null, string? SessionId = null,
        decimal? VendorCostUsd = null,
        // The run ended on RunCostGuard's spend checkpoint: the agent handed off, it did not
        // finish. Phases park the task and ask (PauseForSpendLimitAsync) instead of advancing.
        bool SpendLimitReached = false)
    {
        /// <summary>True when the run streamed or produced anything at all — a resume
        /// attempt that failed before output can safely be retried cold.</summary>
        public bool ReceivedOutput =>
            FullText.Length > 0 || !string.IsNullOrEmpty(ResultText)
            || PromptTokens > 0 || CompletionTokens > 0;
    }

    /// <summary>
    /// Char-count breakdown of the prompt about to be sent — the measurement side of the
    /// token-overhead work. Savings must be attributable to a section (docs, diff, plan,
    /// conversation…), not inferred from usage totals after the fact.
    /// </summary>
    private void LogPromptSections(
        Ulid taskId, Lane lane, string system, string user,
        params (string Name, string? Text)[] sections)
    {
        if (!logger.IsEnabled(LogLevel.Information)) return;

        logger.LogInformation(
            "[PromptSize] task {TaskId} lane {Lane}: system={SystemChars} user={UserChars} {Sections}",
            taskId, lane, system.Length, user.Length,
            string.Join(" ", sections
                .Where(s => !string.IsNullOrEmpty(s.Text))
                .Select(s => $"{s.Name}={s.Text!.Length}")));
    }

    /// <summary>Attempts per agent run: the initial run plus transient-error retries.</summary>
    private const int MaxAgentRunAttempts = 3;

    /// <summary>Backoff before retry attempt N (indexed by completed attempts - 1).</summary>
    private static readonly TimeSpan[] AgentRunRetryDelays =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20)];

    /// <summary>
    /// User message for a continuation attempt. The dropped attempt's CLI session still holds
    /// the phase prompt and everything the agent did before the stream died, so the retry
    /// resumes that session and asks it to carry on — instead of replaying the phase from turn
    /// 0, which re-reads the plan, re-explores the repo and redoes work already on disk.
    /// Platform-authored — never wrapped in the untrusted-content fence.
    /// </summary>
    private const string AgentRunContinuationMessage =
        "(The connection dropped mid-run and this is an automatic retry on the same session. " +
        "Continue from exactly where you stopped — your instructions are already in this " +
        "session and the edits you had written are still in your worktree. Do not start the " +
        "task over, do not re-read context you have already read, and do not mention the " +
        "interruption.)";

    /// <summary>
    /// Runs the agent, retrying transient transport failures ("Connection closed
    /// mid-response", resets, API overload) up to <see cref="MaxAgentRunAttempts"/> times
    /// before letting the error propagate to the fail-and-block path. Retries are
    /// surfaced only as a Progress stream event — the task keeps running and the user is
    /// not bothered unless every attempt fails. Tokens burned by failed attempts are
    /// carried into the returned outcome so they are still billed.
    ///
    /// When the dropped attempt reported a CLI session id (and the invoker honors resume),
    /// the retry resumes that session with a continuation prompt — safe even after output
    /// has streamed, because the agent appends instead of replaying. Without one, a retry
    /// replays the whole phase, so it only happens while nothing has been produced yet:
    /// past that point a replay duplicates the thread's messages and redoes finished work,
    /// which costs more than the failure it is trying to paper over.
    /// </summary>
    private async Task<AgentRunOutcome> StreamAgentRunAsync(
        Ulid taskId, AgentSummary? agent, AgentRequest request, ExecutionRoute route, bool persistText,
        TimeSpan? runTimeout, CancellationToken ct,
        LiveDiffContext? liveDiff = null)
    {
        request = await WithReadOnlyDirsAsync(request, ct);
        int burnedPrompt = 0, burnedCompletion = 0, burnedCacheRead = 0, burnedCacheWrite = 0;
        // Null until some attempt actually reports one, so a provider that never reports cost
        // stays null rather than becoming a misleading 0.00.
        decimal? burnedVendorCost = null;
        // Text from dropped attempts. A continuation appends to it, so the phase's wrap-up
        // (question detection, agent log) sees the whole run and not just its last leg.
        var earlierText = new System.Text.StringBuilder();
        var earlierOutput = false;

        for (var attempt = 1; ; attempt++)
        {
            var outcome = await StreamAgentRunOnceAsync(taskId, agent, request, route, persistText, runTimeout, ct, liveDiff);

            var continueViaResume = route.Invoker.SupportsSessionResume && outcome.SessionId is not null;
            var retryable = outcome.Error is not null
                && attempt < MaxAgentRunAttempts
                && Orkka.SharedKernel.Ai.ClaudeCliErrors.IsTransient(outcome.Error)
                && !ct.IsCancellationRequested
                && (continueViaResume || (!outcome.ReceivedOutput && !earlierOutput));

            if (!retryable)
            {
                return outcome with
                {
                    FullText = earlierText.Length == 0
                        ? outcome.FullText
                        : earlierText.ToString() + outcome.FullText,
                    PromptTokens = outcome.PromptTokens + burnedPrompt,
                    CompletionTokens = outcome.CompletionTokens + burnedCompletion,
                    CacheReadTokens = outcome.CacheReadTokens + burnedCacheRead,
                    CacheWriteTokens = outcome.CacheWriteTokens + burnedCacheWrite,
                    // Matches the token treatment: a failed attempt still cost real money, so
                    // the vendor figure has to cover the same attempts our computed cost does
                    // or the two are not comparable.
                    VendorCostUsd = outcome.VendorCostUsd + burnedVendorCost
                        ?? outcome.VendorCostUsd ?? burnedVendorCost,
                };
            }

            burnedPrompt += outcome.PromptTokens;
            burnedCompletion += outcome.CompletionTokens;
            burnedCacheRead += outcome.CacheReadTokens;
            burnedCacheWrite += outcome.CacheWriteTokens;
            if (outcome.VendorCostUsd is { } attemptCost)
                burnedVendorCost = (burnedVendorCost ?? 0m) + attemptCost;
            earlierText.Append(outcome.FullText);
            earlierOutput |= outcome.ReceivedOutput;

            if (continueViaResume)
            {
                request = request with
                {
                    ResumeSessionId = outcome.SessionId,
                    UserMessage = AgentRunContinuationMessage,
                };
            }

            logger.LogWarning(
                "Transient agent-run failure for task {TaskId} (attempt {Attempt}/{Max}): {Error} — retrying{Mode}",
                taskId, attempt, MaxAgentRunAttempts, outcome.Error,
                continueViaResume ? " via session resume" : " from the start");

            await taskNotifier.SendStreamEventAsync(taskId, new StreamEvent(
                "Progress",
                continueViaResume
                    ? $"Connection to the model dropped — picking up where it left off "
                      + $"(attempt {attempt + 1} of {MaxAgentRunAttempts})…"
                    : $"Connection to the model dropped — retrying (attempt {attempt + 1} of {MaxAgentRunAttempts})…",
                DateTimeOffset.UtcNow));

            await Task.Delay(AgentRunRetryDelays[attempt - 1], ct);
        }
    }

    private async Task<AgentRunOutcome> StreamAgentRunOnceAsync(
        Ulid taskId, AgentSummary? agent, AgentRequest request, ExecutionRoute route, bool persistText,
        TimeSpan? runTimeout, CancellationToken ct,
        LiveDiffContext? liveDiff = null)
    {
        // Mirrors the worktree into the task's live code-change snapshot while the agent
        // edits (Code View). Only for runs that own a branch — planning/QA/review pass null.
        await using var liveCapture = liveDiff is null
            ? null
            : new LiveWorkspaceCapture(liveDiff, route.Git, StoreLiveSnapshotAsync, taskNotifier, logger, ct);

        var fullText = new System.Text.StringBuilder();
        var prompt = 0;
        var completion = 0;
        var cacheRead = 0;
        var cacheWrite = 0;
        string? error = null;
        string? resultText = null;
        string? sessionId = null;
        decimal? vendorCost = null;
        var spendLimitReached = false;

        var metadata = agent is not null
            ? new { agentId = agent.Id.ToString(), agentName = agent.Name, agentRole = agent.Role.ToString() }
            : null;

        // No CancelAfter when the project has no runtime limit: the run then ends when the
        // agent is done, when the invoker's own phase wall-clock budget stops it, or when the
        // caller cancels.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (runTimeout is { } limit)
            timeoutCts.CancelAfter(limit);

        // A tracker task's ticket attachments live on the developer's machine; the runner copies
        // them into the worktree when it starts the run. Asked per run, not per phase, so a
        // ticket that gained attachments mid-pipeline is current on the next one.
        if (request.TicketKey is null)
        {
            using var ticketScope = scopeFactory.CreateScope();
            if (ticketScope.ServiceProvider.GetService<Orkka.Modules.Trackers.Contracts.ITrackersApi>() is { } trackers
                && await trackers.GetTicketContextAsync(taskId, ct) is { ConnectionKey.Length: > 0 } ticket
                && ticket.Attachments.Any(a => a.Cached))
            {
                request = request with { TicketConnectionKey = ticket.ConnectionKey, TicketKey = ticket.TicketKey };
            }
        }

        try
        {
            await foreach (var chunk in route.Invoker.InvokeAsync(request, timeoutCts.Token))
            {
                switch (chunk.Type)
                {
                    case AgentChunkType.Text:
                        // The CLI streams transport failures as assistant text ("API
                        // Error: Connection lost mid-response…", "API Error … Retrying in
                        // 4 seconds…") — not agent prose. Keep them out of the thread and
                        // the accumulated run text. Never treat one as the failure itself:
                        // the CLI's own retry may recover the run, and a genuine death is
                        // followed by an Error chunk, which our retry loop acts on.
                        if (Orkka.SharedKernel.Ai.ClaudeCliErrors.IsStreamErrorNotice(chunk.Content))
                            break;

                        fullText.Append(chunk.Content);
                        liveCapture?.OnAgentProgress();
                        await taskNotifier.SendStreamEventAsync(taskId, new StreamEvent(
                            "AgentThinking", chunk.Content, DateTimeOffset.UtcNow, metadata));

                        if (persistText && !LooksLikeStructuredOutput(chunk.Content))
                        {
                            await PersistAgentTextChunkAsync(taskId, agent, chunk.Content, ct);
                        }
                        break;

                    case AgentChunkType.Thinking:
                        liveCapture?.OnAgentProgress();
                        await taskNotifier.SendStreamEventAsync(taskId, new StreamEvent(
                            "AgentThinking", chunk.Content, DateTimeOffset.UtcNow, metadata));
                        break;

                    case AgentChunkType.ToolUse:
                        // The Code View builds its live file tree from these events, so they
                        // carry structured tool/filePath metadata (parsed here once, not by
                        // every browser) and are capped: a Write of a generated file can
                        // exceed the SignalR frame budget on the worker→API link, and a
                        // dropped frame kills the whole stream. A capped payload still names
                        // the file via metadata; the exact diff comes from the stored
                        // snapshot afterwards.
                        // Progress first (a previous tool has finished), then mark this one.
                        liveCapture?.OnAgentProgress();
                        liveCapture?.OnToolUse(chunk.Content);
                        await taskNotifier.SendStreamEventAsync(taskId, new StreamEvent(
                            "ToolUse", CapToolUseContent(chunk.Content), DateTimeOffset.UtcNow,
                            BuildToolUseMetadata(chunk.Content, agent)));
                        break;

                    case AgentChunkType.Error:
                        // Keep consuming: the Done chunk with token usage follows the
                        // error — dropping it would leave the burned tokens unbilled.
                        error = chunk.Content;
                        break;

                    case AgentChunkType.Done:
                        prompt = chunk.PromptTokens;
                        completion = chunk.CompletionTokens;
                        cacheRead = chunk.CacheReadTokens;
                        cacheWrite = chunk.CacheWriteTokens;
                        sessionId = chunk.SessionId;
                        vendorCost = chunk.VendorCostUsd;
                        spendLimitReached |= chunk.SpendLimitReached;
                        if (!string.IsNullOrWhiteSpace(chunk.Content))
                            resultText = chunk.Content;
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            error = runTimeout is { } exceeded
                ? $"Agent run hit this project's task runtime limit of {exceeded.TotalMinutes:F0} minutes and was stopped."
                : "Agent run was cancelled.";
            logger.LogError("Agent run for task {TaskId} stopped after {Timeout}", taskId, runTimeout);
        }

        // Land the last edits: the permanent snapshot follows only after the phase's own
        // wrap-up, and a run that ends in an error never gets one.
        if (liveCapture is not null && !ct.IsCancellationRequested)
            await liveCapture.FlushAsync();

        return new AgentRunOutcome(
            fullText.ToString(), prompt, completion, cacheRead, cacheWrite, error, resultText, sessionId,
            vendorCost, spendLimitReached);
    }

    /// <summary>
    /// ToolUse content is "{Tool}: {input JSON}" — a Write carries the whole file. Beyond this
    /// size the tail is cut (breaking the JSON on purpose; clients fall back to the metadata
    /// from <see cref="BuildToolUseMetadata"/>). 256K chars stays far under the hubs' 1MB
    /// receive cap even as UTF-8.
    /// </summary>
    private const int MaxToolUseContentChars = 256 * 1024;

    private static string CapToolUseContent(string content) =>
        content.Length <= MaxToolUseContentChars
            ? content
            : content[..MaxToolUseContentChars] + "…[truncated]";

    /// <summary>
    /// Metadata for a ToolUse stream event: the run's agent fields plus the tool name and, for
    /// file tools, the target path — so the Code View's live tree survives capped content.
    /// Parsing is best-effort; a shape we don't recognise just yields the agent fields.
    /// </summary>
    private static object? BuildToolUseMetadata(string content, AgentSummary? agent)
    {
        string? tool = null;
        string? filePath = null;
        var colon = content.IndexOf(':');
        if (colon > 0)
        {
            var candidate = content[..colon].Trim();
            if (candidate.Length <= 64 && candidate.All(c => char.IsLetterOrDigit(c) || c is '_' or '-'))
            {
                tool = candidate;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(content[(colon + 1)..].Trim());
                    if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                        (doc.RootElement.TryGetProperty("file_path", out var p) ||
                         doc.RootElement.TryGetProperty("notebook_path", out p)) &&
                        p.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        filePath = p.GetString();
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    // Not JSON (e.g. an MCP tool's free-form input) — tool name alone is fine.
                }
            }
        }

        if (tool is null && agent is null) return null;
        return agent is not null
            ? new { agentId = agent.Id.ToString(), agentName = agent.Name, agentRole = agent.Role.ToString(), tool, filePath }
            : new { agentId = (string?)null, agentName = (string?)null, agentRole = (string?)null, tool, filePath };
    }

    private static bool LooksLikeStructuredOutput(string text)
    {
        var trimmed = text.TrimStart();
        return trimmed.StartsWith('{') || trimmed.StartsWith("```");
    }

    /// <summary>Stores one live working-tree capture in its own scope (see LiveWorkspaceCapture).</summary>
    private async Task<WorkspaceChangedEvent?> StoreLiveSnapshotAsync(
        LiveDiffContext context, string diff, bool isTruncated, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var tasksApi = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        return await tasksApi.UpsertLiveCodeChangeSnapshotAsync(
            context.TaskId, context.ExecutionId, context.RepoId, context.Lane,
            context.BranchName, context.BaseBranch, diff, isTruncated, ct);
    }

    /// <summary>
    /// Persists a streamed agent text block as a conversation message using its own
    /// short-lived scope (the streaming loop runs outside the phase's unit of work).
    /// </summary>
    private async Task PersistAgentTextChunkAsync(
        Ulid taskId, AgentSummary? agent, string content, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(content)) return;

        try
        {
            using var scope = scopeFactory.CreateScope();
            var tasksApi = scope.ServiceProvider.GetRequiredService<ITasksApi>();

            await PersistAgentMessageAsync(tasksApi, taskId, agent, content, MessageType.Text, "agent", ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist agent text chunk for task {TaskId}", taskId);
        }
    }

    private string? BuildAgentMetadataJson(AgentSummary? agent) =>
        agent is not null
            ? JsonSerializer.Serialize(new
            {
                agentId = agent.Id.ToString(),
                agentName = agent.Name,
                agentRole = agent.Role.ToString()
            }, JsonOptions)
            : null;

    private async Task PersistAgentMessageAsync(
        ITasksApi tasksApi, Ulid taskId, AgentSummary? agent,
        string content, MessageType type, string role, CancellationToken ct)
    {
        var message = await tasksApi.AddTaskMessageAsync(
            taskId, role, content, type, BuildAgentMetadataJson(agent), ct);
        if (message is null) return;

        // NewMessage is broadcast by AddTaskMessageAsync itself (the single write path).

        // Board cards preview only agent-authored messages; system messages (e.g. "Task
        // blocked") are covered by the card's own status/blocked indicators.
        if (role == "agent")
        {
            await taskNotifier.SendTaskLastMessageChangedAsync(message.ProjectId,
                new TaskLastMessageChangedEvent(
                    taskId, message.Role, TaskLastMessageChangedEvent.BuildPreview(message.Content),
                    message.MessageType.ToString(), agent?.Name, message.CreatedAt));
        }
    }

    /// <summary>
    /// Moves a task to PendingQuestions: persists the question, records which lane to
    /// resume once the human answers, and frees the agent slot.
    /// </summary>
    /// <summary>
    /// Classifies the tail of a no-commit dev run: did the agent stop because it needs an
    /// answer from the human but asked in prose, without the <question> tag the regex
    /// catches? Returns the question to surface, or null both for "not a question" and for
    /// every failure — a wrong null costs one retry cycle, while a wrong question parks a
    /// genuinely empty run on a human, so uncertainty falls through to the retry path.
    /// Only called on empty-diff runs, so the extra model call is rare and small.
    /// </summary>
    private async Task<string?> DetectProseQuestionAsync(
        IServiceScope scope, TaskSnapshot task, AgentSummary? agent, Ulid executionId,
        string runText, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(runText))
            return null;

        // Only the closing stretch matters — when a run ends on a question, the question is
        // how it ends. The rest of the log is tool chatter that would drown the signal.
        const int tailChars = 4000;
        var tail = runText.Length <= tailChars ? runText : runText[^tailChars..];

        try
        {
            var runnerUtility = scope.ServiceProvider
                .GetRequiredService<Orkka.SharedKernel.Ai.IUserRunnerUtility>();
            var options = scope.ServiceProvider
                .GetRequiredService<IOptions<Orkka.SharedKernel.Ai.AnthropicOptions>>().Value;

            var request = new Orkka.SharedKernel.Ai.AnthropicRequest
            {
                Model = options.FastUtilityModel,
                MaxTokens = 600,
                System =
                    "You read the end of an automated coding agent's run log. The run produced "
                    + "no commits. Decide which of two things happened:\n"
                    + "- The agent stopped because it is waiting on an answer, decision, or "
                    + "clarification from the human before implementing — a question, even one "
                    + "phrased as a statement like 'tell me how you'd like to proceed'.\n"
                    + "- The agent claimed the work is done, gave a status report, or ended "
                    + "without asking the human anything.\n\n"
                    + "Reply with ONLY a JSON object, no fences, no other text:\n"
                    + "{\"blockedOnHuman\": true|false, \"question\": \"...\"}\n"
                    + "When blockedOnHuman is true, \"question\" restates what the agent needs "
                    + "answered — concise but complete, in the agent's own voice, keeping the "
                    + "options it offered. When false, use an empty string.",
                Messages = [new Orkka.SharedKernel.Ai.AnthropicMessage("user", tail)],
            };

            // One retry, because this check's failure is load-bearing out of proportion to
            // its size: it decides between "park for the human's answer" and "re-run
            // developing", and the network blip that just truncated the agent's run is exactly
            // as likely to eat this one-shot call seconds later — observed doing so, which
            // turned a connection drop into a no-commit bounce.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    // On the task's own runner (their subscription, $0). No platform-key
                    // fallback: a miss throws, is retried once (a reconnect blip), and then
                    // falls through to "treat the run as empty" below.
                    var response = await runnerUtility.CompleteOnRunnerAsync(
                        task.OrgId, task.RunnerUserId ?? task.CreatedByUserId, request,
                        "check whether the agent stopped on a question", ct);

                    if (response.InputTokens > 0 || response.OutputTokens > 0)
                    {
                        var usageRecorder = scope.ServiceProvider.GetRequiredService<IUsageRecorder>();
                        await usageRecorder.RecordUsageAsync(new RecordUsageCommand(
                            task.OrgId, task.ProjectId, task.Id, agent?.Id, executionId,
                            options.FastUtilityModel, response.InputTokens, response.OutputTokens,
                            FlatCostBase: 0m,
                            UserId: task.CreatedByUserId), ct);
                    }

                    var verdict = Orkka.SharedKernel.Ai.ModelJson.Deserialize<ProseQuestionVerdict>(
                        response.Text, "no-commit question check");
                    return verdict.BlockedOnHuman && !string.IsNullOrWhiteSpace(verdict.Question)
                        ? verdict.Question.Trim()
                        : null;
                }
                catch (Exception ex) when (attempt == 1 && ex is not OperationCanceledException)
                {
                    logger.LogInformation(ex,
                        "Prose-question check attempt 1 failed for task {TaskId} — retrying once",
                        task.Id);
                    await Task.Delay(TimeSpan.FromSeconds(3), ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Prose-question check failed for task {TaskId} — treating the run as empty", task.Id);
            return null;
        }
    }

    private sealed class ProseQuestionVerdict
    {
        [System.Text.Json.Serialization.JsonPropertyName("blockedOnHuman")]
        public bool BlockedOnHuman { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("question")]
        public string? Question { get; set; }
    }

    /// <summary>How many auto-answers one task may consume across its whole life. Each one
    /// re-runs a full phase; an agent that keeps asking past this many is stuck on something
    /// answering cannot fix, and the park is the honest outcome.</summary>
    private const int MaxAutoAnswersPerTask = 5;

    /// <summary>Topics autopilot never answers on its own — a wrong pick here is not "slightly
    /// suboptimal", it is data loss or money. Substring match over question + recommendation.</summary>
    private static readonly string[] GuardedQuestionMarkers =
        ["delete", "drop ", "destroy", "wipe", "payment", "billing", "charge",
         "credential", "secret", "password", "api key", "production", "prod "];

    private static bool TouchesGuardedTopic(TaskQuestion q)
    {
        var text = $"{q.Question} {q.Recommended}";
        return GuardedQuestionMarkers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Autopilot's answer to a structured question set: pick the asking agent's own
    /// recommended option for every question and re-queue the phase, instead of parking a
    /// task nobody is around to unpark. Returns true when the questions were answered (the
    /// caller must NOT park); false means park as usual — which is still correct whenever any
    /// question lacks a recommendation, touches a guarded topic, or the task has burned its
    /// auto-answer budget.
    ///
    /// When the session was started to answer every question, the first two of those stop
    /// parking: a question without a recommendation takes its first option (or, with no
    /// options, an order to proceed on the agent's own assumption), and a guarded topic is
    /// answered like any other. Only the auto-answer cap remains — it is what ends a loop.
    /// </summary>
    private async Task<bool> TryAutoAnswerQuestionsAsync(
        ITasksApi tasksApi, TaskSnapshot task, ProjectSummary? project, AgentSummary? agent,
        List<TaskQuestion> questions, Lane sourceLane, CancellationToken ct)
    {
        if (questions.Count == 0) return false;

        var policy = EffectiveTaskPolicy.For(task, project?.Settings);
        if (!policy.AutoAnswerQuestions) return false;

        if (!policy.AnswerEveryQuestion)
        {
            if (questions.Any(q => string.IsNullOrWhiteSpace(q.Recommended)))
            {
                logger.LogInformation(
                    "Task {TaskId}: question set has entries without a recommendation — parking for a human",
                    task.Id);
                return false;
            }

            if (questions.Any(TouchesGuardedTopic))
            {
                logger.LogInformation(
                    "Task {TaskId}: question touches a guarded topic — parking for a human", task.Id);
                return false;
            }
        }
        else if (questions.Any(TouchesGuardedTopic))
        {
            logger.LogInformation(
                "Task {TaskId}: question touches a guarded topic — answering anyway, the session answers every question",
                task.Id);
        }

        var alreadyAnswered = await tasksApi.CountAutoAnsweredMessagesAsync(task.Id, ct);
        if (alreadyAnswered >= MaxAutoAnswersPerTask)
        {
            logger.LogWarning(
                "Task {TaskId}: reached {Max} auto-answers — parking for a human",
                task.Id, MaxAutoAnswersPerTask);
            return false;
        }

        // The questions land in the conversation exactly as they would when parking, so the
        // human sees what was asked; the answer message is the audit trail AND what the
        // re-run phase reads. Role "user" because that is what the phase prompts treat as an
        // answer; the content says out loud that autopilot wrote it.
        await PersistAgentMessageAsync(
            tasksApi, task.Id, agent, BuildQuestionsMessage(questions), MessageType.Question, "agent", ct);

        var answer = new System.Text.StringBuilder();
        answer.AppendLine(policy.AnswerEveryQuestion
            ? "Autopilot answered automatically — this session answers every question (override by replying here):"
            : "Autopilot answered with the recommended options (override by replying here):");
        foreach (var q in questions)
            answer.AppendLine($"- {q.Question} → **{AutoAnswerFor(q)}**");

        await tasksApi.AddTaskMessageAsync(
            task.Id, "user", answer.ToString().TrimEnd(), MessageType.Answer,
            metadataJson: """{"autoAnswered":true}""", ct: ct);

        var requeued = await tasksApi.RequeueTaskForAnswerAsync(task.Id, sourceLane, ct);
        if (!requeued) return false;

        logger.LogInformation(
            "Task {TaskId}: autopilot auto-answered {Count} question(s) — re-queued in {Lane} "
            + "(answer {Number} of {Max})",
            task.Id, questions.Count, sourceLane, alreadyAnswered + 1, MaxAutoAnswersPerTask);
        return true;
    }

    /// <summary>The option autopilot takes for one question: the agent's recommendation when
    /// it gave one, else the first option — agents list the natural default first, and any
    /// option beats a park the project asked not to have — else, for a fully open question,
    /// the standing order to decide for itself.</summary>
    private static string AutoAnswerFor(TaskQuestion q)
    {
        if (!string.IsNullOrWhiteSpace(q.Recommended)) return q.Recommended;
        var first = q.Options.FirstOrDefault(o => !string.IsNullOrWhiteSpace(o));
        return first ?? OpenQuestionAutoAnswer;
    }

    private const string OpenQuestionAutoAnswer =
        "No preference — make the most reasonable choice yourself, state it as an explicit assumption, and proceed.";

    /// <summary>
    /// The developing-lane counterpart of <see cref="TryAutoAnswerQuestionsAsync"/> for a
    /// free-text <c>&lt;question&gt;</c>. There is nothing to pick, so it only ever fires under
    /// a session started to answer every question: the question is surfaced in the
    /// conversation, answered with an order to decide, and the phase re-runs on the branch the
    /// developer already pushed. Same audit trail and the same loop cap as the structured path.
    /// </summary>
    private async Task<bool> TryAutoAnswerDevQuestionAsync(
        ITasksApi tasksApi, TaskSnapshot task, ProjectSummary? project, AgentSummary? agent,
        string question, CancellationToken ct)
    {
        if (!EffectiveTaskPolicy.For(task, project?.Settings).AnswerEveryQuestion) return false;

        var alreadyAnswered = await tasksApi.CountAutoAnsweredMessagesAsync(task.Id, ct);
        if (alreadyAnswered >= MaxAutoAnswersPerTask)
        {
            logger.LogWarning(
                "Task {TaskId}: reached {Max} auto-answers on a developer question — parking for a human",
                task.Id, MaxAutoAnswersPerTask);
            return false;
        }

        await PersistAgentMessageAsync(tasksApi, task.Id, agent, question, MessageType.Question, "agent", ct);
        await tasksApi.AddTaskMessageAsync(
            task.Id, "user", ProceedOnOwnAssumptionAnswer, MessageType.Answer,
            metadataJson: """{"autoAnswered":true}""", ct: ct);

        var requeued = await tasksApi.RequeueTaskForAnswerAsync(task.Id, Lane.Developing, ct);
        if (!requeued) return false;

        logger.LogInformation(
            "Task {TaskId}: autopilot answered a developer question with an order to decide — re-queued in Developing "
            + "(answer {Number} of {Max})",
            task.Id, alreadyAnswered + 1, MaxAutoAnswersPerTask);
        return true;
    }

    private async Task MoveToPendingQuestionsAsync(
        ITasksApi tasksApi, TaskSnapshot task, AgentSummary? agent,
        string questionContent, Lane sourceLane, CancellationToken ct)
    {
        await PersistAgentMessageAsync(tasksApi, task.Id, agent, questionContent, MessageType.Question, "agent", ct);

        var fromLane = await tasksApi.MoveTaskToPendingQuestionsAsync(task.Id, sourceLane, ct);
        if (fromLane is null) return;

        await taskNotifier.SendLaneChangedAsync(task.ProjectId, new LaneChangedEvent(
            task.Id, task.Title, fromLane.Value.ToString(), Lane.PendingQuestions.ToString(),
            TaskStatus.WaitingAnswer.ToString(), DateTimeOffset.UtcNow));

        logger.LogInformation("Task {TaskId} moved to PendingQuestions (resumes in {SourceLane} on answer)",
            task.Id, sourceLane);
    }

    /// <summary>
    /// Marks the execution completed (one Tasks-module save), then records usage
    /// attributed to the agent, updates agent token counters, and extracts a memory.
    /// </summary>
    private async Task CompleteExecutionAsync(
        IServiceScope scope, ITasksApi tasksApi, TaskSnapshot task, Ulid executionId,
        ProjectSummary? project, AgentSummary? agent, AgentRunOutcome outcome,
        string? agentLog, ExecutionRoute route, CancellationToken ct)
    {
        var execution = await tasksApi.CompleteExecutionAsync(
            task.Id, executionId, outcome.PromptTokens, outcome.CompletionTokens, agentLog,
            outcome.SessionId, ct);
        if (execution is null)
        {
            logger.LogWarning("Execution {ExecutionId} for task {TaskId} not found while completing", executionId, task.Id);
            return;
        }

        await RecordRunResultsAsync(scope, task, project, agent, outcome, execution, route, ct);
    }

    /// <summary>Post-save bookkeeping shared by the execution-completion paths.</summary>
    private async Task RecordRunResultsAsync(
        IServiceScope scope, TaskSnapshot task, ProjectSummary? project, AgentSummary? agent,
        AgentRunOutcome outcome, ExecutionSnapshot execution, ExecutionRoute route, CancellationToken ct)
    {
        var agentsApi = scope.ServiceProvider.GetRequiredService<IAgentsApi>();
        if (agent is not null)
        {
            // Counters live in the Agents module - set-based update, same semantics as the
            // previous tracked-entity increments.
            await agentsApi.RecordAgentRunResultAsync(
                agent.Id, outcome.PromptTokens, outcome.CompletionTokens, 0, 0, ct);
        }

        var usageModel = ResolveModel(task, agent, project) ?? "claude-sonnet-5";
        var usageRecorder = scope.ServiceProvider.GetRequiredService<IUsageRecorder>();
        // Every run consumes the developer's own AI subscription — record the usage at $0 so
        // nothing is billed for the tokens.
        await usageRecorder.RecordUsageAsync(new RecordUsageCommand(
            task.OrgId, task.ProjectId, task.Id, agent?.Id, execution.Id,
            usageModel, outcome.PromptTokens, outcome.CompletionTokens,
            outcome.CacheReadTokens, outcome.CacheWriteTokens,
            FlatCostBase: 0m,
            UserId: task.CreatedByUserId,
            VendorReportedCostUsd: outcome.VendorCostUsd), ct);

        if (agent is not null)
        {
            try
            {
                await memoryExtractor.ExtractAndSaveAsync(agentsApi, execution, agent, task.Title, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Memory extraction failed for execution {ExecutionId}", execution.Id);
            }
        }
    }

    private sealed record WorkspaceResult(
        string? WorktreePath,
        string? BranchName, string? Error,
        // Why the worktree can't run the app, when a setup command was configured and failed.
        // Null means "prepared" — including the common case of no setup command at all, since
        // a repo that needs no preparation is not a repo that failed to prepare.
        string? SetupFailure = null,
        // What merging the base branch in did. Null when the sync was skipped or unavailable
        // (old runner, dirty worktree) — which is the pre-base-sync behavior, not an error.
        BaseSyncOutcome? BaseSync = null,
        // What this task actually forked from — the repo's default branch. Carried on the
        // result so that everything downstream of setup — the base sync, the diff, the PR —
        // targets the SAME branch the worktree was created from.
        string? BaseBranch = null,
        // A Story member's starting commit on the shared branch (see TaskEntity.ReviewBaseSha).
        string? ReviewBaseSha = null);

    /// <summary>The task's diff for review, snapshots and QA. A Story member's shows its own
    /// change (<c>ReviewBaseSha..HEAD</c>), not the whole shared branch; every other task's is
    /// the usual <c>origin/{base}...HEAD</c>.</summary>
    private static Task<Result<string>> TaskDiffAsync(
        ExecutionRoute route, TaskSnapshot task, Ulid repoId, string baseBranch,
        int maxChars = 60_000, CancellationToken ct = default)
        => task.ReviewBaseSha is { } baseSha
            ? route.Git.GetDiffSinceAsync(task.OrgId, repoId, task.Id, baseBranch, baseSha, maxChars, task.BranchName, ct)
            : route.Git.GetDiffAsync(task.OrgId, repoId, task.Id, baseBranch, maxChars, task.BranchName, ct);

    /// <summary>
    /// How a phase wants the base branch merged into the task worktree before it runs.
    ///
    /// The split is about who can be handed conflict markers. A phase whose agent writes code
    /// can resolve them and should, while it still holds both sides in context — that is the
    /// entire point of syncing early instead of at merge time. A phase that only reads
    /// (review, QA) cannot, so a conflict there rolls back and becomes another dev pass.
    /// </summary>
    private enum BaseSyncMode
    {
        /// <summary>Merge, and leave conflicts staged for the agent about to run.</summary>
        AgentResolves,

        /// <summary>Merge only if it is clean; roll back and report otherwise.</summary>
        CleanOnly
    }

    /// <summary>
    /// How a conflict-resolver run that errored is recorded. A run the environment cut off —
    /// the runner disconnected, or was never connected — is EnvironmentBlocked: the resolver
    /// never got to try, and the merge sweep gives the task its automatic attempt back instead
    /// of handing the conflict to a dev pass (prod, 2026-09-17). Anything else is an attempt
    /// that ran and failed.
    /// </summary>
    internal static MergeFailureKind ResolverFailureKind(string? agentError)
        => MergeBlockedReasons.IsEnvironmentBlocked(agentError)
            ? MergeFailureKind.EnvironmentBlocked
            : MergeFailureKind.Unknown;

    /// <summary>
    /// Ensures the bare clone and the task worktree exist, then persists the task's
    /// branch name. Idempotent: re-entering a phase reuses/continues the existing branch.
    /// </summary>
    private async Task<WorkspaceResult> SetupGitWorkspaceAsync(
        IServiceScope scope, ITasksApi tasksApi, TaskSnapshot task, RepoSummary? repo, ExecutionRoute route,
        CancellationToken ct, BaseSyncMode baseSync = BaseSyncMode.CleanOnly)
    {
        if (repo is null)
            return new WorkspaceResult(null, null, "Task has no repository assigned.");

        var cloneResult = await route.Git.EnsureBareCloneAsync(task.OrgId, repo.Id, repo.GitUrl, repo.LocalCheckoutPath, ct);

        if (cloneResult.IsFailure)
        {
            logger.LogError("Bare clone failed for task {TaskId}: {Error}", task.Id, cloneResult.Error);

            // The single place every phase's "could not set up repository workspace" message
            // comes from, so wording it here words it everywhere. Raw git stderr describes our
            // subprocess flags, not anything the reader can act on; the diagnostics name the
            // machine that ran git so the reader knows whose network or install to look at.
            return new WorkspaceResult(
                null, null,
                RepoAccessDiagnostics.DescribeCloneFailure(
                    repo.Name, cloneResult.Error,
                    onUserMachine: true));
        }

        // Friendly naming: branch orkka/{KEY}-{n} and a matching worktree folder, so
        // "orkka/BRW-7" beats "orkka/task-01KXHV…" in git and on disk. Existing tasks keep
        // their persisted branch; anything unresolvable falls back to the ULID convention.
        string? friendlyName = null;
        try
        {
            var projectsApi = scope.ServiceProvider.GetRequiredService<IProjectsApi>();
            var project = await projectsApi.GetProjectSummaryAsync(task.ProjectId, ct);
            if (!string.IsNullOrWhiteSpace(project?.Key))
                friendlyName = $"{project!.Key}-{task.Number}";
        }
        catch { /* fall back to ULID naming */ }

        // A reset task must start from a clean branch: reset deletes the worktree but not
        // the pushed branch (no branch-delete capability), and CreateWorktreeAsync resumes
        // from an existing remote branch — so the -r{n} suffix makes the name fresh.
        var resetSuffix = task.ResetCount > 0 ? $"-r{task.ResetCount}" : "";

        // A tracker ticket's branch follows its ticket source's template (default
        // "{type}/{key}-{slug}") — the key in the name is what links the branch, and the pull
        // request opened from it, to the ticket in Jira's development panel and ADO.
        string? ticketBranch = null;
        // A Story member always works on its Story's shared branch (a reset clears the task's
        // branch; the story's is where the previous members' code is).
        if (task.BranchName is null && task.GroupId is not null && task.StoryOrder is not null
            && await tasksApi.GetStoryForTaskAsync(task.Id, ct) is { BranchName.Length: > 0 } story)
            task = task with { BranchName = story.BranchName };
        if (task.BranchName is null && task.ExternalKey is { } ticketKey)
        {
            try
            {
                var ticket = await (scope.ServiceProvider.GetService<Orkka.Modules.Trackers.Contracts.ITrackersApi>()
                    ?.GetTicketContextAsync(task.Id, ct) ?? Task.FromResult<Orkka.Modules.Trackers.Contracts.TaskTicketContext?>(null));
                ticketBranch = TrackerBranchName.Render(ticket?.BranchTemplate, task.Type, ticketKey, task.Title);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Task {TaskId}: could not read its ticket's branch template", task.Id);
            }
        }

        var branchName = task.BranchName
            ?? (ticketBranch is not null
                ? $"{ticketBranch}{resetSuffix}"
                : friendlyName is not null
                    ? $"orkka/{friendlyName}{resetSuffix}"
                    : $"orkka/task-{task.Id}{resetSuffix}");

        // Every task forks from, and opens its pull request against, the repository's default
        // branch. Orkka creates no branches of its own on anyone's forge.
        var baseBranch = repo.DefaultBranch;

        var worktreeResult = await route.Git.CreateWorktreeAsync(
            task.OrgId, repo.Id, task.Id, branchName,
            baseBranch, friendlyName, ct);

        if (worktreeResult.IsFailure)
        {
            logger.LogError("Worktree creation failed for task {TaskId}: {Error}", task.Id, worktreeResult.Error);
            return new WorkspaceResult(
                null, null, worktreeResult.Error, BaseBranch: baseBranch);
        }

        // Persisted only after the worktree exists — same flush point as the original
        // tracked-context save.
        await tasksApi.SetTaskBranchAsync(task.Id, branchName, ct);

        // A Story member records where it starts on the shared branch, once, before it commits
        // anything: the previous member's pushed head, or the merge-base for the first. Its
        // review and diffs are that..HEAD — its own change.
        var reviewBaseSha = task.ReviewBaseSha;
        if (reviewBaseSha is null && task.StoryOrder is not null && task.GroupId is not null)
        {
            reviewBaseSha = await route.Git.ResolveReviewBaseAsync(task.OrgId, repo.Id, task.Id, branchName, repo.DefaultBranch, ct);
            if (reviewBaseSha is not null)
                await tasksApi.SetTaskReviewBaseShaAsync(task.Id, reviewBaseSha, ct);
        }
        // The base this work is built on is the base its PR must open against — recorded, not
        // re-derived, so a later change of the repo's default branch cannot retarget it.
        await tasksApi.SetTaskBaseBranchAsync(task.Id, baseBranch, ct);

        // Bring the base branch in BEFORE the phase runs. Without this a task develops, gets
        // reviewed and gets QA'd against the commit it forked from, and first meets whatever
        // its siblings merged in the meantime at squash-merge time — with nobody left holding
        // the context to resolve it. A sync that cannot run (the base cannot be fetched) stops
        // the phase here with its reason: continuing on a stale base only moves the failure to
        // the push, a whole run later.
        var (syncOutcome, syncError) = await SyncBaseBranchAsync(task, repo, route, baseSync, baseBranch, ct);
        if (syncError is not null)
            return new WorkspaceResult(null, branchName, syncError, BaseBranch: baseBranch);

        var setupFailure = await RunSetupCommandAsync(scope, task, repo, route, ct);

        // Persisted, not just streamed: the person this matters to is whoever opens the
        // worktree to run the app, and they may never scroll the activity feed. Also
        // cleared here on success, so a fixed setup stops nagging.
        await tasksApi.SetTaskSetupFailureAsync(task.Id, setupFailure, ct);

        return new WorkspaceResult(
            worktreeResult.Value, branchName, null, setupFailure,
            syncOutcome, baseBranch, reviewBaseSha);
    }

    /// <summary>
    /// Merges the base branch into the task worktree for a phase that asked for it. Whatever
    /// the previous run left uncommitted is committed first as the platform (a run that died
    /// past its publish step must not strand the task). Returns the outcome, or the error that
    /// stops the phase: the base could not be fetched, the merge could not run.
    /// </summary>
    private async Task<(BaseSyncOutcome? Outcome, string? Error)> SyncBaseBranchAsync(
        TaskSnapshot task, RepoSummary repo, ExecutionRoute route, BaseSyncMode mode,
        string baseBranch, CancellationToken ct)
    {
        var result = await route.Git.SyncBaseBranchAsync(
            task.OrgId, repo.Id, task.Id, baseBranch,
            leaveConflictsInProgress: mode == BaseSyncMode.AgentResolves,
            commitLeftovers: true, ct: ct);

        if (result.IsFailure)
        {
            logger.LogWarning(
                "Task {TaskId}: could not merge {BaseBranch} into the worktree — {Error}",
                task.Id, baseBranch, result.Error);
            return (null, $"The base branch `{baseBranch}` could not be brought into the workspace: {result.Error}");
        }

        var outcome = result.Value!;
        if (outcome.Status == BaseSyncStatus.AlreadyCurrent)
            return (outcome, null);

        var message = outcome.Status switch
        {
            BaseSyncStatus.Merged =>
                $"Picked up {outcome.CommitsBehind} new commit(s) from `{baseBranch}`.",
            BaseSyncStatus.Conflicted =>
                $"`{baseBranch}` moved by {outcome.CommitsBehind} commit(s) and collides with this branch in "
                + $"{outcome.ConflictedFiles.Count} file(s) — resolving as part of this pass.",
            _ =>
                $"`{baseBranch}` moved by {outcome.CommitsBehind} commit(s) and collides with this branch in "
                + $"{outcome.ConflictedFiles.Count} file(s) — sending it back for another dev pass.",
        };
        await SendProgressAsync(task.Id, null, message);

        return (outcome, null);
    }

    /// <summary>
    /// Prepares the worktree so the app can actually start — a fresh checkout has no
    /// dependencies and no env, which is why the QA lane's run command fails and why a
    /// developer opening the worktree can't run what the agent wrote.
    ///
    /// Returns a human-readable failure, or null when the worktree is usable. Never throws
    /// and never fails the phase: an agent reading and writing code does not need
    /// node_modules, so only the lanes that must *run* the app treat this as fatal.
    /// </summary>
    private async Task<string?> RunSetupCommandAsync(
        IServiceScope scope, TaskSnapshot task, RepoSummary repo, ExecutionRoute route,
        CancellationToken ct)
    {
        var command = await ResolveSetupCommandAsync(scope, task, repo, route, ct);
        if (command is null)
            return null;

        try
        {
            // Runs on the customer's machine, where the command is theirs.
            var result = await route.Git.RunSetupCommandAsync(
                task.OrgId, repo.Id, task.Id, command, ct);

            if (result.IsFailure)
            {
                logger.LogWarning(
                    "Setup command unavailable for task {TaskId}: {Error}", task.Id, result.Error);
                return result.Error;
            }

            var outcome = result.Value!;
            if (outcome.ApprovalNeeded is { } approval)
            {
                await NoteCommandAwaitingApprovalAsync(task.Id, approval, ct);
                return approval;
            }

            if (outcome.Skipped)
                return null;

            if (outcome.ExitCode == 0)
            {
                await SendProgressAsync(task.Id, null,
                    $"Prepared the workspace ({outcome.DurationSeconds:0}s).");
                return null;
            }

            // The tail is where an install says why it died; the head is usually banner noise.
            var output = outcome.Output ?? "";
            var detail = output.Length == 0
                ? ""
                : $"\n{(output.Length <= 1_000 ? output : "…" + output[^1_000..])}";
            await SendProgressAsync(task.Id, null,
                $"Setup command failed (exit {outcome.ExitCode}). The app may not start.{detail}");

            return $"Setup command failed (exit {outcome.ExitCode}).";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Setup command errored for task {TaskId}", task.Id);
            return $"Setup command could not run: {ex.Message}";
        }
    }

    /// <summary>
    /// The commands the repository's own <c>orkka.json</c> declares on its default branch, read
    /// through the task's runner. The fallback for a repo whose settings leave setup, check or
    /// build empty — and the one source the runner accepts without the developer's approval.
    /// Never throws; an unreadable manifest declares nothing.
    /// </summary>
    private async Task<RepoCommands> ReadManifestCommandsAsync(
        TaskSnapshot task, RepoSummary repo, ExecutionRoute route, CancellationToken ct)
    {
        try
        {
            var manifest = await route.Repo.GetFileContentAsync(
                task.OrgId, repo.Id, repo.DefaultBranch, OrkkaManifest.FileName, ct);
            return manifest.IsSuccess ? OrkkaManifest.TryReadCommands(manifest.Value) : RepoCommands.None;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Task {TaskId}: could not read {Manifest}", task.Id, OrkkaManifest.FileName);
            return RepoCommands.None;
        }
    }

    /// <summary>
    /// A repository command the developer's runner would not run, because nothing they control
    /// vouches for it (orkka.json on the default branch, or their approval on the machine).
    /// Persisted in the thread — not just streamed — because the person who has to act is at
    /// that machine's terminal and may only see it later. The phase carries on as if the
    /// command could not run; approving it makes the next run use it.
    /// </summary>
    private async Task NoteCommandAwaitingApprovalAsync(Ulid taskId, string message, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ITasksApi>()
                .AddTaskMessageAsync(taskId, "system", message, MessageType.SystemEvent, ct: ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Task {TaskId}: could not post the command-approval note", taskId);
        }
    }

    private Task SendProgressAsync(Ulid taskId, AgentSummary? agent, string message) =>
        taskNotifier.SendStreamEventAsync(taskId, new StreamEvent(
            "Progress", message, DateTimeOffset.UtcNow,
            agent is not null
                ? new { agentId = agent.Id.ToString(), agentName = agent.Name, agentRole = agent.Role.ToString() }
                : null));

    // ─────────────────────────────────────────────────────────────────────────
    // Prompt building
    // ─────────────────────────────────────────────────────────────────────────

    // The Agent SDK passes the assembled system prompt to claude.exe as a --system-prompt
    // command-line argument, and Windows caps the whole command line at ~32K chars
    // (CreateProcess error 206). Persona, user instructions, and memories are the only
    // system-prompt sections that grow with user data — budget them so the final prompt
    // stays comfortably under the limit even with the largest fixed phase template.
    // (Task context, documents, plans, and diffs ride in the user message, which the SDK
    // streams over stdin — they never touch the command line.)
    private const int PersonaCharCap = 12_000;
    private const int UserInstructionsCharCap = 4_000;
    private const int MemoriesSectionCharBudget = 4_000;

    /// <summary>
    /// Persona + the team's own instructions, as the head of a phase system prompt.
    /// <para>
    /// <paramref name="codeWritingPhase"/> opts the caller into the code-comment policy, which is
    /// injected here rather than baked only into the seeded persona: <c>ProjectAgent.SystemPrompt</c>
    /// is a DB column stamped at seed time, so agents that already exist — and custom agents with a
    /// hand-written persona — would otherwise never see it. It is skipped when the persona already
    /// carries the marker (a freshly seeded agent), so nobody pays for it twice, and it sits BEFORE
    /// the team instructions on purpose so "comment every function" from the Team page wins.
    /// </para>
    /// Defaults to false so a new call site fails safe (no rule) instead of leaking a code-writing
    /// rule into a review or QA prompt.
    /// </summary>
    private static string BuildPersonaSection(AgentSummary agent, bool codeWritingPhase = false)
    {
        var persona = agent.SystemPrompt.Replace("{AgentName}", agent.Name);
        if (persona.Length > PersonaCharCap)
            persona = persona[..PersonaCharCap] + "\n[... truncated]";

        // The engineering bar first: it is the broadest rule (every code-writing role, every
        // code-writing phase) and the one agents seeded before it existed are most likely to
        // lack. Same marker guard, same position — before the team's instructions.
        if (codeWritingPhase
            && agent.Role.IsCodeWritingRole()
            && !persona.Contains(SharedPromptBlocks.EngineeringBarMarker, StringComparison.Ordinal))
        {
            persona += SharedPromptBlocks.EngineeringBar;
        }

        if (codeWritingPhase
            && agent.Role.IsCodeWritingRole()
            && !persona.Contains(SharedPromptBlocks.CodeCommentPolicyMarker, StringComparison.Ordinal))
        {
            persona += SharedPromptBlocks.CodeCommentPolicy;
        }

        // Same reasoning as the comment policy, and the same reason it cannot live in
        // DefaultPrompts alone: SystemPrompt is stamped once at seed time, so every project
        // that already exists would keep building UI to no bar at all.
        if (codeWritingPhase
            && agent.Role.IsUiBuildingRole()
            && !persona.Contains(SharedPromptBlocks.VisualBarMarker, StringComparison.Ordinal))
        {
            persona += SharedPromptBlocks.VisualBar;
        }

        if (!string.IsNullOrWhiteSpace(agent.UserInstructions))
        {
            var instructions = agent.UserInstructions.Length > UserInstructionsCharCap
                ? agent.UserInstructions[..UserInstructionsCharCap] + "\n[... truncated]"
                : agent.UserInstructions;
            persona += $"""


                ## Additional instructions from your team
                {instructions}
                """;
        }

        return persona;
    }

    // Skills ride in the system prompt as one line each (slug + description); the bodies go
    // to the worktree so the agent reads only the one that matches. When files can't be
    // written (no repo, old runner) the bodies are inlined under this budget instead.
    private const int SkillsInlineCharBudget = 6_000;
    private const int SkillDescriptionCharCap = 300;

    /// <summary>
    /// The agent's assigned skills (project playbooks) as a prompt section, after writing each
    /// SKILL.md under <c>.orkka/context/skills/&lt;slug&gt;/</c> in the task worktree. Empty when
    /// the agent has none. Never fails the run — a skill that can't be materialized is inlined
    /// or, past the budget, listed by name only.
    /// </summary>
    private async Task<string> LoadSkillsSectionAsync(
        IAgentsApi agentsApi, AgentSummary agent, TaskSnapshot task, RepoSummary? repo,
        ExecutionRoute route, CancellationToken ct)
    {
        IReadOnlyList<AgentSkillSummary> skills;
        try
        {
            skills = await agentsApi.ListAgentSkillsAsync(agent.Id, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not load skills for agent {AgentId}; running without them", agent.Id);
            return "";
        }
        if (skills.Count == 0) return "";

        var written = false;
        if (repo is not null)
        {
            var files = skills
                .Select(s => new WorktreeContextFile($"skills/{s.Slug}/SKILL.md", s.Content))
                .ToList();
            var write = await route.Git.WriteWorktreeContextAsync(task.OrgId, repo.Id, task.Id, files, ct);
            written = write.IsSuccess;
            if (!written)
                logger.LogInformation("Skills-as-files unavailable for task {TaskId} ({Error}) — inlining",
                    task.Id, write.Error);
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine();
        sb.AppendLine("## Skills available to you");
        sb.AppendLine(written
            ? "Project playbooks assigned to you. When the assignment matches one, READ its SKILL.md with the Read tool and follow it."
            : "Project playbooks assigned to you. When the assignment matches one, follow it.");
        var inlineUsed = 0;
        foreach (var skill in skills)
        {
            var description = skill.Description.Length > SkillDescriptionCharCap
                ? skill.Description[..SkillDescriptionCharCap] + " […]"
                : skill.Description;
            if (written)
            {
                sb.AppendLine($"- **{skill.Name}** (`{skill.Slug}`) — {description} → `.orkka/context/skills/{skill.Slug}/SKILL.md`");
                continue;
            }

            var body = skill.Content.Trim();
            if (body.Length > 0 && inlineUsed + body.Length <= SkillsInlineCharBudget)
            {
                inlineUsed += body.Length;
                sb.AppendLine($"- **{skill.Name}** (`{skill.Slug}`) — {description}");
                sb.AppendLine("  <skill>");
                sb.AppendLine(body);
                sb.AppendLine("  </skill>");
            }
            else
            {
                sb.AppendLine($"- **{skill.Name}** (`{skill.Slug}`) — {description}");
            }
        }
        return sb.ToString();
    }

    /// <summary>The project-memory block for this run: retrieved by relevance to the task
    /// (title + description drive the full-text rank; pinned rules always come first) and
    /// rendered by the shared section so pipeline and chat read memory the same way. When the
    /// run carries the tool server the block also says how to add to the memory.</summary>
    private static async Task<string> LoadMemoriesSectionAsync(
        IAgentsApi agentsApi, AgentSummary agent, TaskSnapshot task, bool toolsAvailable, CancellationToken ct)
    {
        var description = task.Description ?? "";
        if (description.Length > 1_500) description = description[..1_500];
        var memories = await agentsApi.RetrieveMemoriesAsync(
            new MemoryQuery(agent.OrgId, agent.ProjectId, agent.Id, task.Title + " " + description,
                Take: 20, CharBudget: MemoriesSectionCharBudget), ct);
        return MemoryPromptSection.Render(memories, MemoriesSectionCharBudget, includeToolHint: toolsAvailable);
    }

    /// <summary>
    /// Renders the human/agent conversation (questions, answers, reviewer feedback)
    /// so re-invocations have full context. Agent narration is excluded to save tokens.
    /// <paramref name="since"/> limits to messages after that time — resumed runs already
    /// hold the earlier conversation in their CLI session transcript.
    /// </summary>
    private static async Task<string> BuildConversationSectionAsync(
        ITasksApi tasksApi, Ulid taskId, DateTimeOffset? since, CancellationToken ct,
        string heading = "Conversation so far",
        // Exclusive upper bound — see ITasksApi. Only interactive turns pass it.
        DateTimeOffset? until = null)
    {
        var messages = await tasksApi.GetConversationMessagesAsync(taskId, since, ct, until);

        if (messages.Count == 0) return "";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"## {heading}");
        sb.AppendLine("Messages between the humans and agents on this task (oldest first). " +
                      "Answers to earlier questions are authoritative — follow them.");
        sb.AppendLine();

        foreach (var m in messages)
        {
            var label = (m.Role, m.MessageType) switch
            {
                (_, MessageType.Question) => "Agent asked",
                ("user", MessageType.Answer) => "Human answered",
                ("user", _) => "Human said",
                ("reviewer", MessageType.CodeReview) => "Code review feedback",
                ("reviewer", _) => "Reviewer feedback",
                ("qa", MessageType.QaReport) => "QA test report — failures to fix",
                ("qa", _) => "QA feedback",
                ("system", MessageType.QaReport) => "Automated check failed — output to fix",
                ("system", _) => "System note",
                ("tracker", _) => "Comment on the ticket",
                _ => m.Role
            };
            sb.AppendLine($"### {label}");
            sb.AppendLine(PromptSafety.Wrap("message", m.Content));
            sb.AppendLine();
        }

        // Keep the tail if it grows too large — recent context matters most
        const int maxChars = 12_000;
        var rendered = sb.ToString();
        if (rendered.Length > maxChars)
            rendered = $"## {heading} (truncated — showing most recent)\n..." + rendered[^maxChars..];

        return rendered;
    }

    /// <summary>
    /// The other repositories of a folder-of-repos project, for reference: where the developer
    /// keeps them and what each is for (its routing description), plus the rule that the
    /// developer's own checkout of THIS repository is off limits — the agent works in its own
    /// clone, and the checkout may hold the developer's uncommitted work. Empty for a monorepo,
    /// a project without a base folder, or a project with one repository.
    /// </summary>
    internal static string BuildSiblingReposSection(
        ProjectSummary? project, RepoSummary? repo, IReadOnlyList<RepoSummary> repos)
    {
        if (project is not { BaseFolderShape: BaseFolderShape.MultiRepo, BaseFolder: { Length: > 0 } baseFolder })
            return "";

        var siblings = repos
            .Where(r => r.Id != repo?.Id && r.FolderPath is not null && r.MissingSince is null)
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (siblings.Count == 0)
            return "";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Sibling repositories (read-only reference)");
        sb.AppendLine($"This project's other repositories, as the developer has them checked out under `{baseFolder}`:");
        foreach (var sibling in siblings)
        {
            var path = sibling.LocalCheckoutPath ?? BaseFolders.CheckoutPath(baseFolder, sibling.FolderPath);
            sb.Append($"- **{sibling.Name}** — `{path}`");
            if (!string.IsNullOrWhiteSpace(sibling.Description))
                sb.Append($" — {sibling.Description.Trim()}");
            sb.AppendLine();
        }

        var ownCheckout = repo?.LocalCheckoutPath
            ?? (repo?.FolderPath is { } folder ? BaseFolders.CheckoutPath(baseFolder, folder) : null);
        sb.AppendLine(ownCheckout is not null
            ? $"Your working copy is the current directory. `{ownCheckout}` is the developer's own checkout — "
              + "never read from or edit it; read other repos only for reference."
            : "Your working copy is the current directory. Read other repos only for reference.");
        sb.AppendLine("Never write, create or delete files in any of them, and never read their `.env` files.");
        return sb.ToString();
    }

    private static async Task<string?> LoadSiblingReposSectionAsync(
        IServiceScope scope, ProjectSummary? project, RepoSummary? repo, CancellationToken ct)
    {
        if (project is not { BaseFolderShape: BaseFolderShape.MultiRepo })
            return null;

        var repos = await scope.ServiceProvider.GetRequiredService<IProjectsApi>().ListReposAsync(project.Id, ct);
        var section = BuildSiblingReposSection(project, repo, repos);
        return section.Length == 0 ? null : section;
    }

    /// <summary>
    /// A folder-of-repos project's base folder joins the run's read-only folders, so the agent
    /// can actually read the siblings the prompt names (Claude/Copilot `--add-dir`). Writes stay
    /// confined to the worktree and dotenv files outside it stay unreadable on the runner.
    /// </summary>
    private async Task<AgentRequest> WithReadOnlyDirsAsync(AgentRequest request, CancellationToken ct)
    {
        if (request.ReadOnlyDirs is not null || request.ProjectId == default)
            return request;

        try
        {
            using var scope = scopeFactory.CreateScope();
            var project = await scope.ServiceProvider.GetRequiredService<IProjectsApi>()
                .GetProjectSummaryAsync(request.ProjectId, ct);
            return project is { BaseFolderShape: BaseFolderShape.MultiRepo, BaseFolder: { Length: > 0 } baseFolder }
                ? request with { ReadOnlyDirs = [baseFolder] }
                : request;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not resolve the base folder of project {ProjectId}", request.ProjectId);
            return request;
        }
    }

    private static string BuildTaskContextSection(
        TaskSnapshot task, RepoSummary? repo, string? contextFiles, string? conversation,
        string? siblingRepos = null)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(PromptSafety.Notice);
        sb.AppendLine();
        sb.AppendLine("Task title:");
        sb.AppendLine(PromptSafety.Wrap("task-title", task.Title));
        sb.AppendLine($"Type: {task.Type}");
        sb.AppendLine("Task description:");
        sb.AppendLine(PromptSafety.Wrap("task-description", task.Description ?? "No description provided."));
        sb.AppendLine($"Repository: {repo?.Name ?? "Unknown"}");
        sb.AppendLine($"Priority: {task.Priority}");

        if (task.ExternalKey is { } ticketKey)
        {
            sb.AppendLine($"Ticket: {ticketKey}{(task.ExternalUrl is { } url ? $" ({url})" : "")}"
                + (task.ExternalEpic is { } epic ? $", under epic {epic}" : ""));
            sb.AppendLine($"Prefix the commit message with {ticketKey} so the tracker links the work to the ticket.");
        }

        if (task.WorkingSubpath is { } subpath)
            sb.AppendLine($"Work inside `{subpath}` — this ticket belongs to that part of the repository. "
                + "Read outside it when you need to, but keep your changes inside it unless the task truly requires otherwise.");

        if (task.ExternalKey is not null && task.Description?.Contains("## Attachments", StringComparison.Ordinal) == true)
            sb.AppendLine("The ticket's attachments that could be downloaded are in `.orkka/context/attachments/`.");

        if (!string.IsNullOrEmpty(siblingRepos))
        {
            sb.AppendLine();
            sb.Append(siblingRepos);
        }

        if (!string.IsNullOrEmpty(contextFiles))
        {
            sb.AppendLine();
            sb.Append(contextFiles);
        }

        if (!string.IsNullOrEmpty(conversation))
        {
            sb.AppendLine();
            sb.Append(conversation);
        }

        return sb.ToString();
    }

    /// <summary>The model a run uses: the task's own override when one was picked at
    /// creation, else the agent's Team-page model, else the project default. The override
    /// only applies to dev-role agents — it is chosen alongside the pinned dev agent, so
    /// PM, QA and Code Reviewer keep their own models (QA's Playwright MCP in particular
    /// needs an MCP-capable Claude model).</summary>
    /// <summary>
    /// The language this task's agents write in: the project's language setting, else the
    /// locale frozen on the task by the human who drove it (see <see cref="ProjectLanguage"/>).
    /// </summary>
    internal static string? LanguageFor(TaskSnapshot task, ProjectSummary? project) =>
        ProjectLanguage.Resolve(project, task.RequesterLocale);

    internal static string? ResolveModel(TaskSnapshot? task, AgentSummary? agent, ProjectSummary? project)
        => DevModelResolution.Resolve(
            agent?.Role.IsDevRole() == true ? task?.Model : null,
            agent?.Model,
            project?.Settings?.DefaultModel);

    /// <summary>The vendor serving the resolved model. The runner dispatches by provider and
    /// authenticates from the developer's own subscription for it.</summary>
    private static AiProvider ResolveProvider(TaskSnapshot? task, AgentSummary? agent, ProjectSummary? project)
        => ModelProviders.FromModelId(ResolveModel(task, agent, project));

    /// <summary>
    /// Model + provider for one run, with the provider-transparency fallback.
    ///
    /// The agent's CONFIGURED model always wins while its provider can run on this route —
    /// an agent set to a GPT model on a machine with both claude and codex runs on the
    /// user's GPT subscription, untouched. Substitution happens only when the configured
    /// provider is blocked (CLI missing or signed out on the runner) and another provider
    /// is connected: the run adapts to that provider's tier-equivalent model instead of
    /// failing pre-flight, so a claude-configured default roster on a codex-only or
    /// copilot-only machine just works. The roster itself is never rewritten — only this
    /// run adapts, and reconnecting the configured provider restores it.
    ///
    /// Two limits on the adaptation, both because it must never be a silent surprise: a task's
    /// dev phase never adapts — its model was checked against the runner at creation
    /// (IRunnerCoverageResolver), so a provider that went missing since is a pre-flight blocker
    /// with a login remedy, not a quiet swap; and every adaptation that does happen (PM, QA and
    /// reviewer phases) is announced in the task thread.
    /// </summary>
    private async Task<(string? Model, AiProvider Provider)> ResolveRunModelAsync(
        IServiceScope scope, ITasksApi tasksApi, TaskSnapshot? task, AgentSummary? agent,
        ProjectSummary? project, ExecutionRoute route, CancellationToken ct)
    {
        var model = ResolveModel(task, agent, project);
        var provider = ModelProviders.FromModelId(model);

        if (route.Readiness.GetBlocker(provider) is not { } blocker)
            return (model, provider);

        if (task is { RunnerUserId: not null } && agent?.Role.IsDevRole() == true)
            return (model, provider); // decided at creation — pre-flight blocks with the remedy

        var connected = route.Readiness.Connected;
        if (connected.Count == 0)
            return (model, provider); // nothing to substitute — pre-flight surfaces the real blocker

        var fallback = connected[0];
        var catalog = await scope.ServiceProvider
            .GetRequiredService<IModelCatalog>().GetAsync(ct);
        // Tier-map like the utility client does: fast-tier models to the provider's fast
        // model, everything else to its capable coding model — agent runs need the capable
        // tier, not the utility one.
        var substituted = model?.Contains("haiku", StringComparison.OrdinalIgnoreCase) == true
            ? catalog.FastModel(fallback)
            : catalog.CapableModel(fallback);

        logger.LogInformation(
            "Task {TaskId}: {Provider} can't run here ({Blocker}) — running on {Fallback} model {Model} instead",
            task?.Id, provider, blocker, fallback, substituted);

        await AnnounceModelAdaptationAsync(tasksApi, task, agent, model, provider, substituted, fallback, blocker, route, ct);

        return (substituted, fallback);
    }

    /// <summary>One thread note per adapted run, so "it ran on GPT" is never something the
    /// user has to discover from the execution log. Best-effort: the run proceeds either way.</summary>
    private async Task AnnounceModelAdaptationAsync(
        ITasksApi tasksApi, TaskSnapshot? task, AgentSummary? agent, string? model, AiProvider provider,
        string substituted, AiProvider fallback, string blocker, ExecutionRoute route, CancellationToken ct)
    {
        if (task is null)
            return;
        try
        {
            var who = agent is null ? "The agent" : $"{agent.Name} ({agent.Role})";
            const string where = "your runner";
            await tasksApi.AddTaskMessageAsync(task.Id, "system",
                $"{who} is set to {model} ({ProviderDisplayNames.Of(provider)}), which isn't ready on {where}: "
                + $"{blocker.TrimEnd('.')}. This run uses {substituted} ({ProviderDisplayNames.Of(fallback)}) instead. "
                + $"Sign in to {ProviderDisplayNames.Of(provider)} there to restore the configured model.",
                MessageType.SystemEvent, ct: ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Task {TaskId}: could not announce the model adaptation", task.Id);
        }
    }

    /// <summary>Merge-resolver provider/model: Claude when it can run (today's behavior;
    /// hosted always), else the first connected provider's capable model. An empty
    /// connected list falls through to Anthropic — the invoker's own pre-flight then
    /// yields the blocker as an error, which the merge-failure path persists.</summary>
    internal static (AiProvider Provider, string Model) SelectMergeResolver(
        IProviderReadiness readiness, ModelCatalogSnapshot catalog)
    {
        var provider = readiness.PreferredProvider();
        return (provider, catalog.CapableModel(provider));
    }

    private static string StripCodeFences(string content)
    {
        var raw = content.Trim();
        if (!raw.StartsWith("```")) return raw;

        var firstNewline = raw.IndexOf('\n');
        var lastFence = raw.LastIndexOf("```", StringComparison.Ordinal);
        if (firstNewline >= 0 && lastFence > firstNewline)
            return raw[(firstNewline + 1)..lastFence].Trim();

        return raw;
    }

    private static T? TryParseJson<T>(string? text) where T : class
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(StripCodeFences(text), JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Cuts a lone JSON object out of a message that wraps it in prose
    /// ("Here's my verdict: {...}"). Only safe on a single message — a full stream
    /// may contain several unrelated braces.</summary>
    private static string? ExtractJsonObject(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }

    /// <summary>
    /// Lenient parse ladder for structured verdicts (PM review, QA, code review):
    /// the final result message first (raw, then its embedded {...} object), then
    /// JSON object spans found in the full stream, newest first. Every candidate is
    /// also retried after quote repair — models quoting conversation text verbatim
    /// inside JSON string values ("the user said "do X"") is the dominant real-world
    /// parse failure, and a discarded verdict throws away the whole run.
    /// </summary>
    /// <summary>
    /// One cheap resume turn asking the just-finished agent to restate its verdict as bare JSON.
    ///
    /// The verdict is the LAST thing in a multi-minute stream, so a connection drop near the
    /// end clips exactly the part the parser needs: the resumed continuation finishes the prose
    /// but never re-emits the JSON, and a task with a perfectly good review escalates as
    /// "unreadable verdict". On a network that drops every few minutes this hit consecutive
    /// reviews of the same task. Restating costs a few hundred tokens, against re-running a
    /// whole review — or parking the task on a human who then re-reads everything the agent
    /// already read.
    ///
    /// Returns the original outcome untouched when the re-ask cannot run or itself fails, so
    /// escalation proceeds exactly as before. On success the outcomes are merged the same way
    /// the transient-retry loop merges attempts: token accounting has to cover both turns.
    /// </summary>
    private async Task<(T? Verdict, AgentRunOutcome Outcome)> ReaskUnparseableVerdictAsync<T>(
        Ulid taskId, AgentSummary? agent, AgentRequest request, ExecutionRoute route,
        AgentRunOutcome outcome, Func<T, bool> isValid, CancellationToken ct) where T : class
    {
        if (outcome.SessionId is null || !route.Invoker.SupportsSessionResume)
            return (null, outcome);

        logger.LogWarning(
            "Verdict for task {TaskId} was unparseable — asking the agent to restate it", taskId);

        var restated = await StreamAgentRunAsync(
            taskId, agent, request with
            {
                ResumeSessionId = outcome.SessionId,
                UserMessage =
                    "Your final message could not be parsed as a verdict. Reply with ONLY the "
                    + "verdict JSON object in the exact format your instructions specify — a "
                    + "single JSON object, no prose, no code fences, nothing else.",
                MaxTurns = 1,
                MaxWallClockSeconds = 120,
            },
            route, persistText: false, TimeSpan.FromSeconds(150), ct);

        if (restated.Error is not null)
        {
            logger.LogWarning(
                "Verdict restate for task {TaskId} failed ({Error}) — escalating as before",
                taskId, restated.Error);
            return (null, outcome);
        }

        var merged = outcome with
        {
            FullText = outcome.FullText + restated.FullText,
            ResultText = restated.ResultText ?? outcome.ResultText,
            PromptTokens = outcome.PromptTokens + restated.PromptTokens,
            CompletionTokens = outcome.CompletionTokens + restated.CompletionTokens,
            CacheReadTokens = outcome.CacheReadTokens + restated.CacheReadTokens,
            CacheWriteTokens = outcome.CacheWriteTokens + restated.CacheWriteTokens,
            VendorCostUsd = outcome.VendorCostUsd is null && restated.VendorCostUsd is null
                ? null
                : (outcome.VendorCostUsd ?? 0m) + (restated.VendorCostUsd ?? 0m),
        };

        return (ParseAgentVerdict<T>(restated.ResultText, restated.FullText, isValid), merged);
    }

    /// <summary>See the resume decision in the Developing phase for the reasoning.</summary>
    internal static bool ResumeWouldRepeatAnEmptyRun(ExecutionSessionInfo session) =>
        !session.Interrupted
        && session.StartedAt is { } started
        && session.CompletedAt - started < TimeSpan.FromSeconds(30);

    internal static T? ParseAgentVerdict<T>(
        string? resultText, string? fullText, Func<T, bool>? isValid = null) where T : class
    {
        var candidates = new List<string?> { resultText, ExtractJsonObject(resultText) };
        if (!string.IsNullOrWhiteSpace(fullText))
        {
            candidates.AddRange(ExtractJsonObjectSpans(fullText)
                .Select(span => fullText.Substring(span.Start, span.Length))
                .Reverse()); // the final answer supersedes earlier output
            candidates.Add(ExtractJsonObject(fullText));
            candidates.Add(fullText);
        }

        foreach (var candidate in candidates)
        {
            // The validity check keeps unrelated JSON in the stream (echoed tool calls
            // parse fine but carry none of the verdict fields) from ending the search.
            var parsed = Accept(TryParseJson<T>(candidate))
                ?? Accept(TryParseJson<T>(RepairUnescapedQuotes(candidate)));
            if (parsed is not null) return parsed;
        }

        return null;

        T? Accept(T? parsed) => parsed is not null && (isValid?.Invoke(parsed) ?? true) ? parsed : null;
    }

    /// <summary>
    /// Escapes bare double quotes inside JSON string values. A quote inside a string
    /// only counts as its terminator when the next non-whitespace character can legally
    /// follow a string (, : } ] or end of input) — otherwise it is content the model
    /// forgot to escape. Wrong guesses only yield JSON that still fails to parse, so
    /// the repair can never turn a bad payload into a false success silently.
    /// </summary>
    internal static string? RepairUnescapedQuotes(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;

        var builder = new System.Text.StringBuilder(text.Length + 16);
        var inString = false;
        var escaped = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (!inString)
            {
                if (c == '"') inString = true;
                builder.Append(c);
                continue;
            }

            if (escaped)
            {
                escaped = false;
                builder.Append(c);
                continue;
            }

            if (c == '\\')
            {
                escaped = true;
                builder.Append(c);
                continue;
            }

            if (c == '"')
            {
                var j = i + 1;
                while (j < text.Length && char.IsWhiteSpace(text[j])) j++;
                var closes = j >= text.Length || text[j] is ',' or ':' or '}' or ']';
                if (closes)
                {
                    inString = false;
                    builder.Append('"');
                }
                else
                {
                    builder.Append("\\\"");
                }
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Detects the planning-phase question escape hatch: {"questions": ["..."]}.
    /// Returns a rendered question message, or null when the output is a plan.
    /// </summary>
    private static List<TaskQuestion>? TryExtractQuestions(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (doc.RootElement.TryGetProperty("approach", out _)) return null;
            if (!doc.RootElement.TryGetProperty("questions", out var questions)) return null;
            if (questions.ValueKind != JsonValueKind.Array || questions.GetArrayLength() == 0) return null;

            var items = NormalizeQuestionElements(questions.EnumerateArray());
            return items.Count == 0 ? null : items;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Plan JSON shape produced by the planning agent. Mirrors the Tasks module's
    /// PlanContent (module-internal) — the plan crosses the module boundary as JSON.
    /// </summary>
    internal sealed class PlanDraft
    {
        public required string Summary { get; set; }
        public required string Approach { get; set; }
        public List<PlanDraftFileChange>? FilesChanged { get; set; }
        public int? EstimatedTokens { get; set; }
    }

    internal sealed class PlanDraftFileChange
    {
        public required string Path { get; set; }
        public required string Action { get; set; }
        public required string Description { get; set; }
    }

    /// <summary>
    /// Parses plan JSON from agent output. Tries the whole content first, then falls
    /// back to scanning for a plan-shaped JSON object embedded in surrounding prose
    /// (models often wrap the JSON in narration despite instructions).
    /// </summary>
    internal static PlanDraft? TryParsePlanContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        var candidates = new List<string> { content };
        candidates.AddRange(ExtractJsonObjectSpans(content)
            .Select(span => content.Substring(span.Start, span.Length))
            .Reverse()); // prefer the last object — the final answer supersedes earlier output

        foreach (var raw in candidates)
        {
            // Retry each candidate after quote repair — same failure mode as verdicts:
            // the model quoting conversation text unescaped inside a JSON string.
            foreach (var candidate in new[] { raw, RepairUnescapedQuotes(raw) })
            {
                if (candidate is null) continue;
                try
                {
                    var parsed = JsonSerializer.Deserialize<PlanDraft>(candidate, JsonOptions);
                    if (!string.IsNullOrWhiteSpace(parsed?.Approach))
                        return parsed;
                }
                catch (JsonException)
                {
                    // not this candidate — keep scanning
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Removes JSON blobs that are clearly not plan prose (tool calls echoed as text,
    /// e.g. {"name": "Read", "arguments": {...}}) so the unstructured-plan fallback
    /// shows the actual plan text instead of protocol artifacts.
    /// </summary>
    internal static string StripToolCallArtifacts(string content)
    {
        var spans = ExtractJsonObjectSpans(content);
        if (spans.Count == 0) return content.Trim();

        var builder = new System.Text.StringBuilder();
        var cursor = 0;
        foreach (var span in spans)
        {
            var json = content.Substring(span.Start, span.Length);
            if (LooksLikeToolCallJson(json))
            {
                builder.Append(content, cursor, span.Start - cursor);
                cursor = span.Start + span.Length;
            }
        }
        builder.Append(content, cursor, content.Length - cursor);

        return builder.ToString().Trim();
    }

    private static bool LooksLikeToolCallJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            return doc.RootElement.TryGetProperty("name", out _)
                && (doc.RootElement.TryGetProperty("arguments", out _)
                    || doc.RootElement.TryGetProperty("parameters", out _)
                    || doc.RootElement.TryGetProperty("input", out _));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Finds top-level {...} object spans in mixed prose/JSON text using brace matching
    /// that respects string literals and escapes.
    /// </summary>
    private static List<(int Start, int Length)> ExtractJsonObjectSpans(string content)
    {
        var spans = new List<(int, int)>();
        var depth = 0;
        var start = -1;
        var inString = false;
        var escaped = false;

        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"' when depth > 0:
                    inString = true;
                    break;
                case '{':
                    if (depth == 0) start = i;
                    depth++;
                    break;
                case '}':
                    if (depth > 0 && --depth == 0)
                        spans.Add((start, i - start + 1));
                    break;
            }
        }

        return spans;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SkipPlanning shortcut
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// When SkipPlanning is true, set up the git workspace and create a synthetic approved plan
    /// so the developing phase can proceed without a planning phase.
    /// </summary>
    private async Task SetupWorkspaceAndSkipPlanningAsync(Ulid taskId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var tasksApi = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var projectsApi = scope.ServiceProvider.GetRequiredService<IProjectsApi>();

        var task = await tasksApi.GetTaskSnapshotAsync(taskId, ct);

        if (task is null) return;

        var route = executionRouter.Resolve(task);

        var repo = task.RepoId is { } repoId ? await projectsApi.GetRepoAsync(repoId, ct) : null;

        // Workspace first: on failure the task must stay in its current lane and go Blocked,
        // exactly like the original (whose lane change was only flushed after worktree success).
        if (repo is not null)
        {
            var workspace = await SetupGitWorkspaceAsync(scope, tasksApi, task, repo, route, ct);
            if (workspace.WorktreePath is null)
            {
                await SetTaskErrorAsync(taskId, workspace.Error ?? "Workspace setup failed.");
                return;
            }
        }

        await tasksApi.StartSkipPlanningDevelopingAsync(taskId, ct);

        // Create a synthetic approved plan so developing phase doesn't fail
        await tasksApi.EnsureSyntheticApprovedPlanAsync(
            taskId,
            "Planning skipped — implementing task directly.",
            task.Description ?? task.Title, ct);

        await taskNotifier.SendLaneChangedAsync(task.ProjectId, new LaneChangedEvent(
            taskId, task.Title, Lane.Ready.ToString(), Lane.Developing.ToString(),
            TaskStatus.Active.ToString(), DateTimeOffset.UtcNow));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Git pushes & failure handling
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The push the awaiting-PR sweep trusts ("every phase ends pushed"), made true by the
    /// platform rather than assumed of the agent, on EVERY route — including local-folder
    /// repos, whose "forge" is the user's own folder: the worktree's origin is that folder and
    /// the push is a ref update into it, which is exactly what the local merge
    /// (squash in a scratch clone of the folder) reads the task branch from. The agent never
    /// pushes (<see cref="CommitAndPushInstruction"/>), so skipping this here left every
    /// local-folder task with its branch stranded in the worktree and the merge refusing with
    /// "is not in your repository" (prod, 2026-09-15). Returns null when the branch is on the
    /// remote, else the push error — and the caller must NOT complete the phase on an error: a
    /// lane that advances on a failed push is how a task lands in Code Review with no PR while
    /// the sweep hammers the forge with un-creatable PRs (422 head: invalid).
    /// </summary>
    private async Task<string?> EnsureBranchPushedAfterRunAsync(
        ExecutionRoute route, TaskSnapshot task, RepoSummary? repo, CancellationToken ct)
    {
        if (repo is null || string.IsNullOrEmpty(task.BranchName))
            return null;

        var result = await route.Git.EnsurePushedAsync(
            task.OrgId, repo.Id, task.Id, task.BranchName, ct);
        if (result.IsFailure)
        {
            logger.LogWarning("Platform push after the run failed for task {TaskId} on {Branch}: {Error}",
                task.Id, task.BranchName, result.Error);
            return result.Error ?? "The push failed for an unknown reason.";
        }
        return null;
    }

    /// <summary>The repo's setup command, or the one derived from its run targets; null when
    /// there is nothing to run.</summary>
    private async Task<string?> ResolveSetupCommandAsync(
        IServiceScope scope, TaskSnapshot task, RepoSummary repo, ExecutionRoute route, CancellationToken ct)
    {
        var command = repo.SetupCommand;
        if (!string.IsNullOrWhiteSpace(command))
            return command;

        // Not in the settings: the repository's own orkka.json on its default branch.
        command = (await ReadManifestCommandsAsync(task, repo, route, ct)).Setup;
        if (!string.IsNullOrWhiteSpace(command))
            return command;

        // Nobody declared one — the run targets' install command for this repo is the same
        // answer, and it is what makes a QA worktree able to start the app.
        command = QaRunCommandDerivation.DeriveSetupCommand(
            await TryGetRunTargetsAsync(scope, task, ct), repo.Id);
        if (string.IsNullOrWhiteSpace(command))
            return null;
        logger.LogInformation(
            "Task {TaskId}: setup command derived from run targets: {Command}", task.Id, command);
        return command;
    }

    /// <summary>
    /// Marks the execution Failed and the task Blocked (one Tasks-module save, tokens
    /// burned before the failure included), then records usage, agent failure
    /// counters and a memory, and notifies.
    /// </summary>
    private async Task FailExecutionAsync(
        IServiceScope scope, ITasksApi tasksApi, TaskSnapshot task, Ulid executionId,
        ProjectSummary? project, AgentSummary? agent, string error,
        AgentRunOutcome? outcome = null, CancellationToken ct = default)
    {
        // A run whose runner has GONE (not a genuine agent failure — the connection is
        // what died) parks as WaitingForRunner instead of blocking: phase re-entry is the
        // crash-recovery model, so the reconnect sweep re-runs the phase. When the runner
        // is still there, the failure is real — block. The task lost its socket on THIS
        // replica, which is exactly what claim affinity reports; the route's own liveness
        // probe answers the rest.
        var route = executionRouter.Resolve(task);
        var runnerGone =
            (task.RunnerUserId is not null
                && !claimAffinity.CanExecuteHere(task.OrgId, task.RunnerUserId))
            || !route.IsBackendLive;

        // The run's CLI session travels with the failure. Phase re-entry is the recovery
        // model, and re-entering cold replays the entire prompt and redoes work the agent
        // already did on disk — the session id is what lets the next pass continue instead.
        var execution = runnerGone
            ? await tasksApi.ParkExecutionWaitingForRunnerAsync(
                task.Id, executionId, error,
                outcome?.PromptTokens ?? 0, outcome?.CompletionTokens ?? 0,
                outcome?.SessionId, ct)
            : await tasksApi.FailExecutionAsync(
                task.Id, executionId, error,
                outcome?.PromptTokens ?? 0, outcome?.CompletionTokens ?? 0,
                outcome?.SessionId, ct);

        // Tokens burned before the failure are still paid to Anthropic — bill them.
        // Subscription-authenticated runs (user's runner) burned THEIR tokens: record
        // the counts for visibility but at zero cost, same as successful runs.
        if (outcome is not null && (outcome.PromptTokens > 0 || outcome.CompletionTokens > 0))
        {
            try
            {
                var usageRecorder = scope.ServiceProvider.GetRequiredService<IUsageRecorder>();
                await usageRecorder.RecordUsageAsync(new RecordUsageCommand(
                    task.OrgId, task.ProjectId, task.Id, agent?.Id, executionId,
                    ResolveModel(task, agent, project) ?? "claude-sonnet-5",
                    outcome.PromptTokens, outcome.CompletionTokens,
                    outcome.CacheReadTokens, outcome.CacheWriteTokens,
                    FlatCostBase: 0m,
                    UserId: task.CreatedByUserId,
                    VendorReportedCostUsd: outcome.VendorCostUsd));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to record usage for failed execution {ExecutionId}", executionId);
            }
        }

        if (agent is not null)
        {
            // Counters and memories live in the Agents module - use a dedicated scope so this
            // works regardless of the caller's scope lifetime (same failure semantics as before).
            using var agentsScope = scopeFactory.CreateScope();
            var agentsApi = agentsScope.ServiceProvider.GetRequiredService<IAgentsApi>();

            await agentsApi.RecordAgentRunResultAsync(
                agent.Id, outcome?.PromptTokens ?? 0, outcome?.CompletionTokens ?? 0, 0, 1);

            if (execution is not null)
            {
                try
                {
                    await memoryExtractor.ExtractAndSaveAsync(agentsApi, execution, agent, task.Title);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Memory extraction failed for failed execution {ExecutionId}", executionId);
                }
            }
        }

        // The two paths above land the task in different statuses — announce the one that
        // actually happened, or the board shows Blocked for a task that will resume itself.
        await taskNotifier.SendStatusChangedAsync(task.ProjectId, new StatusChangedEvent(
            task.Id,
            (runnerGone ? TaskStatus.WaitingForRunner : TaskStatus.Blocked).ToString(),
            DateTimeOffset.UtcNow));

        // Persist the reason in the thread — stream events are ephemeral, so without
        // this a reloaded task page shows the status with no visible cause.
        try
        {
            var note = runnerGone
                ? $"Task paused: {error} It resumes automatically once the runner is back."
                : $"Task blocked: {error}";
            await PersistAgentMessageAsync(
                tasksApi, task.Id, agent, note, MessageType.Error, "system", ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist block-reason message for task {TaskId}", task.Id);
        }

        await taskNotifier.SendStreamEventAsync(task.Id, new StreamEvent(
            "Error", error, DateTimeOffset.UtcNow));

        if (runnerGone)
            logger.LogWarning(
                "Task {TaskId} parked as WaitingForRunner — the runner went away mid-run: {Error}",
                task.Id, error);
        else
            logger.LogError("Task {TaskId} execution failed: {Error}", task.Id, error);
    }

    private async Task SetTaskErrorAsync(Ulid taskId, string error)
    {
        try
        {
            logger.LogError("Setting task {TaskId} to Blocked due to unhandled error: {Error}", taskId, error);

            using var scope = scopeFactory.CreateScope();
            var tasksApi = scope.ServiceProvider.GetRequiredService<ITasksApi>();

            var blocked = await tasksApi.BlockTaskWithErrorAsync(taskId, error);
            if (blocked is null) return;

            await taskNotifier.SendStatusChangedAsync(blocked.ProjectId, new StatusChangedEvent(
                taskId, TaskStatus.Blocked.ToString(), DateTimeOffset.UtcNow));

            if (blocked.Message is { } message)
            {
                await taskNotifier.SendNewMessageAsync(taskId, new NewMessageEvent(
                    taskId, message.Id, message.Role, message.Content,
                    message.MessageType.ToString(), message.SenderId, message.CreatedAt));
            }

            await taskNotifier.SendStreamEventAsync(taskId, new StreamEvent(
                "Error", error, DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to set error status for task {TaskId}", taskId);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Prompt fragments
    // ─────────────────────────────────────────────────────────────────────────

    private const string ClaudeMdGuidelines = """

        ## CLAUDE.md Documentation Requirement
        For every directory where you create or significantly modify files, you MUST create or update
        a CLAUDE.md file in that directory. This file provides context for future AI agents working
        on bug fixes, refactors, or new features in the same area.

        Each CLAUDE.md should include:
        - **Purpose**: What this module/feature does and why it exists
        - **Key decisions**: Architecture choices, trade-offs, and why alternatives were rejected
        - **Business logic**: Core rules, edge cases, and invariants that must be preserved
        - **Dependencies**: What this code depends on and what depends on it
        - **Gotchas**: Non-obvious behavior, known limitations, or things that are easy to break

        Keep CLAUDE.md files concise (under 80 lines). Focus on "why" not "what" — the code shows
        what it does, the CLAUDE.md explains why it does it that way.

        If a CLAUDE.md already exists, update it — don't duplicate or overwrite useful context.
        Do NOT create CLAUDE.md files in the repository root (the project already has one).
        """;

    private const string PlanJsonSchema = """
            {
              "summary": "Brief 1-2 sentence summary of what will be done",
              "approach": "Detailed step-by-step implementation approach including analysis, testing strategy, risks, and CLAUDE.md updates",
              "filesChanged": [
                {
                  "path": "relative/path/to/file.ext",
                  "action": "create|modify|delete",
                  "description": "What changes will be made to this file"
                }
              ],
              "estimatedTokens": 0
            }
            """;

    private async Task<string?> BuildContextFilesSectionAsync(
        TaskSnapshot task, RepoSummary? repo, ExecutionRoute route, int maxChars, bool pathsOnly, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(task.ContextFiles) || task.RepoId is null)
            return null;

        List<string>? filePaths;
        try
        {
            filePaths = JsonSerializer.Deserialize<List<string>>(task.ContextFiles, JsonOptions);
        }
        catch
        {
            logger.LogWarning("Failed to deserialize ContextFiles for task {TaskId}", task.Id);
            return null;
        }

        if (filePaths is not { Count: > 0 })
            return null;

        var branch = repo?.DefaultBranch ?? "main";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Context Files");

        // Paths-only: the agent has Read/Glob/Grep on the repo, so inlining contents here
        // would pay for the same bytes twice. List the files and make reading them mandatory.
        if (pathsOnly)
        {
            sb.AppendLine("The user linked these files as required context. Read EACH of them with the Read tool before starting — they are not optional.");
            sb.AppendLine();
            foreach (var filePath in filePaths)
                sb.AppendLine($"- {filePath}");
            return sb.ToString();
        }

        sb.AppendLine("The user linked these files for additional context. Review them carefully before starting.");
        sb.AppendLine();

        var maxTotalChars = maxChars;
        var totalChars = 0;

        foreach (var filePath in filePaths)
        {
            if (totalChars >= maxTotalChars)
            {
                sb.AppendLine($"_(Remaining {filePaths.Count - filePaths.IndexOf(filePath)} context files omitted — token budget exceeded)_");
                logger.LogWarning("Context files truncated for task {TaskId} — exceeded {MaxChars} char budget", task.Id, maxTotalChars);
                break;
            }

            var result = await route.Repo.GetFileContentAsync(task.OrgId, task.RepoId.Value, branch, filePath, ct);

            if (result.IsFailure)
            {
                sb.AppendLine($"### {filePath}");
                sb.AppendLine("_(file not found in repository)_");
                sb.AppendLine();
                continue;
            }

            var content = result.Value!;
            var remaining = maxTotalChars - totalChars;
            if (content.Length > remaining)
            {
                content = content[..remaining] + "\n... (truncated)";
            }

            totalChars += content.Length;

            var ext = System.IO.Path.GetExtension(filePath).TrimStart('.');
            sb.AppendLine($"### {filePath}");
            sb.AppendLine($"```{ext}");
            sb.AppendLine(content);
            sb.AppendLine("```");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// The project's decided palette, type and tone. Provisioning derives this once and the
    /// app's stylesheet gets its @theme block from it (the preview finishing touch writes it —
    /// the scaffold deploys before the kit exists), but until this section existed nothing
    /// carried it into a task run — so every agent after the scaffold restyled from Tailwind's
    /// stock palette and the app drifted away from its own brand kit one task at a time.
    ///
    /// Unlike the design-assets section this carries no role gate: it is ten short lines, not
    /// thirty signed URLs, and a backend agent touching one template is better off knowing the
    /// palette than guessing it. Failure is swallowed — a missing brand section degrades the
    /// styling of one run, losing the run entirely would be worse.
    /// </summary>
    private async Task<string?> BuildBrandKitSectionAsync(
        IServiceScope scope, TaskSnapshot task, CancellationToken ct)
    {
        BrandKitSummary? kit;
        try
        {
            var designApi = scope.ServiceProvider.GetRequiredService<IDesignGenerationApi>();
            kit = await designApi.GetBrandKitAsync(task.OrgId, task.ProjectId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not read the brand kit for project {ProjectId} — running without it",
                task.ProjectId);
            return null;
        }

        if (kit is null) return null;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Project Brand");
        sb.AppendLine("This project's visual identity is already decided — the stylesheet and the " +
                      "approved assets are built from these exact values. Apply them through the " +
                      "project's design tokens; do NOT choose your own colours, and do NOT reach for " +
                      "a stock framework palette (`bg-blue-600`, `text-gray-500`) when a brand token " +
                      "exists. If the stylesheet disagrees with the values below, it has drifted — " +
                      "say so in your final message instead of silently picking one.");
        sb.AppendLine();
        sb.AppendLine($"- Primary: {kit.Palette.Primary} (hover {kit.Palette.PrimaryHover})");
        sb.AppendLine($"- Secondary: {kit.Palette.Secondary}");
        sb.AppendLine($"- Accent: {kit.Palette.Accent}");
        sb.AppendLine($"- Background: {kit.Palette.Background} / Surface: {kit.Palette.Surface}");
        sb.AppendLine($"- Text: {kit.Palette.Text} / Muted: {kit.Palette.TextMuted}");
        sb.AppendLine($"- Border: {kit.Palette.Border}");
        if (kit.DisplayFont is not null) sb.AppendLine($"- Display font: {kit.DisplayFont}");
        if (kit.BodyFont is not null) sb.AppendLine($"- Body font: {kit.BodyFont}");
        if (kit.Tone is not null) sb.AppendLine($"- Visual tone: {kit.Tone}");

        return sb.ToString();
    }

    private const int MaxDesignAssetsInContext = 30;

    /// <summary>Approved design assets from the project's Design gallery, rendered as
    /// ready-to-run download commands for frontend-facing dev agents. URLs are read-signed
    /// for 3 hours — enough for queue wait plus the 30-minute agent cap.</summary>
    private async Task<string?> BuildDesignAssetsSectionAsync(
        IServiceScope scope, IAgentsApi agentsApi, TaskSnapshot task, AgentSummary? agent, CancellationToken ct)
    {
        if (agent?.Role is not (AgentRole.FrontendDev or AgentRole.FullStackDev))
            return null;

        var assets = await agentsApi.ListApprovedDesignAssetsAsync(task.ProjectId, MaxDesignAssetsInContext, ct);
        if (assets.Count == 0)
            return null;

        var blobStorage = scope.ServiceProvider.GetRequiredService<IBlobStorageService>();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Approved Design Assets");
        sb.AppendLine("The design team approved these visual assets for this project. The brand identity " +
                      "(logo-icon, wordmark, loading-spinner) is normally already committed under " +
                      "public/brand/ — check there first and use the committed files; the app shell's " +
                      "header shows the logo mark, never a drawn substitute. For anything else the task " +
                      "calls for (icon, hero, banner, illustration), download it into the repo's " +
                      "static-assets folder (public/ unless the project uses another convention) with the " +
                      "matching command below, then reference the committed file from code. Only download " +
                      "assets the task actually needs. NEVER commit or hard-code the signed URL itself — " +
                      "it expires.");
        sb.AppendLine();

        foreach (var asset in assets)
        {
            var url = await blobStorage.GenerateDownloadUrlAsync(
                asset.BlobContainer, asset.BlobName, TimeSpan.FromHours(3));
            sb.AppendLine($"- {asset.Name} ({asset.MimeType}):");
            sb.AppendLine($"  curl -L -o public/{asset.FileName} \"{url}\"");
        }

        return sb.ToString();
    }

    private const int MaxTaskAttachmentsInContext = 20;

    /// <summary>Files the user uploaded on the task's Attachments card, rendered as
    /// ready-to-run download commands. Without this section agents only see the repo and
    /// wrongly conclude an attached file "never arrived". URLs are read-signed for 3 hours
    /// (cloud) — enough for queue wait plus the agent cap; the local edition serves blobs
    /// from this host without expiry.
    ///
    /// PDFs get an extra line: the agent runtime cannot Read a PDF (AgentPdfReadGuard), so
    /// their text is extracted here, once, into a .txt sibling blob that the agent downloads
    /// through the same signed-URL model.</summary>
    private async Task<string?> BuildTaskAttachmentsSectionAsync(
        IServiceScope scope, ITasksApi tasksApi, TaskSnapshot task, CancellationToken ct)
    {
        var attachments = await tasksApi.ListTaskAttachmentsAsync(task.Id, ct);
        if (attachments.Count == 0)
            return null;

        var blobStorage = scope.ServiceProvider.GetRequiredService<IBlobStorageService>();
        var pdfText = scope.ServiceProvider.GetRequiredService<IPdfTextSidecarService>();

        var lines = new List<TaskAttachmentPrompt.Line>();
        foreach (var attachment in attachments.Take(MaxTaskAttachmentsInContext))
        {
            var url = await blobStorage.GenerateDownloadUrlAsync(
                "task-attachments", attachment.BlobName, TimeSpan.FromHours(3));

            var isPdf = PdfTextSidecarService.IsPdf(attachment.FileName, attachment.FileType);
            string? textUrl = null;
            if (isPdf)
            {
                var sidecar = await pdfText.GetOrCreateAsync(
                    "task-attachments", attachment.BlobName, attachment.FileName, attachment.FileSize, ct);
                if (sidecar is not null)
                    textUrl = await blobStorage.GenerateDownloadUrlAsync(
                        "task-attachments", sidecar.BlobName, TimeSpan.FromHours(3));
            }

            lines.Add(new TaskAttachmentPrompt.Line(
                attachment.FileName, attachment.FileType, attachment.FileSize,
                SafeAttachmentFileName(attachment.FileName), url, isPdf, textUrl));
        }

        return TaskAttachmentPrompt.Render(
            lines, Math.Max(0, attachments.Count - MaxTaskAttachmentsInContext));
    }

    /// <summary>Attachment names come from user uploads — reduce to a shell-safe basename
    /// before embedding in a runnable curl command.</summary>
    private static string SafeAttachmentFileName(string fileName)
    {
        var name = System.IO.Path.GetFileName(fileName);
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-');
        var safe = sb.ToString().TrimStart('.');
        return safe.Length > 0 ? safe : "attachment";
    }

    /// <summary>Filesystem-safe slug for context-file names (lowercase, dashes, ≤40 chars).</summary>
    private static string Slugify(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        if (slug.Length > 40) slug = slug[..40].TrimEnd('-');
        return slug.Length > 0 ? slug : "document";
    }

    /// <summary>Writes the task diff to .orkka/context/diff.patch. False (disabled, no
    /// repo, old runner) means the caller inlines the diff as before.</summary>
    private async Task<bool> TryWriteDiffFileAsync(
        TaskSnapshot task, RepoSummary? repo, string diff, ExecutionRoute route, CancellationToken ct)
    {
        if (!_context.DiffAsFile || repo is null)
            return false;

        var write = await route.Git.WriteWorktreeContextAsync(task.OrgId, repo.Id, task.Id,
            [new WorktreeContextFile("diff.patch", diff)], ct);
        if (write.IsFailure)
            logger.LogInformation("Diff-as-file unavailable for task {TaskId} ({Error}) — inlining diff",
                task.Id, write.Error);
        return write.IsSuccess;
    }

    private static string? ConcatSections(string? first, string? second) =>
        (string.IsNullOrEmpty(first), string.IsNullOrEmpty(second)) switch
        {
            (true, true) => null,
            (true, false) => second,
            (false, true) => first,
            _ => first + "\n" + second,
        };

    /// <summary>Project documents the user attached to the task (Documents module), rendered
    /// for prompt injection. Deleted/foreign ids are silently omitted — the fetch only returns
    /// documents that still exist in this org/project.</summary>
    private async Task<string?> BuildContextDocumentsSectionAsync(
        IServiceScope scope, TaskSnapshot task, int maxChars, ExecutionRoute route, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(task.ContextDocumentIds))
            return null;

        List<string>? rawIds;
        try
        {
            rawIds = JsonSerializer.Deserialize<List<string>>(task.ContextDocumentIds, JsonOptions);
        }
        catch
        {
            logger.LogWarning("Failed to deserialize ContextDocumentIds for task {TaskId}", task.Id);
            return null;
        }

        var documentIds = (rawIds ?? [])
            .Select(id => Ulid.TryParse(id, out var parsed) ? parsed : Ulid.Empty)
            .Where(id => id != Ulid.Empty)
            .Distinct()
            .ToList();

        if (documentIds.Count == 0)
            return null;

        var documentsApi = scope.ServiceProvider.GetRequiredService<IDocumentsApi>();
        var documents = await documentsApi.GetDocumentsContentAsync(task.OrgId, task.ProjectId, documentIds, ct);

        if (documents.Count == 0)
            return null;

        // Materialize as worktree files when enabled — the same document bytes are
        // otherwise re-inlined in PM review, planning, AND developing. Falls back to
        // inlining when the workspace can't take files (old local runner, no worktree).
        if (_context.DocsAsFiles && task.RepoId is { } docsRepoId)
        {
            var files = new List<WorktreeContextFile>(documents.Count);
            var entries = new List<string>(documents.Count);
            for (var i = 0; i < documents.Count; i++)
            {
                var doc = documents[i];
                var label = string.IsNullOrEmpty(doc.Path) ? doc.Title : $"{doc.Path}/{doc.Title}";
                var fileName = $"doc-{i + 1}-{Slugify(doc.Title)}.md";
                files.Add(new WorktreeContextFile(fileName, $"# {label}\n\n{doc.Content}"));
                entries.Add($"- .orkka/context/{fileName} — \"{label}\"");
            }

            var write = await route.Git.WriteWorktreeContextAsync(task.OrgId, docsRepoId, task.Id, files, ct);
            if (write.IsSuccess)
            {
                return "## Context Documents\n"
                    + "The user attached these documents from the project's knowledge base — they are "
                    + "saved as files in your worktree. Read the relevant ones with the Read tool before "
                    + "starting; treat their content as authoritative context.\n\n"
                    + string.Join('\n', entries) + "\n";
            }

            logger.LogInformation(
                "Docs-as-files unavailable for task {TaskId} ({Error}) — inlining documents",
                task.Id, write.Error);
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Context Documents");
        sb.AppendLine("The user attached these documents from the project's knowledge base. Treat them as authoritative context.");
        sb.AppendLine();

        var maxTotalChars = maxChars; // separate budget from context files
        var totalChars = 0;

        for (var i = 0; i < documents.Count; i++)
        {
            if (totalChars >= maxTotalChars)
            {
                sb.AppendLine($"_(Remaining {documents.Count - i} context documents omitted — token budget exceeded)_");
                logger.LogWarning("Context documents truncated for task {TaskId} — exceeded {MaxChars} char budget", task.Id, maxTotalChars);
                break;
            }

            var doc = documents[i];
            var content = doc.Content;
            var remaining = maxTotalChars - totalChars;
            if (content.Length > remaining)
            {
                content = content[..remaining] + "\n... (truncated)";
            }

            totalChars += content.Length;

            var label = string.IsNullOrEmpty(doc.Path) ? doc.Title : $"{doc.Path}/{doc.Title}";
            sb.AppendLine($"### {label}");
            // Four-backtick fence: document bodies are markdown and routinely contain ``` blocks
            sb.AppendLine("````md");
            sb.AppendLine(content);
            sb.AppendLine("````");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    // Internal for tests — prompt composition (persona vs platform-rule ordering) is asserted directly.
    /// <summary>
    /// Tells a read-only phase that it is read-only.
    ///
    /// Planning, PM review and code review are granted Read/Glob/Grep and no Bash. Without being
    /// told, an agent discovers this by having a command refused, cannot tell a missing tool from
    /// a broken one, and reports the sandbox as stuck — which parks the task on a question no
    /// human can answer and, under autopilot, stalls the whole session. Naming the constraint up
    /// front costs a few tokens and removes the entire failure mode.
    /// </summary>
    internal const string ReadOnlyPhaseNotice = """

        ## Tools available in this phase
        You have Read, Glob and Grep. You do NOT have a shell in this phase — that is by design,
        not a fault, and no approval is pending. Do not attempt to build, test, install
        dependencies, run migrations, commit, or push; those calls will be refused and the
        refusal is not a problem to report. Describe the commands that should be run as part of
        your plan instead — a later phase has a shell and will execute them.
        """;

    internal static (string System, string User) BuildPlanningPrompt(
        TaskSnapshot task, ProjectSummary? project, RepoSummary? repo, AgentSummary? agent, string memories,
        string? contextFilesSection, string? conversationSection, string? siblingRepos = null)
    {
        var persona = agent is not null
            ? BuildPersonaSection(agent, codeWritingPhase: true)
            : "You are an AI software developer assistant. You are creating an execution plan for a coding task.";

        var system = $$"""
            {{persona}}
            {{memories}}
            ## Current Assignment
            Create an execution plan for the coding task described by the user message.
            1. FIRST, explore the codebase using your built-in tools (Read, Glob, Grep). Start by understanding the project structure.
            2. Read relevant files (README, CLAUDE.md, config files, and source files related to the task).
            3. Read your `code-craft` skill (and `frontend-craft` when the task renders UI) so the plan is built to the bar, not just to the task.
            4. Search for relevant code patterns, function names, or components mentioned in the task — and for the existing helpers, components and patterns the change should reuse instead of re-creating.
            5. Based on your ACTUAL understanding of the codebase, create a detailed execution plan.
            6. Reference REAL file paths that you found in the repository — never guess or hallucinate file paths.

            ## Response Format
            After exploring the codebase, you MUST respond with ONLY a valid JSON object (no markdown, no code fences, no extra text) matching this exact schema:
            {{PlanJsonSchema}}
            Guidelines for the plan:
            - "summary" should be concise and describe the overall goal
            - "approach" should be thorough, under these markdown headings: **Problem analysis**; **Existing code to reuse** (real paths of the helpers, components and patterns the change builds on); **Design** (which layer each change lives in, any new abstraction and why one is needed, what you deliberately did not abstract); **Implementation steps**; **Testing strategy** (which tests, in which framework and files); **Risks / refactor-first** (if the task would duplicate existing code or grow a unit that is already too large, say so and plan the small refactor first); and any CLAUDE.md documentation updates needed
            - "approach" is rendered as markdown in the UI — use headings, numbered lists, and inline code formatting for readability
            - "filesChanged" should list every file you plan to create, modify, or delete — use actual paths you discovered
            - "estimatedTokens" can be 0 if unknown

            {{ReadOnlyPhaseNotice}}

            ## If you are blocked
            If you genuinely cannot produce a plan without a decision or clarification from the human,
            respond with ONLY: {"questions": [{"question": "your question", "options": ["short option A", "short option B"], "recommended": "short option A", "allowCustom": true, "multiSelect": false}]}
            Give 2-5 SHORT clickable "options" whenever natural choices exist, and always name the
            one you would pick if the user gave no guidance in "recommended" (fold your one-line
            reason into the question text); a plain string entry is also accepted for a fully
            open-ended question.
            Only do this for true blockers — check the conversation below first; earlier answers may already resolve your doubt.
            {{RefusedToolNotice}}
            {{AutonomyInstruction(EffectiveTaskPolicy.For(task, project?.Settings), "in the plan's \"approach\"")}}
            {{ClaudeMdGuidelines}}
            {{AgentLanguage.Instruction(LanguageFor(task, project))}}
            """;

        var user = BuildTaskContextSection(task, repo, contextFilesSection, conversationSection, siblingRepos);
        return (system, user);
    }

    /// <summary>
    /// System prompt for the code-review phase. The persona carries the reviewer's checklist;
    /// this carries the verdict contract and the objective "What blocks" list — orchestrator-
    /// owned rather than persona-baked so it reaches reviewers seeded before it existed, and so
    /// the dev agent's engineering bar and the reviewer's blocking list cannot drift apart.
    /// </summary>
    internal static string BuildCodeReviewSystemPrompt(
        TaskSnapshot task, AgentSummary agent, string memories, int maxReviewCycles,
        ProjectSummary? project = null) => $$"""
            {{BuildPersonaSection(agent)}}
            {{memories}}
            ## Current Assignment
            Review the pull request diff below (review cycle {{task.ReviewCycleCount + 1}} of {{maxReviewCycles}} before human escalation).
            You are in a read-only checkout of the PR branch — use Read/Glob/Grep to inspect surrounding
            code when the diff alone isn't enough context.
            {{ReadOnlyPhaseNotice}}

            ## What blocks
            `request_changes` is for [Critical] findings only: correctness and security defects, and
            these objective engineering defects — duplicating logic or a component the repo already
            has (name it); business rules placed in a view, component, controller or SQL string; new
            or changed logic with no test when the repo has a test framework; a swallowed exception,
            empty catch or ignored error result; a blocking call on an async path, an N+1 or an
            unbounded query on a request path; a hard-coded secret or environment value; dead code,
            commented-out code or debug output left in the diff; a public entry point without input
            validation; a dependency added when the repo already has an equivalent. Follow your
            `engineering-review` and `design-review` skills for the full rubric. Everything that is
            a matter of taste or preference is [Suggestion] — a blocked merge costs a full cycle.

            ## Response format
            Respond with ONLY a valid JSON object (no markdown fences, no extra text):
            {
              "decision": "approve" | "request_changes",
              "summary": "overall assessment in markdown — what's good, what must change",
              "comments": [
                { "path": "relative/file/path", "line": 123, "body": "[Critical]/[Suggestion]/[Nit] comment text" }
              ]
            }
            Rules:
            - "line" must reference a line number in the NEW version of the file as shown in the diff.
            - Only use "request_changes" for [Critical] issues — see "What blocks". Suggestions and nits alone mean "approve".
            - If the implementation matches the task and plan and has no critical defects, approve it.
            {{AgentLanguage.Instruction(LanguageFor(task, project))}}
            """;

    /// <summary>
    /// The environment an agent run is started with: the commit identity it is not allowed to
    /// change.
    ///
    /// GIT_AUTHOR_*/GIT_COMMITTER_* rather than git config, because config is ADVICE and these
    /// are not: git reads them ahead of the local, global and system config files, so an agent
    /// that runs `git config user.email ...` in its worktree changes a value nothing will read.
    /// The bare repo's config (ApplyCommitIdentityAsync) still carries the same identity as the
    /// belt to this pair of braces — that one is what a human sees when they inspect the clone.
    /// </summary>
    internal static (string System, string User) BuildDevelopingPrompt(
        TaskSnapshot task, ProjectSummary? project, RepoSummary? repo, AgentSummary? agent, string memories, string plan,
        string? contextFilesSection, string? conversationSection, string? siblingRepos = null)
    {
        var persona = agent is not null
            ? BuildPersonaSection(agent, codeWritingPhase: true)
            : "You are an AI software developer. You are implementing a coding task according to an approved plan.";

        var checkCommand = repo?.CheckCommand;
        var checkNotice = string.IsNullOrWhiteSpace(checkCommand)
            ? ""
            : $"""

              After your run the platform executes `{checkCommand}` in this worktree; a non-zero
              exit sends the task straight back to you with the output, so run it yourself and make
              it pass before finishing.
              """;

        var system = $"""
            {persona}
            {memories}
            ## Current Assignment
            Implement the coding task described by the user message according to its approved plan.
            - Follow the plan closely, including its "Existing code to reuse" and "Design" sections.
            - Read the craft skill that matches the work (`code-craft`; `frontend-craft` when it
              renders UI) before you start, and build to your engineering bar.
            - Commit as you go, in small commits with clear, descriptive messages.

            ## Self-review before you finish
            Re-read the full `git diff` against the plan and your engineering bar: remove debris
            (dead code, debug output, commented-out code, unrelated reformatting), check that every
            new public surface validates its input and has tests, and name what you reused. Then
            run the repo's lint, typecheck and tests and put the exact commands and their pass/fail
            in your final message.{checkNotice}

            ## Tests are part of the implementation
            Write tests for what you build, in the same run: unit tests for the logic you add or
            change, and integration tests for any endpoint or cross-component behaviour you
            touch — success, error, and validation paths. Follow the repo's existing test
            framework and layout; if the repo has no test framework at all, say so rather than
            scaffolding one. A later phase RUNS these tests and reports what fails, so coverage
            you skip here is coverage nobody adds afterwards.

            ## Definition of Done
            Your work is complete when the code builds, existing and new tests pass, and
            lint/type checks are clean. Do NOT launch the application or game, start dev
            servers, drive a browser, or capture screenshots/window recordings as proof —
            live verification is handled later in the pipeline (QA and code review), and
            attempting it here only burns your run's time budget. This applies even if a
            script, README, or CLAUDE.md in the repo suggests capturing visual proof.

            {RunnableContract.DevAgentSection}
            ## If you are blocked
            If you genuinely cannot proceed without a decision or clarification from the human:
            1. Commit and push the work you have completed so far.
            2. End your run with a final message containing your question inside a tag, exactly like:
               <question>Your question for the human here</question>
            Only do this for true blockers — check the conversation for earlier answers first.
            {RefusedToolNotice}
            {AutonomyInstruction(EffectiveTaskPolicy.For(task, project?.Settings), "in your commit message and final message")}
            {ClaudeMdGuidelines}
            {AgentLanguage.Instruction(LanguageFor(task, project))}
            """;

        var userBuilder = new System.Text.StringBuilder();
        userBuilder.Append(BuildTaskContextSection(task, repo, contextFilesSection, conversationSection, siblingRepos));
        userBuilder.AppendLine($"Branch: {task.BranchName ?? "unknown"}");

        userBuilder.AppendLine();
        userBuilder.AppendLine("## Approved Plan");
        userBuilder.AppendLine(plan);
        userBuilder.AppendLine();
        userBuilder.AppendLine("""
            Implement this plan now. The plan's "filesChanged" list was produced by actually
            exploring this repository during planning — start by reading those exact files and
            trust them as your map. Only search more broadly if the plan turns out to be missing
            something. Create or update CLAUDE.md files in each directory where you make
            significant changes.
            """);

        if (task.ReviewCycleCount > 0)
        {
            userBuilder.AppendLine();
            userBuilder.AppendLine("""
                NOTE: A code reviewer requested changes on your previous implementation — see the
                "Code review feedback" entries in the conversation above. Address every [Critical]
                point before finishing.
                """);
        }

        if (task.CheckCycleCount > 0)
        {
            userBuilder.AppendLine();
            userBuilder.AppendLine($"""
                NOTE: The automated check failed after your previous pass — see "Automated checks
                failed" in the conversation above for the command output. Fix the code (not the
                check) and run `{checkCommand ?? "the repo's lint, typecheck and tests"}` yourself
                until it passes before finishing.
                """);
        }

        userBuilder.AppendLine();
        userBuilder.AppendLine(CommitInstruction);

        return (system, userBuilder.ToString());
    }

    /// <summary>
    /// The project's stored run targets, or null when there are none / the lookup fails.
    /// A plain row read — no model call and no repo walk, so the Testing phase can ask on
    /// every run. Failure is deliberately swallowed: which QA procedure to use is a
    /// refinement, and losing a whole QA cycle because a config read threw would be a far
    /// worse outcome than falling back to the browser lane.
    /// </summary>
    private async Task<IReadOnlyList<RunTargetDto>?> TryGetRunTargetsAsync(
        IServiceScope scope, TaskSnapshot task, CancellationToken ct)
    {
        try
        {
            var detector = scope.ServiceProvider.GetRequiredService<IRunConfigDetector>();
            var config = await detector.GetStoredAsync(task.OrgId, task.ProjectId, ct);
            return config?.Targets;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex,
                "Could not read run targets for project {ProjectId} — QA falls back to browser mode",
                task.ProjectId);
            return null;
        }
    }

    /// <summary>
    /// The QA agent's system prompt. Two procedures, one verdict contract: a repo that serves
    /// a UI is driven through a browser, one that serves only an HTTP API is verified with its
    /// own test suite plus curl. Extracted so both branches are assertable — a prompt that
    /// silently loses a section degrades every QA run in the lane and nothing else catches it.
    /// </summary>
    internal static string BuildQaSystemPrompt(
        TaskSnapshot task, ProjectSummary? project, AgentSummary? agent, string memories,
        string runCommand, bool apiMode)
    {
        var procedure = apiMode
            ? $$"""
            ## Current Assignment
            Verify this task's changes against the running API (test cycle {{task.QaCycleCount + 1}} of {{MaxQaCycles}} before human escalation).
            You are in a checkout of the task branch with the implemented changes. This repo serves
            no browser UI, so you verify it over HTTP — you have no browser tools in this run.

            ## How to test
            1. Start the app yourself with Bash using this exact command, run in the BACKGROUND — i.e. wrapped as `nohup <command> > qa-app.log 2>&1 &` in an ordinary Bash call (the Bash tool's run_in_background option is not available here):
            {{PromptSafety.Wrap("qa-run-command", runCommand)}}
            2. Wait for it to be ready — poll with `curl` against the local URL it prints (do not use netstat/ss).
            3. Find and run the repo's own test suite (check the manifest's test script; Glob/Grep for test files). Report every failure it produces that relates to this task.
            4. Then exercise the endpoints THIS task changed with `curl` yourself — success path, error and validation paths, auth if required. Assert status codes AND response bodies; a 200 carrying the wrong payload is a failure.
            5. Before you finish, kill the app process you started.

            You have no Write or Edit tools here by design: writing tests is the dev agent's job.
            If the changed endpoints have NO test coverage in the suite, that is itself a failure
            entry — the dev agent was asked to write them, and a human should know it did not.
            """
            : $$"""
            ## Current Assignment
            Test this task's feature end-to-end in a real browser (test cycle {{task.QaCycleCount + 1}} of {{MaxQaCycles}} before human escalation).
            You are in a checkout of the task branch with the implemented changes.

            ## Starting the app under test
            1. Start the app yourself with Bash using this exact command, run in the BACKGROUND — i.e. wrapped as `nohup <command> > qa-app.log 2>&1 &` in an ordinary Bash call (the Bash tool's run_in_background option is not available here):
            {{PromptSafety.Wrap("qa-run-command", runCommand)}}
            2. Wait for it to be ready — poll with `curl` against the local URL it prints (do not use netstat/ss).
            3. Drive the app at that localhost URL with the Playwright browser tools (browser_navigate, browser_click, browser_type, browser_snapshot). Prefer accessibility snapshots over screenshots for behaviour — cheaper and more reliable.
            4. Responsive and visual check, for every screen this task added or changed: resize the browser (browser_resize) to 375x812 and then to 1280x800. At each size, run `document.documentElement.scrollWidth > document.documentElement.clientWidth` with browser_evaluate — `true` is a failure (the body scrolls horizontally) — and take ONE screenshot (browser_take_screenshot) and look at it. Text wrapping one word per line, overlapping or clipped elements, a header that does not fit, a missing logo where the brand has one, unstyled fallback fonts, or a landing screen that is empty when the task was meant to show data are failures, reproduced as "open <path> at <width>".
            5. Before you finish, kill the app process you started.
            """;

        var system = $$"""
            {{BuildPersonaSection(agent)}}
            {{memories}}
            {{procedure}}

            ## Response format
            End with ONLY a valid JSON object (no markdown fences, no extra text):
            {
              "decision": "pass" | "fail",
              "summary": "overall assessment in markdown — what was tested and what you observed",
              "failures": [
                { "criterion": "what should work", "steps": "how to reproduce", "expected": "expected behavior", "actual": "observed behavior" }
              ]
            }
            Rules:
            - "fail" means the feature does not work as the task requires — including when the app fails to start because of this task's changes. Every failure entry must be reproducible from your steps.
            - Only test what this task changed; do not fail for pre-existing issues outside its scope (mention them in the summary instead).
            {{AgentLanguage.Instruction(LanguageFor(task, project))}}
            """;

        return system;
    }

    /// <summary>
    /// The agent commits; the platform pushes after the run. The command guard refuses
    /// <c>git push</c>, so telling an agent to push only produces "known remote error, not in my
    /// control" transcripts.
    /// </summary>
    private const string CommitInstruction =
        """
        When done, commit all changes with a clear commit message:
        ```
        git add -A
        git commit -m "your commit message"
        ```
        Do NOT run `git push` — Orkka pushes the branch itself right after this run, and
        opens the pull request once the work is reviewed. Finish once the commit is in.
        """;

    /// <summary>
    /// User message for a resumed Developing run. The CLI session transcript already holds
    /// the approved plan, context files/documents, and earlier conversation — re-sending
    /// them would pay for the same bytes again, so only the new feedback travels.
    /// </summary>
    internal static string BuildResumeDevelopingUserMessage(
        TaskSnapshot task, string? newConversation, bool interrupted = false)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(PromptSafety.Notice);
        sb.AppendLine();
        sb.AppendLine("You are resuming your previous session on this task:");
        sb.AppendLine(PromptSafety.Wrap("task-title", task.Title));
        sb.AppendLine();
        sb.AppendLine("Your earlier context (approved plan, context files, documents, conversation) " +
                      "is already in this session — do not ask for it again.");

        // A run cut off mid-tool-call leaves the CLI's own cancellation notice in the session
        // transcript, worded as a human refusal ("The user doesn't want to take this action
        // right now"). Left unexplained, agents read it as a permissions restriction and park
        // the task to ask about it (observed: a dev agent asked whether new-file creation was
        // forbidden after four such cancellations). Unconditional because the poisoned entry
        // can sit in a session whose run still COMPLETED normally afterwards.
        sb.AppendLine();
        sb.AppendLine("If this session's history shows a tool call cancelled with \"The user " +
                      "doesn't want to take this action right now\", that was the platform cutting " +
                      "the run off mid-tool-call — NOT a human refusing the action, and NOT a " +
                      "permissions restriction. Nothing is blocked for you: retry such actions " +
                      "freely instead of asking whether they are allowed.");

        if (interrupted)
        {
            sb.AppendLine();
            sb.AppendLine("Your previous run was CUT OFF mid-flight (the connection dropped, the runner " +
                          "went away, or the machine restarted) — it did not fail on its own merits and " +
                          "you did not choose to stop. Any edits it had already written are still in your " +
                          "worktree. Check what is there, keep what is good, and carry on from that point. " +
                          "Do not start the task over and do not re-explore ground you already covered.");
        }

        if (!string.IsNullOrEmpty(newConversation))
        {
            sb.AppendLine();
            sb.Append(newConversation);
            sb.AppendLine();
            sb.AppendLine("Address EVERY point above (QA failures, [Critical] review comments, or the " +
                          "human's feedback) before finishing.");
        }
        // An interrupted run already got its instruction above; "re-queued for another
        // pass" would contradict it.
        else if (!interrupted)
        {
            sb.AppendLine();
            sb.AppendLine("The task was re-queued for another development pass. Re-check your previous " +
                          "work and finish anything incomplete.");
        }

        sb.AppendLine();
        sb.AppendLine(CommitInstruction);
        return sb.ToString();
    }

    /// <summary>
    /// Guard for the read-only phases (QA, review). A branch the base no longer merges into is
    /// not the code that will land, so testing or reviewing it measures the wrong thing — and
    /// neither agent can be handed conflict markers to sort out. Sends the task back for a dev
    /// pass that reconciles the two.
    ///
    /// Returns true when the phase must stop: either the task was re-queued, or its retry budget
    /// is spent and it is now parked for a human.
    /// </summary>
    private async Task<bool> BounceForBaseSyncAsync(
        ITasksApi tasksApi, TaskSnapshot task, RepoSummary repo, Ulid executionId,
        BaseSyncOutcome? sync, string baseBranch, CancellationToken ct)
    {
        if (sync is not { Status: BaseSyncStatus.Aborted }) return false;

        var reason =
            $"'{baseBranch}' moved {sync.CommitsBehind} commit(s) ahead and no longer merges "
            + $"cleanly into this branch — {sync.ConflictedFiles.Count} file(s) collide: "
            + string.Join(", ", sync.ConflictedFiles.Take(10))
            + (sync.ConflictedFiles.Count > 10 ? ", …" : "");

        var requeue = await tasksApi.RequeueForBaseSyncAsync(task.Id, MaxBaseSyncCycles, reason, ct);
        if (requeue is null) return false;

        await tasksApi.MarkExecutionFailedAsync(task.Id, executionId, reason, ct);

        if (requeue.EscalatedToHuman)
        {
            logger.LogWarning(
                "Task {TaskId} has spent its {Max} base-sync passes on {BaseBranch} — parking for a human",
                task.Id, MaxBaseSyncCycles, baseBranch);
            await SendProgressAsync(task.Id, null,
                $"Could not reconcile `{baseBranch}` after {MaxBaseSyncCycles} attempts — this one needs a human.");
        }
        else
        {
            logger.LogInformation(
                "Task {TaskId}: base {BaseBranch} no longer merges cleanly — back to Developing (pass {Attempt}/{Max})",
                task.Id, baseBranch, requeue.BaseSyncCycleCount, MaxBaseSyncCycles);
        }

        await taskNotifier.SendLaneChangedAsync(task.ProjectId, new LaneChangedEvent(
            task.Id, task.Title, requeue.FromLane.ToString(), requeue.NewLane.ToString(),
            requeue.NewStatus.ToString(), DateTimeOffset.UtcNow));

        return true;
    }

    /// <summary>
    /// Prepended to a Developing run whose worktree holds a conflicted merge of the base branch.
    /// Written as work rather than as an error: the agent is the only party that holds both the
    /// task's intent and the incoming change, which is the whole reason the merge waited for it
    /// instead of being attempted at squash-merge time by a resolver with no context.
    /// </summary>
    internal static string BuildBaseSyncConflictSection(
        RepoSummary repo, BaseSyncOutcome sync, string baseBranch)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"## Finish the in-progress merge of `{baseBranch}` first");
        sb.AppendLine();
        sb.AppendLine(
            $"`{baseBranch}` moved {sync.CommitsBehind} commit(s) ahead of this branch while you were "
            + "working, and merging it in conflicts with your changes. The merge is already staged in your "
            + "worktree — do this before anything else:");
        sb.AppendLine();
        sb.AppendLine("1. Resolve every conflicted file, keeping BOTH sides' intent. Read the incoming");
        sb.AppendLine("   change before you overwrite it — it is another task's finished, merged work,");
        sb.AppendLine("   not a stale draft.");
        sb.AppendLine("2. Stage the resolved files and run: `git commit --no-edit`");
        sb.AppendLine("3. Re-check your own work against what just arrived. If the incoming change moved");
        sb.AppendLine("   or renamed something you depend on, adapt to it rather than reverting it.");
        sb.AppendLine();
        sb.AppendLine("Conflicted files:");
        foreach (var file in sync.ConflictedFiles)
            sb.AppendLine($"- `{file}`");
        sb.AppendLine();
        sb.AppendLine("Do NOT run `git merge --abort` — abandoning the merge only moves this collision to");
        sb.AppendLine("merge time, where nobody has the context to resolve it.");
        return sb.ToString();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PR helpers & conflict resolution
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether the task's recorded PR is merged or closed — i.e. nothing can act on it any
    /// more and the branch's new commits need a fresh one. False on any lookup failure, so a
    /// transient forge error never churns pull requests.
    /// </summary>
    private async Task<bool> IsPullRequestDoneAsync(
        IServiceScope scope, TaskSnapshot task, RepoSummary repo, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(task.PrUrl))
            return false;

        var number = ParsePrNumber(task.PrUrl);
        if (number is null)
            return false;

        try
        {
            var pullRequests = scope.ServiceProvider.GetRequiredService<IPullRequestService>();
            var pr = await pullRequests.GetAsync(repo.Id, number.Value, ct);
            return pr.IsSuccess && PullRequestIsDone(pr.Value!);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not check the state of {PrUrl} for task {TaskId}", task.PrUrl, task.Id);
            return false;
        }
    }

    /// <summary>Merged, or closed without merging — either way no review can land on it.</summary>
    internal static bool PullRequestIsDone(GitHubPullRequestDetail pr) =>
        pr.Merged || !string.Equals(pr.State, "open", StringComparison.OrdinalIgnoreCase);

    private static int? ParsePrNumber(string? prUrl) =>
        int.TryParse(prUrl?.Split('/').LastOrDefault(), out var n) ? n : null;

    /// <summary>Per-file choices (path → "task" | "current" | "ai") from the conflict
    /// snapshot JSON the merge page wrote; null when absent or unparseable.</summary>
    internal static Dictionary<string, string>? ParseFileChoices(string? conflictsJson)
    {
        if (string.IsNullOrEmpty(conflictsJson))
            return null;

        try
        {
            var snapshot = JsonSerializer.Deserialize<MergeConflictSnapshot>(
                conflictsJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var choices = snapshot?.Files
                .Where(f => f.Choice is "task" or "current" or "ai")
                .ToDictionary(f => f.Path, f => f.Choice!);
            return choices is { Count: > 0 } ? choices : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Renders the approved plan's camelCase JSON (summary/approach/filesChanged) as
    /// markdown for the PR body. Falls back to a fenced code block if the JSON is invalid.
    /// </summary>
    internal static string FormatPlanForPrBody(string planJson)
    {
        // Keep the PR body well under GitHub's 65,536-char limit
        const int maxApproachChars = 12_000;
        try
        {
            using var doc = JsonDocument.Parse(planJson);
            var root = doc.RootElement;
            var sb = new System.Text.StringBuilder();

            if (root.TryGetProperty("summary", out var summaryEl)
                && summaryEl.GetString() is { Length: > 0 } summary)
            {
                sb.AppendLine(summary).AppendLine();
            }

            if (root.TryGetProperty("approach", out var approachEl)
                && approachEl.GetString() is { Length: > 0 } approach)
            {
                if (approach.Length > maxApproachChars)
                    approach = approach[..maxApproachChars] + "\n\n*… (truncated)*";
                sb.AppendLine(approach).AppendLine();
            }

            if (root.TryGetProperty("filesChanged", out var filesEl)
                && filesEl.ValueKind == JsonValueKind.Array
                && filesEl.GetArrayLength() > 0)
            {
                sb.AppendLine("### Planned file changes");
                foreach (var file in filesEl.EnumerateArray())
                {
                    var path = file.TryGetProperty("path", out var p) ? p.GetString() : null;
                    if (string.IsNullOrEmpty(path)) continue;
                    sb.Append("- `").Append(path).Append('`');
                    if (file.TryGetProperty("action", out var a)
                        && a.GetString() is { Length: > 0 } action)
                    {
                        sb.Append(" (").Append(action).Append(')');
                    }
                    if (file.TryGetProperty("description", out var d)
                        && d.GetString() is { Length: > 0 } description)
                    {
                        sb.Append(" — ").Append(description);
                    }
                    sb.AppendLine();
                }
            }

            var text = sb.ToString().TrimEnd();
            return text.Length > 0 ? text : "_No plan details available._";
        }
        catch (JsonException)
        {
            return $"```json\n{planJson}\n```";
        }
    }

    public async Task ResolveConflictsAndMergeAsync(Ulid taskId, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var tasksApi = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var pullRequests = scope.ServiceProvider.GetRequiredService<IPullRequestService>();
        var projectsApi = scope.ServiceProvider.GetRequiredService<IProjectsApi>();

        var task = await tasksApi.GetTaskSnapshotAsync(taskId, ct);

        // The pending strategy IS the "a resolution is running" flag: it pins the merge
        // page on its spinner, makes /resolve-merge reject a fresh pick as already in
        // progress, and keeps the merge sweep re-firing this same run. So it must be
        // cleared however this attempt ends — including the early exits below and any
        // throw — or a single failed attempt leaves the task loading forever with no way
        // out for the user.
        if (task is not null)
        {
            // The automatic pass arms no strategy, so nothing has stamped a start for it and
            // the merge page would have no elapsed time to show. No-ops when one of the armed
            // paths already stamped it.
            try
            {
                await tasksApi.MarkMergeResolutionStartedAsync(taskId, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not stamp the resolution start for task {TaskId}", taskId);
            }
        }

        try
        {
            await ResolveConflictsAndMergeCoreAsync(
                scope, tasksApi, pullRequests, projectsApi, task, taskId, ct);
        }
        finally
        {
            // Unconditional now, where it used to run only for an armed strategy: this is
            // also where a follow-up queued mid-run is promoted to the active request, and
            // the automatic pass can collect one just as easily as a human-picked attempt.
            await ClearMergeResolutionRequestAsync(tasksApi, taskId);
        }
    }

    private async Task ResolveConflictsAndMergeCoreAsync(
        IServiceScope scope, ITasksApi tasksApi, IPullRequestService pullRequests,
        IProjectsApi projectsApi, TaskSnapshot? task, Ulid taskId, CancellationToken ct)
    {
        var repo = task?.RepoId is { } repoId ? await projectsApi.GetRepoAsync(repoId, ct) : null;

        if (task is null || repo is null || task.BranchName is null)
        {
            logger.LogWarning("Cannot resolve conflicts for task {TaskId} — missing repo or branch", taskId);
            return;
        }

        var route = executionRouter.Resolve(task);

        // The end of a Story checks every repository it touches; a conflict in another one is
        // resolved there first (and the landing retried when this member's own is clean).
        if (task.GroupId is not null && task.StoryOrder is not null
            && await ResolveStoryRepositoriesAsync(scope, tasksApi, pullRequests, projectsApi, task, repo, route, ct))
            return;

        logger.LogInformation("Starting conflict resolution for task {TaskId} on branch {Branch}",
            taskId, task.BranchName);

        // Local-folder fail-fast for a human-picked strategy: the final step squash-merges
        // inside the developer's working repository, which refuses to run while that repo has
        // uncommitted changes or the wrong branch checked out. Probe the merge first so
        // those failures surface immediately instead of after a full resolver-agent run —
        // and when the branch no longer conflicts at all, the probe simply completes it.
        if (task.MergeResolutionStrategy is not null && RepoUrl.Classify(repo.GitUrl) == RepoLocation.LocalFolder
            && await TryFinishLocalMergeEarlyAsync(tasksApi, pullRequests, task, repo, route, taskId, ct))
        {
            return;
        }

        // Ensure bare clone is up to date (fetches latest main)
        var resolverClone = await route.Git.EnsureBareCloneAsync(task.OrgId, repo.Id, repo.GitUrl, repo.LocalCheckoutPath, ct);
        if (resolverClone.IsFailure)
        {
            await MergeFailureNotifications.NotifyMergeFailureAsync(tasksApi, taskNotifier, taskId,
                $"Conflict resolution could not start: the repository could not be refreshed ({resolverClone.Error}).", ct);
            return;
        }

        // Ensure worktree exists — start from the feature branch (not main)
        // so the agent can merge main INTO the feature branch and resolve conflicts
        var worktreeResult = await route.Git.CreateWorktreeAsync(
            task.OrgId, repo.Id, taskId, task.BranchName,
            task.BranchName, worktreeName: null, ct);

        if (worktreeResult.IsFailure)
        {
            logger.LogWarning("Failed to create worktree for conflict resolution on task {TaskId}: {Error}",
                taskId, worktreeResult.Error);
            await MergeFailureNotifications.NotifyMergeFailureAsync(tasksApi, taskNotifier, taskId,
                $"Conflict resolution could not start: {worktreeResult.Error}", ct);
            return;
        }

        var worktreePath = worktreeResult.Value!;
        // The branch this task's PR actually targets — recorded at workspace setup; resolved
        // (rather than assumed to be the default) only for tasks that predate the record. That
        // is what keeps the conflict the agent is asked to fix the same conflict the merge is
        // blocked on.
        var baseBranch = task.BaseBranch ?? repo.DefaultBranch;

        await ResolveConflictsCoreAsync(
            scope, tasksApi, pullRequests, task, repo, route,
            worktreePath, baseBranch, taskId, ct);
    }

    /// <summary>
    /// Ends the attempt that was holding the task. Also the hand-off point: when the user
    /// queued a follow-up while this ran, and the merge still hasn't landed, that follow-up
    /// becomes the active request and the sweep starts it on its next cycle.
    /// </summary>
    private async Task ClearMergeResolutionRequestAsync(ITasksApi tasksApi, Ulid taskId)
    {
        try
        {
            var promoted = await tasksApi.ClearTaskMergeResolutionAsync(taskId, CancellationToken.None);
            if (promoted is not null)
                logger.LogInformation(
                    "Task {TaskId}: attempt finished unmerged — starting the queued '{Strategy}' follow-up",
                    taskId, promoted);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to clear merge resolution request for task {TaskId}", taskId);
        }
    }

    /// <summary>
    /// Orkka Local pre-flight before running a human-picked resolution: attempts the
    /// squash merge directly. True = the flow is finished — either the PR merged (no
    /// conflicts remained) or the merge is environment-blocked (recorded on the task;
    /// the user must fix their repo, no resolver can help). False = a conflict-type
    /// failure or the probe itself failed — run the normal resolution flow.
    /// </summary>
    private async Task<bool> TryFinishLocalMergeEarlyAsync(
        ITasksApi tasksApi, IPullRequestService pullRequests, TaskSnapshot task, RepoSummary repo,
        ExecutionRoute route, Ulid taskId, CancellationToken ct)
    {
        try
        {
            var prNumber = ParsePrNumber(task.PrUrl);
            if (prNumber is null) return false;

            var mergeResult = await pullRequests.MergeAsync(repo.Id, prNumber.Value, ct);

            if (mergeResult.IsSuccess)
            {
                // Forge PR first: "merged with no forge PR" reads as a folder landing.
                if (mergeResult.Value!.ForgeUrl is { } forgeUrl)
                    await tasksApi.SetTaskPublishedPrUrlAsync(taskId, forgeUrl, ct);
                await tasksApi.MarkTaskMergedAsync(taskId, mergeResult.Value.MergeCommitSha, ct);
                logger.LogInformation(
                    "PR for task {TaskId} merged directly — no conflict resolution needed", taskId);
                await route.Git.CleanupWorktreeAsync(task.OrgId, repo.Id, task.Id);
                return true;
            }

            if (MergeBlockedReasons.IsEnvironmentBlocked(mergeResult.Error))
            {
                logger.LogWarning("Merge for task {TaskId} is blocked before resolution could run: {Error}",
                    taskId, mergeResult.Error);
                await MergeFailureNotifications.NotifyMergeFailureAsync(
                    tasksApi, taskNotifier, taskId, mergeResult.Error!, ct);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Local merge pre-flight failed for task {TaskId} — running full resolution", taskId);
            return false;
        }
    }

    private async Task ResolveConflictsCoreAsync(
        IServiceScope scope, ITasksApi tasksApi, IPullRequestService pullRequests,
        TaskSnapshot task, RepoSummary repo, ExecutionRoute route,
        string worktreePath, string baseBranch,
        Ulid taskId, CancellationToken ct)
    {
        var strategy = task.MergeResolutionStrategy;

        // ── Deterministic side-picking (no tokens) ────────────────────────────
        // "task"/"current" take a whole side for every conflict; "perFile" applies the
        // user's per-file picks. Files assigned to AI (or an unavailable op on an old
        // runner) fall through to the resolver agent below.
        IReadOnlyList<string>? remainingForAgent = null;
        var needAgent = true;
        if (strategy is "task" or "current" or "perFile")
        {
            var choices = strategy == "perFile" ? ParseFileChoices(task.MergeConflictsJson) : null;
            var applied = await route.Git.ApplyMergeStrategyAsync(
                task.OrgId, repo.Id, taskId, baseBranch, strategy, choices, ct);

            if (applied.IsSuccess && applied.Value!.Completed)
            {
                needAgent = false;
            }
            else if (applied.IsSuccess)
            {
                remainingForAgent = applied.Value!.RemainingConflicts;
            }
            else
            {
                logger.LogInformation(
                    "Deterministic merge strategy '{Strategy}' unavailable for task {TaskId} ({Error}) — using the resolver agent",
                    strategy, taskId, applied.Error);
            }
        }

        if (needAgent && !await RunResolverUntilVerifiedAsync(
                scope, tasksApi, task, repo, route, worktreePath, baseBranch, strategy,
                remainingForAgent, taskId, ct))
        {
            return;
        }

        // The deterministic path commits without an agent, but "-X ours" plus explicit per-file
        // checkouts can still leave a modify/delete case half-staged. It gets checked too — the
        // whole point is that nothing reaches the base branch unverified.
        if (!needAgent && !await VerifyOrFailAsync(tasksApi, task, repo, route, baseBranch, taskId, ct))
            return;

        await PushAndRetryMergeAsync(tasksApi, pullRequests, task, repo, route, taskId, ct);
    }

    /// <summary>How many times the resolver may be run for one collision. More than one because
    /// a verified failure is genuinely new information — the retry is told exactly what it left
    /// broken — and a blind repeat would just spend the same tokens twice.</summary>
    private const int MaxResolverPasses = 3;

    /// <summary>
    /// Runs the resolver agent and does not accept its own word for the result: after each pass
    /// the worktree is inspected, and a merge that is unfinished, abandoned, or committed with
    /// conflict markers sends the agent back in with that specific fault as its brief.
    ///
    /// Returns false when the collision is genuinely unresolved and a failure has been recorded.
    /// </summary>
    private async Task<bool> RunResolverUntilVerifiedAsync(
        IServiceScope scope, ITasksApi tasksApi, TaskSnapshot task, RepoSummary repo,
        ExecutionRoute route, string worktreePath, string baseBranch, string? strategy,
        IReadOnlyList<string>? remainingForAgent, Ulid taskId, CancellationToken ct)
    {
        string? feedback = null;
        // What beat it last, so the terminal message tells the human which problem to look at.
        // "Could not finish the merge" and "merged it but broke the build" send a reader to
        // completely different places in the diff.
        var lastFault = "could not produce a finished merge";

        for (var pass = 1; pass <= MaxResolverPasses; pass++)
        {
            if (!await RunMergeResolverAgentAsync(
                    scope, tasksApi, task, route, worktreePath, baseBranch, strategy,
                    remainingForAgent, feedback, pass, taskId, ct))
            {
                return false;
            }

            var verification = await route.Git.VerifyMergeAsync(
                task.OrgId, repo.Id, taskId, baseBranch, ct);

            if (verification.IsFailure)
            {
                // Cannot verify is NOT verified. Pushing on the resolver's word is how committed
                // conflict markers reached a base branch once; this pass counts as failed and
                // the next one is briefed with why.
                logger.LogWarning(
                    "Task {TaskId}: merge could not be verified ({Error}) — not pushing",
                    taskId, verification.Error);
                feedback = $"The merge could not be verified: {verification.Error}. "
                    + "Make sure the merge is committed, the worktree is clean, and origin/"
                    + $"{baseBranch} is contained in the branch.";
                continue;
            }

            if (verification.Value!.Completed)
            {
                // Structurally finished. Whether it COMPILES is a separate question, and the
                // one that decides if this merge is safe to land.
                var build = await RunMergeBuildCheckAsync(task, repo, route, taskId, baseBranch, ct);
                if (build is null)
                {
                    if (pass > 1)
                        logger.LogInformation("Task {TaskId}: merge verified on pass {Pass}", taskId, pass);
                    return true;
                }

                feedback = build;
                remainingForAgent = null;
                lastFault = "merged the branches but left the build failing";
                logger.LogWarning(
                    "Task {TaskId}: merge resolved but the build check failed on pass {Pass}/{Max}",
                    taskId, pass, MaxResolverPasses);
                continue;
            }

            feedback = DescribeVerificationFailure(verification.Value, baseBranch);
            lastFault = SummarizeVerification(verification.Value, baseBranch).TrimEnd('.');

            // The worktree has moved since the deterministic pass staged things; the next pass
            // has to re-read the real state rather than trust a stale file list.
            remainingForAgent = null;

            logger.LogWarning(
                "Task {TaskId}: resolver pass {Pass}/{Max} did not finish the merge — retrying with the fault",
                taskId, pass, MaxResolverPasses);
        }

        await MergeFailureNotifications.NotifyMergeFailureAsync(tasksApi, taskNotifier, taskId,
            $"Automatic conflict resolution ran {MaxResolverPasses} times against '{baseBranch}' and "
            + $"{lastFault}. This one needs a person.", ct, MergeFailureKind.ResolutionExhausted);
        return false;
    }

    /// <summary>How long a merge-path build check may run. Short on purpose: this is a compile
    /// check on the merge path, not a test suite, and it is paid on every conflicted merge.</summary>
    private const int MergeBuildTimeoutSeconds = 600;

    /// <summary>
    /// Runs the repo's build command against a structurally-finished merge. Returns null when
    /// the merge is good to push — which includes every case where the check could not run at
    /// all (no command configured, an old runner, a timeout). A build check that cannot execute
    /// must not become a merge that cannot land: it is a stronger signal when present, never a
    /// new precondition.
    ///
    /// Non-null is the compiler's own output, formatted as the resolver's next brief.
    /// </summary>
    private async Task<string?> RunMergeBuildCheckAsync(
        TaskSnapshot task, RepoSummary repo, ExecutionRoute route, Ulid taskId,
        string baseBranch, CancellationToken ct)
    {
        var buildCommand = !string.IsNullOrWhiteSpace(repo.BuildCommand)
            ? repo.BuildCommand
            : (await ReadManifestCommandsAsync(task, repo, route, ct)).Build;
        if (string.IsNullOrWhiteSpace(buildCommand))
            return null;

        var result = await route.Git.RunWorktreeCommandAsync(
            task.OrgId, repo.Id, taskId, buildCommand, RepoCommandKind.Build, MergeBuildTimeoutSeconds, ct);

        if (result.IsFailure)
        {
            logger.LogInformation(
                "Task {TaskId}: merge build check could not run ({Error}) — accepting the verified merge",
                taskId, result.Error);
            return null;
        }

        if (result.Value!.ApprovalNeeded is { } buildApproval)
        {
            await NoteCommandAwaitingApprovalAsync(taskId, buildApproval, ct);
            return null;
        }

        if (result.Value!.Succeeded)
        {
            await SendProgressAsync(taskId, null,
                $"Merged `{baseBranch}` and the build passed ({result.Value.DurationSeconds:0}s).");
            return null;
        }

        // The tail is where a compiler says what is broken; the head is banner noise.
        var output = result.Value.Output ?? "";
        var tail = output.Length <= 6_000 ? output : "…\n" + output[^6_000..];

        return $"""
            The merge you produced does not build. `{buildCommand}` exited
            {result.Value.ExitCode}.

            This is the merge's fault, not a pre-existing failure — the branch built before the
            merge and the base builds on its own. You most likely took one side of a conflict
            without updating what the other side changed around it: a renamed symbol, a changed
            signature, a moved import, a removed export.

            Read the errors, fix the code, and commit. Do not silence them by reverting the
            incoming changes.

            ```
            {tail}
            ```
            """;
    }

    /// <summary>
    /// Runs the repo's check command (lint/typecheck/tests) against what Developing produced.
    /// Returns null when the task may advance — a passing check, or every case where the check
    /// could not run at all (no command, old runner, timeout): like the merge build check, it is
    /// a stronger signal when present, never a new precondition. Non-null is the failure brief
    /// the dev agent reads on its next pass; a non-zero exit is the one case that fails CLOSED.
    /// </summary>
    private async Task<(string? Failure, string? Command)> RunDevelopingCheckAsync(
        TaskSnapshot task, RepoSummary repo, ExecutionRoute route, Ulid taskId, CancellationToken ct)
    {
        var command = !string.IsNullOrWhiteSpace(repo.CheckCommand)
            ? repo.CheckCommand
            : (await ReadManifestCommandsAsync(task, repo, route, ct)).Check;
        if (string.IsNullOrWhiteSpace(command))
            return (null, null);

        await SendProgressAsync(taskId, null, $"Running checks: `{command}`");

        var result = await route.Git.RunWorktreeCommandAsync(
            task.OrgId, repo.Id, taskId, command, RepoCommandKind.Check, _checkTimeoutSeconds, ct);

        if (result.IsFailure)
        {
            logger.LogInformation(
                "Task {TaskId}: check command could not run ({Error}) — advancing without it",
                taskId, result.Error);
            return (null, command);
        }

        if (result.Value!.ApprovalNeeded is { } checkApproval)
        {
            await NoteCommandAwaitingApprovalAsync(taskId, checkApproval
                + " The checks did not run for this pass.", ct);
            return (null, command);
        }

        if (result.Value!.Succeeded)
        {
            await SendProgressAsync(taskId, null,
                $"Checks passed: `{command}` ({result.Value.DurationSeconds:0}s).");
            return (null, command);
        }

        return (FormatCheckFailure(command, result.Value.ExitCode, result.Value.Output), command);
    }

    /// <summary>The brief a failed check becomes in the conversation. Tail, not head: the tail
    /// is where a compiler or test runner says what is broken; the head is banner noise.</summary>
    internal static string FormatCheckFailure(string command, int exitCode, string? output)
    {
        var text = output ?? "";
        var tail = text.Length <= 6_000 ? text : "…\n" + text[^6_000..];

        return $"""
            ## Automated checks failed
            `{command}` exited {exitCode} on your branch after your run. The task is back with
            you: read the output, fix the code (not the check), and make the command pass before
            you finish — run it yourself this time and report the result.

            ```
            {tail}
            ```
            """;
    }

    /// <summary>Verification gate for a merge no agent touched. False = recorded and stopped.</summary>
    private async Task<bool> VerifyOrFailAsync(
        ITasksApi tasksApi, TaskSnapshot task, RepoSummary repo, ExecutionRoute route,
        string baseBranch, Ulid taskId, CancellationToken ct)
    {
        var verification = await route.Git.VerifyMergeAsync(task.OrgId, repo.Id, taskId, baseBranch, ct);
        if (verification.IsFailure || verification.Value!.Completed)
            return true;

        await MergeFailureNotifications.NotifyMergeFailureAsync(tasksApi, taskNotifier, taskId,
            "The chosen merge resolution did not produce a finished merge: "
            + SummarizeVerification(verification.Value, baseBranch), ct,
            MergeFailureKind.ResolutionExhausted);
        return false;
    }

    /// <summary>One-line, human-readable version of what a verification found.</summary>
    internal static string SummarizeVerification(MergeVerification v, string baseBranch)
    {
        var parts = new List<string>();
        if (!v.BaseMerged)
            parts.Add($"the branch still does not contain '{baseBranch}'");
        if (v.UnmergedPaths.Count > 0)
            parts.Add($"{v.UnmergedPaths.Count} file(s) left unmerged");
        if (v.FilesWithMarkers.Count > 0)
            parts.Add($"{v.FilesWithMarkers.Count} file(s) still contain conflict markers");
        return parts.Count == 0 ? "the merge is incomplete" : string.Join("; ", parts) + ".";
    }

    /// <summary>
    /// The retry brief. Names the exact fault rather than saying "try again" — a resolver that
    /// committed conflict markers believes it succeeded, and repeating the original instruction
    /// would produce the same output for the same reason.
    /// </summary>
    internal static string DescribeVerificationFailure(MergeVerification v, string baseBranch)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Your previous attempt did NOT finish the merge. Specifically:");
        sb.AppendLine();

        if (!v.BaseMerged)
        {
            sb.AppendLine(
                $"- The branch still does not contain origin/{baseBranch}. The merge was aborted or "
                + "never committed. Start it again and see it through — do NOT run `git merge --abort`.");
        }

        if (v.UnmergedPaths.Count > 0)
        {
            sb.AppendLine(
                "- These paths are still unmerged — resolve and `git add` each one:");
            foreach (var path in v.UnmergedPaths)
                sb.AppendLine($"  - {path}");
        }

        if (v.FilesWithMarkers.Count > 0)
        {
            sb.AppendLine(
                "- These files were COMMITTED with conflict markers (`<<<<<<<` / `>>>>>>>`) still in "
                + "them. Committing the markers is not resolving the conflict — it ships broken code. "
                + "Open each one, decide what the merged result should actually be, and rewrite it:");
            foreach (var path in v.FilesWithMarkers)
                sb.AppendLine($"  - {path}");
        }

        sb.AppendLine();
        sb.AppendLine("Fix every point above, stage the files, and commit. Verify your own work with "
                      + "`git diff --name-only --diff-filter=U` (must be empty) and by grepping the "
                      + "files you touched for `<<<<<<<`.");
        return sb.ToString();
    }

    /// <summary>Runs the resolver agent; false = it errored (failure already recorded).</summary>
    private async Task<bool> RunMergeResolverAgentAsync(
        IServiceScope scope, ITasksApi tasksApi, TaskSnapshot task, ExecutionRoute route,
        string worktreePath, string baseBranch, string? strategy,
        IReadOnlyList<string>? remainingForAgent, string? verificationFeedback, int pass,
        Ulid taskId, CancellationToken ct)
    {
        // Conflict resolution is bash/git in a worktree — no MCP — so any connected
        // provider can run it. Claude when available (today's behavior; hosted always).
        var (mergeProvider, mergeModel) = SelectMergeResolver(
            route.Readiness,
            await scope.ServiceProvider.GetRequiredService<IModelCatalog>().GetAsync(ct));

        // The user's whole-side choice (when the deterministic path was unavailable) and
        // free-text guidance travel as prompt rules.
        var strategyRules = strategy switch
        {
            "task" =>
                "\n- The user chose to KEEP THIS TASK'S VERSION wherever the two sides overlap: resolve every conflict by taking the current branch's side.",
            "current" =>
                $"\n- The user chose to KEEP THE APP AS IT IS wherever the two sides overlap: resolve every conflict by taking the origin/{baseBranch} side.",
            _ => "",
        };
        if (!string.IsNullOrWhiteSpace(task.MergeResolutionGuidance))
            strategyRules += $"\n- User guidance for resolving the conflicts: {task.MergeResolutionGuidance}";

        // What this branch was FOR. Without it the resolver is asked to "preserve the intent of
        // both sides" while being told neither — it sees two versions of a hunk and guesses.
        const int intentCap = 4_000;
        static string Cap(string text) =>
            text.Length <= intentCap ? text : text[..intentCap] + "\n[… truncated]";

        var plan = await tasksApi.GetLatestApprovedPlanAsync(taskId, ct);
        var intent = new System.Text.StringBuilder();
        intent.AppendLine("## What this branch was trying to do");
        intent.AppendLine(PromptSafety.Wrap("task-title", task.Title));
        if (!string.IsNullOrWhiteSpace(task.Description))
            intent.AppendLine(PromptSafety.Wrap("task-description", Cap(task.Description!)));
        if (!string.IsNullOrWhiteSpace(plan?.Summary))
            intent.AppendLine(PromptSafety.Wrap("approved-plan-summary", Cap(plan!.Summary)));

        var jobSteps = remainingForAgent is not null
            ? $"""
              A merge of origin/{baseBranch} is ALREADY IN PROGRESS in this worktree — the user's
              per-file choices are applied and staged. Your job:
              1. Resolve ONLY the remaining conflicted files: {string.Join(", ", remainingForAgent)}
              2. Stage the resolved files and run: git commit --no-edit
              """
            : $"""
              Your job:
              1. Run: git fetch origin
              2. Run: git merge origin/{baseBranch}
              3. If there are merge conflicts, resolve them intelligently by reading both sides and producing correct merged code.
              4. After resolving all conflicts, stage the resolved files and run: git commit --no-edit
              5. If the merge succeeds with no conflicts, do nothing extra — the merge commit is enough.
              """;

        var request = new AgentRequest(
            SystemPrompt: $"""
                You are a merge conflict resolver for a software project.
                You are working in a git worktree on branch '{task.BranchName}'.
                The base branch is '{baseBranch}'.

                {intent}
                The other side is not noise: every commit on '{baseBranch}' is finished work that
                another task already landed. Before you resolve anything, find out what it was:
                - `git log --oneline HEAD..origin/{baseBranch}` — what arrived while this branch ran
                - `git log --oneline origin/{baseBranch}..HEAD` — what this branch did
                - `git log -p -1 <sha>` on any incoming commit that touches a conflicted file
                Read the surrounding file too. A conflict is only resolvable once you know what
                both sides were trying to achieve.

                {jobSteps}

                Rules:
                - Produce the code that satisfies BOTH intents. That is usually not either side
                  verbatim — if one side renamed a symbol and the other added a call to it, the
                  answer is the new name with the new call, not a choice between two hunks.
                - Do NOT delete or discard changes from either side unless one side is clearly obsolete.
                - NEVER commit conflict markers. A file containing `<<<<<<<`, `=======` or `>>>>>>>`
                  is not resolved, and committing it ships code that does not parse.
                - Keep the result consistent beyond the conflicted hunks: if you take an incoming
                  rename or signature change, update this branch's callers of it too.
                - If you genuinely cannot tell which behavior is correct, prefer keeping both
                  behaviors over dropping one.
                - Do NOT run git push — that will be handled separately.{strategyRules}

                Before you finish, check your own work:
                - `git diff --name-only --diff-filter=U` must print nothing
                - none of the files you touched may still contain `<<<<<<<`
                - `git log --oneline -1` must show the merge you just committed
                """,
            UserMessage: verificationFeedback is not null
                ? verificationFeedback
                : remainingForAgent is not null
                    ? "Finish the in-progress merge by resolving the remaining conflicts."
                    : $"Merge origin/{baseBranch} into the current branch and resolve any conflicts.",
            WorktreePath: worktreePath,
            Provider: mergeProvider,
            Model: mergeModel,
            MaxTurns: _budgets.MergeResolve.MaxTurns,
            MaxToolCalls: _budgets.MergeResolve.MaxToolCalls,
            MaxWallClockSeconds: _budgets.MergeResolve.MaxWallClockSeconds,
            MaxCostUsd: _budgets.MergeResolve.MaxCostUsd,
            Phase: AgentPhase.Developing,
            AgentName: "merge-resolver",
            AgentRole: "developer",
            OrgId: task.OrgId,
            ProjectId: task.ProjectId,
            TaskId: task.Id);

        logger.LogInformation("Running conflict resolution agent for task {TaskId}", taskId);

        string? agentError = null;
        var usage = (Prompt: 0, Completion: 0, CacheRead: 0, CacheWrite: 0);
        decimal? vendorCost = null;

        await foreach (var chunk in route.Invoker.InvokeAsync(request, ct))
        {
            switch (chunk.Type)
            {
                case AgentChunkType.Error:
                    logger.LogError("Conflict resolution agent error for task {TaskId}: {Error}", taskId, chunk.Content);
                    agentError = chunk.Content;
                    break;
                case AgentChunkType.Done:
                    usage = (chunk.PromptTokens, chunk.CompletionTokens, chunk.CacheReadTokens, chunk.CacheWriteTokens);
                    vendorCost = chunk.VendorCostUsd;
                    break;
            }
        }

        // Conflict resolution burns real tokens — bill them like any other run
        if (usage.Prompt > 0 || usage.Completion > 0)
        {
            try
            {
                var usageRecorder = scope.ServiceProvider.GetRequiredService<IUsageRecorder>();
                await usageRecorder.RecordUsageAsync(new RecordUsageCommand(
                    task.OrgId, task.ProjectId, taskId, null, null,
                    request.Model ?? "claude-sonnet-5",
                    usage.Prompt, usage.Completion, usage.CacheRead, usage.CacheWrite,
                    FlatCostBase: 0m,
                    UserId: task.CreatedByUserId,
                    VendorReportedCostUsd: vendorCost), ct);

                await tasksApi.AddTaskTokensUsedAsync(taskId, usage.Prompt + usage.Completion, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to record conflict-resolution usage for task {TaskId}", taskId);
            }
        }

        if (agentError is not null)
        {
            await MergeFailureNotifications.NotifyMergeFailureAsync(tasksApi, taskNotifier, taskId,
                $"Automatic conflict resolution failed: {agentError}", ct,
                ResolverFailureKind(agentError));
            return false;
        }

        return true;
    }

    private async Task PushAndRetryMergeAsync(
        ITasksApi tasksApi, IPullRequestService pullRequests, TaskSnapshot task, RepoSummary repo,
        ExecutionRoute route, Ulid taskId, CancellationToken ct)
    {
        if (task.BranchName is not { Length: > 0 } branchName)
        {
            logger.LogWarning(
                "No branch on task {TaskId} after conflict resolution — nothing to push", taskId);
            await MergeFailureNotifications.NotifyMergeFailureAsync(tasksApi, taskNotifier, taskId,
                "The resolved merge could not be pushed: this task no longer has a branch.", ct,
                MergeFailureKind.PushFailed);
            return;
        }

        var pushResult = await route.Git.EnsurePushedAsync(
            task.OrgId, repo.Id, taskId, branchName, ct);

        if (pushResult.IsFailure)
        {
            logger.LogWarning("Push failed after conflict resolution for task {TaskId}: {Error}",
                taskId, pushResult.Error);
            await MergeFailureNotifications.NotifyMergeFailureAsync(tasksApi, taskNotifier, taskId,
                $"Failed to push conflict resolution: {pushResult.Error}", ct,
                MergeFailureKind.PushFailed);
            return;
        }

        logger.LogInformation("Conflict resolution complete for task {TaskId} — retrying merge", taskId);

        var prNumber = ParsePrNumber(task.PrUrl);
        if (prNumber is null)
        {
            logger.LogWarning("Cannot parse PR number from {PrUrl} for task {TaskId}", task.PrUrl, taskId);
            return;
        }

        // A Gated cross-repo group lands all-or-nothing, and only the merge sweep knows when
        // every sibling can land. The resolver's job ends at a mergeable branch: merging here
        // put one half of a group on main while its sibling had not even started (prod,
        // 2026-09-15, StoreFront task 6), which is exactly what the gate exists to prevent.
        if (task.GroupId is { } groupId)
        {
            var groupState = (await tasksApi.GetGroupMergeStatesAsync([groupId], ct)).FirstOrDefault();
            if (groupState is { MergePolicy: TaskGroupMergePolicy.Gated, Ready: false })
            {
                // The conflict is over: without this the notice keeps the task demoted out of
                // Done and counted as stuck by its own group's gate, which then never opens.
                await tasksApi.ClearTaskMergeFailureAsync(taskId, ct);
                logger.LogInformation(
                    "Task {TaskId}: conflict resolution pushed — the merge waits for its cross-repo group ({BlockedBy})",
                    taskId, groupState.BlockedBy ?? "siblings still in flight");
                return;
            }
        }

        // Merges a folder in place, or publishes a remote's work to its forge.
        var mergeResult = await pullRequests.MergeAsync(repo.Id, prNumber.Value, ct);

        if (mergeResult.IsSuccess)
        {
            // Merge-candidate tasks are already in Done, so this only flips IsMerged
            // (MarkTaskMergedAsync leaves lane/status untouched for Done tasks).
            // Forge PR first: "merged with no forge PR" reads as a folder landing.
            if (mergeResult.Value!.ForgeUrl is { } forgeUrl)
                await tasksApi.SetTaskPublishedPrUrlAsync(taskId, forgeUrl, ct);
            await tasksApi.MarkTaskMergedAsync(taskId, mergeResult.Value.MergeCommitSha, ct);
            logger.LogInformation("PR merged after conflict resolution for task {TaskId}", taskId);

            // Work is merged — the worktree is no longer needed.
            await route.Git.CleanupWorktreeAsync(task.OrgId, repo.Id, task.Id);
        }
        else
        {
            logger.LogWarning("Merge still failed after conflict resolution for task {TaskId}: {Error}",
                taskId, mergeResult.Error);
            await MergeFailureNotifications.NotifyMergeFailureAsync(tasksApi, taskNotifier, taskId,
                mergeResult.Error ?? "Merge failed after conflict resolution", ct);
        }
    }
}
