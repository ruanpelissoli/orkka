namespace Orkka.Modules.Agents.Features.AgentChat;

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Domain;
using Orkka.Modules.Agents.Features.AgentChat.Tools;
using Orkka.Modules.Agents.Infrastructure;
using Orkka.Modules.Usage.Contracts;
using Orkka.Modules.Documents.Contracts;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.SharedKernel.Ai;
using Orkka.SharedKernel.Events;
using Orkka.SharedKernel.Events.Catalog;
using Orkka.SharedKernel.Utilities;

/// <summary>
/// Executes one agent-chat turn to completion: invokes the agent, persists the assistant
/// message, extracts diagrams/tasks/documents/design assets, records usage, titles the
/// conversation, and maintains the rolling summary. Runs detached from the SSE request in
/// its own DI scope (see <see cref="ChatRunCoordinator"/>): SSE payloads are emitted into
/// the run handle's channel, and a browser disconnect merely stops the relay — everything
/// here still happens, so no reply or burned tokens are ever lost.
///
/// IMPORTANT: the scope has no authenticated ICurrentUser, so the tenant query filter is
/// OFF — OrgId comes from the spec and must be set on new rows and filtered in queries
/// explicitly.
/// </summary>
public sealed partial class ChatTurnRunner(
    AgentsDbContext db,
    IChatAgentInvoker chatInvoker,
    IUsageRecorder usageRecorder,
    IProjectsApi projectsApi,
    ITasksApi tasksApi,
    IDocumentsApi documentsApi,
    IUserRunnerUtility runnerUtility,
    IOptions<AnthropicOptions> anthropicOptions,
    IOptions<ChatSummaryOptions> summaryOptions,
    DesignAssetGenerator designGenerator,
    ITaskNotifier taskNotifier,
    IEventBus eventBus,
    IServiceScopeFactory scopeFactory,
    ILogger<ChatTurnRunner> logger)
{
    public async Task RunAsync(ChatTurnSpec spec, ChatRunHandle handle, CancellationToken ct)
    {
        var conversation = await db.AgentConversations
            .FirstAsync(c => c.Id == spec.ConversationId && c.OrgId == spec.OrgId, CancellationToken.None);

        try
        {
            await RunCoreAsync(spec, handle, conversation, ct);
        }
        finally
        {
            // The conversation is only "thinking" while this run lives — clear the marker on
            // every exit path (success, fault, cancel), never on the run token: it may
            // already be cancelled.
            try
            {
                conversation.ActiveRunStartedAt = null;
                await db.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                // The staleness window in the response mapper neutralizes a leaked marker.
                logger.LogWarning(ex, "Failed to clear active-run marker for conversation {ConversationId}",
                    spec.ConversationId);
            }
        }
    }

    private async Task RunCoreAsync(
        ChatTurnSpec spec, ChatRunHandle handle, AgentConversation conversation, CancellationToken ct)
    {
        // Repos missing from the workspace, said out loud before the agent answers. Without
        // it the user asks about their own code, is told it cannot be found, and the reason
        // — almost always "git on your machine is not signed in to that remote" — exists
        // only in a pod log. Emitted like the retry notice below, on the same channel the
        // client already renders as agent progress.
        if (!string.IsNullOrWhiteSpace(spec.WorkspaceNotice))
        {
            logger.LogInformation(
                "Chat turn for conversation {ConversationId} runs without some repos — notifying the user",
                spec.ConversationId);
            // Same payload shape as the transient-retry notice further down. A dedicated
            // "notice" event would render better, but only once a client knows the key —
            // this one is shown today, which is the difference between the user learning
            // about their broken git credentials and not.
            handle.Emit(new { thinking = spec.WorkspaceNotice });
        }

        // Pre-allocated so tool-created artifacts can link to the assistant message
        // (SourceMessageId) BEFORE its row exists — every save path below uses this id.
        var assistantMessageId = Ulid.NewUlid();

        // With tools enabled, the turn carries the "orkka" MCP tool server: handlers create
        // documents/tasks/diagrams/designs mid-stream through the module facades, scoped to
        // this turn's identity (never tool inputs), and emit the same SSE payloads the
        // post-stream extractors emit. Each handler call opens its own DI scope — it runs
        // concurrently with this scope's DbContext.
        OrkkaChatToolServer? toolServer = null;
        ChatTurnToolContext? toolContext = null;
        if (spec.ToolsEnabled)
        {
            toolContext = new ChatTurnToolContext
            {
                OrgId = spec.OrgId,
                ProjectId = spec.ProjectId,
                AgentId = spec.AgentId,
                ConversationId = spec.ConversationId,
                AssistantMessageId = assistantMessageId,
                RequesterUserId = spec.RequesterUserId,
                RequesterLocale = spec.RequesterLocale,
                AgentRole = spec.AgentRole,
                DesignFolderPath = spec.DesignFolderPath,
                Emit = handle.Emit,
            };
            toolServer = new OrkkaChatToolServer(toolContext, scopeFactory, logger);
        }

        // A resume attempt withholds error events: its failure may be retried below with a
        // fresh session, and the client must not see the intermediate error.
        var outcome = await StreamAgentRunAsync(
            spec, handle, spec.SystemPrompt, spec.ResumeSessionId, toolServer, toolContext,
            suppressErrorEvents: spec.ResumeSessionId is not null, ct);

        if (spec.ResumeSessionId is not null && outcome.Error is not null && !outcome.Cancelled)
        {
            if (!outcome.ReceivedOutput && !ClaudeCliErrors.IsTransient(outcome.Error))
            {
                // The warm session could not be resumed (session file lives on the machine
                // that ran the previous turn — a different runner/replica or a cleanup loses
                // it) and nothing reached the client yet. Run again with a fresh session and
                // the full history-injected prompt — exactly the pre-resume cold path.
                logger.LogInformation(
                    "CLI session {SessionId} could not be resumed for conversation {ConversationId} ({Error}) — starting a fresh session",
                    spec.ResumeSessionId, spec.ConversationId, outcome.Error);

                conversation.ClaudeSessionId = null;
                conversation.SessionProvider = null;
                var resumeAttempt = outcome;
                outcome = await StreamAgentRunAsync(
                    spec, handle, spec.FreshSystemPrompt, resumeSessionId: null, toolServer,
                    toolContext, suppressErrorEvents: false, ct);
                // Tokens burned by the failed resume attempt still bill.
                outcome = outcome with
                {
                    InputTokens = outcome.InputTokens + resumeAttempt.InputTokens,
                    OutputTokens = outcome.OutputTokens + resumeAttempt.OutputTokens,
                    CacheReadTokens = outcome.CacheReadTokens + resumeAttempt.CacheReadTokens,
                    CacheWriteTokens = outcome.CacheWriteTokens + resumeAttempt.CacheWriteTokens,
                    // Same rule as the tokens: the abandoned resume attempt was really charged,
                    // so the vendor figure must cover it too or it won't line up with ours.
                    VendorCostUsd = outcome.VendorCostUsd + resumeAttempt.VendorCostUsd
                        ?? outcome.VendorCostUsd ?? resumeAttempt.VendorCostUsd,
                };
            }
            else if (!outcome.Faulted)
            {
                // Output already streamed before the failure (rerunning would duplicate it),
                // or the error is a transient transport blip whose in-run retries are
                // already exhausted (the session file is fine — a cold rerun would just
                // re-fail against the same outage) — surface the error event that was
                // withheld during the resume attempt.
                handle.Emit(new { error = outcome.Error });
            }
        }

        // Session id of this run (fresh runs mint one; resumed runs report the same id) —
        // persisted alongside the assistant message so the next turn can resume. Stored
        // only for providers whose resume path is honored end-to-end (claude; codex behind
        // the runner's cli-resume capability), tagged with the provider so a codex thread
        // id is never fed to claude --resume after a model switch.
        var turnProvider = ModelProviders.FromModelId(spec.Model);
        if (!string.IsNullOrWhiteSpace(outcome.SessionId)
            && chatInvoker.SupportsSessionResume(turnProvider))
        {
            conversation.ClaudeSessionId = outcome.SessionId;
            conversation.SessionProvider = turnProvider.ToString();
        }

        if (outcome.Cancelled)
        {
            // Explicit user cancel (or host shutdown). Keep whatever already streamed so
            // the partial reply survives in history. The interrupted attempt's own token
            // counts never arrived (no Done chunk), but transient-retry attempts before it
            // may have — those are folded into the outcome and still bill. Never a
            // notification — the user asked for the stop. Tool-created artifacts are
            // already persisted with this message id, so an empty reply with artifacts
            // must still save the row — otherwise their SourceMessageId dangles and the
            // reload cards vanish.
            await RecordAbortedRunUsageAsync(spec, outcome);
            if (outcome.FullResponse.Length > 0 || (toolContext?.TotalCreated ?? 0) > 0)
            {
                var partial = await SaveAssistantMessageAsync(
                    spec, assistantMessageId, outcome.FullResponse, CancellationToken.None);
                await PushChatMessageAsync(spec, partial);
            }
            handle.Emit(new { done = true, cancelled = true });
            return;
        }

        if (outcome.Faulted)
        {
            // A faulted attempt has no Done chunk (so no usage of its own), but earlier
            // transient-retry attempts folded into the outcome may have burned tokens.
            await RecordAbortedRunUsageAsync(spec, outcome);

            // Persist the failure in the conversation — the SSE error event below is
            // ephemeral, and without it a failed run reads as the agent never replying.
            var failed = await SaveAssistantMessageAsync(
                spec, assistantMessageId,
                outcome.FullResponse.Length > 0 ? outcome.FullResponse : outcome.Error!,
                CancellationToken.None);
            await PushChatMessageAsync(spec, failed);

            handle.Emit(new { error = outcome.Error, done = true });
            await NotifyCompletionAsync(spec, conversation, handle,
                deliverables: toolContext?.TotalCreated ?? 0);
            return;
        }

        var fullResponse = outcome.FullResponse;

        // Save assistant message. A run that produced no text but reported an error (e.g.
        // the runner's claude CLI never started) stores the error instead of an empty
        // message, so the reason survives in history like any other agent reply.
        var assistantMessage = await SaveAssistantMessageAsync(
            spec, assistantMessageId,
            fullResponse.Length == 0 && outcome.Error is not null ? outcome.Error : fullResponse,
            CancellationToken.None);
        await PushChatMessageAsync(spec, assistantMessage);

        // Chat deliverables (diagrams, board tasks, documents, design assets) count toward
        // the agent's card stats below — chat work is work, even outside the pipeline.
        // Tool-created artifacts were persisted and announced mid-stream by the handlers.
        var deliverables = toolContext?.TotalCreated ?? 0;

        // Post-stream processing failures must not lose the already-persisted reply —
        // convert them into an SSE error event instead.
        //
        // With tools enabled, the fenced-block extractors below run as FALLBACKS: each kind
        // is skipped when the tools already created artifacts of that kind this turn (the
        // model occasionally still emits a legacy block out of habit — parsing it after a
        // successful tool call would double-create).
        try
        {
            // Extract and save diagrams if this is a Solution Architect agent
            if (spec.AgentRole == AgentRole.SolutionArchitect && (toolContext?.CreatedDiagrams ?? 0) == 0)
            {
                var diagrams = await ExtractDiagramsAsync(spec, assistantMessage.Id, fullResponse, ct);
                deliverables += diagrams.Count;

                // Tell the client about each created diagram so the chat can link to the Architecture Board
                foreach (var diagram in diagrams)
                    handle.Emit(new { diagram = new { id = diagram.Id.ToString(), title = diagram.Title } });
            }

            // Extract and create board tasks if this is a PM agent
            if (spec.AgentRole == AgentRole.PM && (toolContext?.CreatedTasks ?? 0) == 0)
            {
                var tasks = await ExtractTasksAsync(spec, assistantMessage.Id, fullResponse, ct);
                deliverables += tasks.Count;

                // Tell the client about each created task so the chat can link to the board
                foreach (var task in tasks)
                    handle.Emit(new { task = new { id = task.Id.ToString(), number = task.Number, title = task.Title } });
            }

            // Extract and save documents for any agent role
            if ((toolContext?.CreatedDocuments ?? 0) == 0)
            {
                var extraction = await ExtractDocumentsAsync(spec, assistantMessage.Id, fullResponse, ct);
                deliverables += extraction.Documents.Count;

                // Tell the client about each created document so the chat can link to the Documents page
                foreach (var document in extraction.Documents)
                    handle.Emit(new { document = new { id = document.Id.ToString(), title = document.Title } });

                // A cut-off block is persisted into the reply (like design failures below) so
                // the reason survives a reload, and streamed so the open panel sees it now.
                if (extraction.Salvaged > 0 || extraction.CutOffUnsaved)
                {
                    var note = CutOffDocumentsNote(extraction);
                    assistantMessage.Content += note;
                    await db.SaveChangesAsync(ct);
                    await PushChatMessageAsync(spec, assistantMessage);
                    handle.Emit(new { text = note });
                }
            }

            // Generate design assets if this is a Designer or GameDesigner agent. Image
            // generation takes 10-30s per Gemini call — progress events let the client
            // render a "creating designs" indicator with frame counts.
            if (spec.AgentRole is AgentRole.Designer or AgentRole.GameDesigner
                && (toolContext?.CreatedDesignAssets ?? 0) == 0)
            {
                var designFailures = new List<string>();
                await foreach (var evt in designGenerator.GenerateAsync(
                    spec.OrgId, spec.ProjectId, spec.AgentId, spec.ConversationId,
                    assistantMessage.Id, spec.RequesterUserId, fullResponse,
                    spec.DesignFolderPath, ct))
                {
                    var payload = DesignEventSse.ToPayload(evt);
                    if (payload is null) continue;

                    if (evt is DesignAssetReady)
                        deliverables++;
                    if (evt is DesignAssetFailed failure)
                        designFailures.Add($"{failure.Name}: {failure.Error}");
                    handle.Emit(payload);
                }

                // Failure events are ephemeral SSE — persist them into the reply so a
                // failed generation never masquerades as success on reload (a message
                // whose design block produced no assets used to render a "Design ready"
                // card with the real error visible only in server logs).
                if (designFailures.Count > 0)
                {
                    assistantMessage.Content +=
                        "\n\n⚠️ Design generation failed:\n" +
                        string.Join("\n", designFailures.Select(f => $"- {Truncate(f, 500)}"));
                    await db.SaveChangesAsync(ct);
                    await PushChatMessageAsync(spec, assistantMessage);
                }
            }

            // Auto-title the conversation from its first exchange (also backfills older
            // untitled conversations on their next message). Never blocks the chat on failure.
            if (!spec.ConversationHasTitle && string.IsNullOrWhiteSpace(conversation.Title) && fullResponse.Length > 0)
            {
                var title = await GenerateTitleAsync(spec, fullResponse, ct);
                if (title is not null)
                {
                    conversation.Title = title;
                    handle.Emit(new { title });
                }
            }

            // Record usage
            await usageRecorder.RecordUsageAsync(new RecordUsageCommand(
                OrgId: spec.OrgId,
                ProjectId: spec.ProjectId,
                TaskId: spec.TaskId,
                AgentId: spec.AgentId,
                ExecutionId: null,
                Model: spec.Model,
                InputTokens: outcome.InputTokens,
                OutputTokens: outcome.OutputTokens,
                CacheReadTokens: outcome.CacheReadTokens,
                CacheWriteTokens: outcome.CacheWriteTokens,
                // A turn relayed to the requester's own machine ran on their subscription and
                // cost the platform nothing — recorded at $0, the same convention runner task
                // runs use. Attribution stays visible; only the spend is zero.
                FlatCostBase: outcome.ServedByRunner ? 0m : null,
                UserId: spec.RequesterUserId,
                VendorReportedCostUsd: outcome.VendorCostUsd), ct);

            // Update agent stats. A chat turn that shipped at least one deliverable counts
            // as ONE completed task on the agent's card (not one per artifact — a single
            // ask is a single job), so Luna/Archie/Muse get credit for chat work that
            // never enters the pipeline.
            var agent = await db.ProjectAgents
                .FirstAsync(a => a.Id == spec.AgentId && a.OrgId == spec.OrgId, ct);
            agent.TotalTokensIn += outcome.InputTokens;
            agent.TotalTokensOut += outcome.OutputTokens;
            if (deliverables > 0)
                agent.TasksCompleted += 1;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Post-stream processing failed for conversation {ConversationId}", spec.ConversationId);

            var errorMsg = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
            handle.Emit(new { error = $"The reply was saved, but processing it failed: {errorMsg}", done = true });
            // Artifacts extracted before the failure are already persisted — still notify
            await NotifyCompletionAsync(spec, conversation, handle, deliverables);
            return;
        }

        handle.Emit(new { done = true });
        await NotifyCompletionAsync(spec, conversation, handle, deliverables);

        // Fold older messages into the rolling summary AFTER the done event so an attached
        // observer never waits on it.
        await UpdateContextSummaryAsync(spec, conversation, CancellationToken.None);
    }

    /// <summary>One agent run emitted into the run handle, folded into an outcome instead
    /// of exceptions/locals: Error is set on both error chunks and thrown exceptions
    /// (Faulted distinguishes the latter), Cancelled marks an explicit cancel/shutdown,
    /// ReceivedOutput says whether anything visible was already emitted (a failed resume
    /// can only be retried before that).</summary>
    private sealed record AgentRunOutcome(
        string FullResponse,
        string? Error,
        bool Faulted,
        bool Cancelled,
        bool ReceivedOutput,
        int InputTokens,
        int OutputTokens,
        int CacheReadTokens,
        int CacheWriteTokens,
        string? SessionId,
        decimal? VendorCostUsd = null,
        // The run executed on the requester's own machine under their subscription (see
        // ChatChunk.ServedByRunner) — its tokens are recorded at $0.
        bool ServedByRunner = false);

    /// <summary>Attempts per agent run: the initial run plus transient-error retries.</summary>
    private const int MaxAgentRunAttempts = 3;

    /// <summary>Backoff before retry attempt N (indexed by completed attempts - 1).</summary>
    private static readonly TimeSpan[] AgentRunRetryDelays =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20)];

    /// <summary>Retry prompt for a continuation attempt: the dropped attempt's CLI session
    /// kept the user message and any partial reply, so the retry resumes it and asks the
    /// model to pick up where the stream died instead of replaying the whole turn.
    /// Platform-authored — never wrapped in the untrusted user-message fence.</summary>
    private const string ContinuationMessage =
        "(The connection dropped mid-response and this is an automatic retry on the same " +
        "session. Continue answering the user's previous message from exactly where your " +
        "response stopped — if you had not produced anything yet, simply answer it now. " +
        "Do not repeat content you already produced and do not mention the interruption.)";

    /// <summary>
    /// Runs the agent turn, retrying transient transport failures ("Connection closed
    /// mid-response", resets, API overload) up to <see cref="MaxAgentRunAttempts"/> times
    /// before letting the error reach the client. When the dropped attempt reported a CLI
    /// session id (and the invoker honors resume for this provider), the retry resumes
    /// that session with a continuation prompt — safe even after partial text streamed,
    /// because the model appends instead of replaying. Without that, a retry replays the
    /// whole turn, so it only happens while nothing visible has reached the client.
    /// Text and tokens from failed attempts are folded into the returned outcome so the
    /// persisted reply stays whole and burned tokens are still billed. Error events are
    /// emitted only for the settled outcome (unless <paramref name="suppressErrorEvents"/>,
    /// where the caller owns surfacing them).
    /// </summary>
    private async Task<AgentRunOutcome> StreamAgentRunAsync(
        ChatTurnSpec spec, ChatRunHandle handle, string systemPrompt,
        string? resumeSessionId, IChatToolServer? toolServer, ChatTurnToolContext? toolContext,
        bool suppressErrorEvents, CancellationToken ct)
    {
        var agentMessage = BuildAgentMessage(spec);
        var canResumeSessions =
            chatInvoker.SupportsSessionResume(ModelProviders.FromModelId(spec.Model));

        var earlierText = new StringBuilder();
        var earlierOutput = false;
        int burnedInput = 0, burnedOutput = 0, burnedCacheRead = 0, burnedCacheWrite = 0;

        AgentRunOutcome Merge(AgentRunOutcome last) => last with
        {
            FullResponse = earlierText.Length == 0
                ? last.FullResponse
                : earlierText.ToString() + last.FullResponse,
            ReceivedOutput = last.ReceivedOutput || earlierOutput,
            InputTokens = last.InputTokens + burnedInput,
            OutputTokens = last.OutputTokens + burnedOutput,
            CacheReadTokens = last.CacheReadTokens + burnedCacheRead,
            CacheWriteTokens = last.CacheWriteTokens + burnedCacheWrite,
        };

        for (var attempt = 1; ; attempt++)
        {
            var run = await StreamAgentRunOnceAsync(
                spec, handle, systemPrompt, agentMessage, resumeSessionId, toolServer, toolContext, ct);

            var continueViaResume = canResumeSessions && run.SessionId is not null;
            var retryable = run.Error is not null
                && attempt < MaxAgentRunAttempts
                && ClaudeCliErrors.IsTransient(run.Error)
                && !run.Cancelled
                && !ct.IsCancellationRequested
                // A plain rerun replays the whole turn — only safe while nothing visible
                // has streamed to the client. A session-resume continuation appends
                // instead, so partial output doesn't block it.
                && (continueViaResume || (!run.ReceivedOutput && !earlierOutput));

            if (!retryable)
            {
                var final = Merge(run);
                if (final.Error is not null && !final.Faulted && !suppressErrorEvents)
                    handle.Emit(new { error = final.Error });
                return final;
            }

            burnedInput += run.InputTokens;
            burnedOutput += run.OutputTokens;
            burnedCacheRead += run.CacheReadTokens;
            burnedCacheWrite += run.CacheWriteTokens;
            earlierText.Append(run.FullResponse);
            earlierOutput |= run.ReceivedOutput;

            if (continueViaResume)
            {
                resumeSessionId = run.SessionId;
                agentMessage = ContinuationMessage;
            }

            logger.LogWarning(
                "Transient agent-chat failure for conversation {ConversationId} (attempt {Attempt}/{Max}): {Error} — retrying{Mode}",
                spec.ConversationId, attempt, MaxAgentRunAttempts, run.Error,
                continueViaResume ? " via session resume" : "");

            handle.Emit(new
            {
                thinking = $"Connection to the model dropped — retrying (attempt {attempt + 1} of {MaxAgentRunAttempts})…"
            });

            try
            {
                await Task.Delay(AgentRunRetryDelays[attempt - 1], ct);
            }
            catch (OperationCanceledException)
            {
                // Accumulators already absorbed this attempt — report them directly
                // instead of Merge(run), which would double-count it.
                return run with
                {
                    FullResponse = earlierText.ToString(),
                    ReceivedOutput = earlierOutput,
                    InputTokens = burnedInput,
                    OutputTokens = burnedOutput,
                    CacheReadTokens = burnedCacheRead,
                    CacheWriteTokens = burnedCacheWrite,
                    Cancelled = true,
                };
            }
        }
    }

    private static string BuildAgentMessage(ChatTurnSpec spec)
    {
        // Fenced per the system prompt's ChatNotice — the raw message stays raw
        // everywhere else (persistence, title generation, design-asset markers). The
        // context block (linked documents/attachments) rides AFTER the fence: its
        // inner contents carry their own untrusted fences, but its section headers are
        // platform-authored.
        var agentMessage = PromptSafety.Wrap("user-message", spec.UserMessage);
        if (!string.IsNullOrEmpty(spec.ContextBlock))
            agentMessage += spec.ContextBlock;
        // Skill instructions come first: they frame how the request that follows is handled.
        if (!string.IsNullOrEmpty(spec.InvokedSkillBlock))
            agentMessage = spec.InvokedSkillBlock + agentMessage;

        // Platform-authored, outside the untrusted fence. The questions protocol lives
        // mid-system-prompt and loses salience under long history/repo context — models
        // kept asking choice questions in prose, which the UI can't render as cards.
        // A per-turn reminder at the very end of the input restores compliance.
        agentMessage +=
            "\n\n(Formatting reminder — not part of the user's message: if you ask the user " +
            "to choose between options or answer several scoping questions, present them " +
            "as the fenced ```questions JSON block from your instructions, never as a " +
            "prose/numbered list. Prose before or after the block is fine.)";

        // Same reasoning, same place: the Response Language section sits ahead of the rolling
        // summary and the conversation history in the system prompt, so on long conversations
        // (and on turns where the user types in another language) the model drifts back to
        // English. Restating it last is what makes it stick.
        agentMessage += AgentLanguage.Reminder(spec.RequesterLocale);

        return agentMessage;
    }

    /// <summary>
    /// Forwards one relayed tool artifact to the client and records what kind it was. The
    /// counters are what tell the post-stream extractors that this turn already created its
    /// documents/tasks/diagrams through tools — without them a tool-mode turn would be
    /// scanned for fenced blocks it never wrote.
    /// </summary>
    private void EmitRelayedArtifact(ChatRunHandle handle, ChatTurnToolContext? toolContext, string payloadJson)
    {
        JsonElement payload;
        try
        {
            payload = JsonDocument.Parse(payloadJson).RootElement.Clone();
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Unparseable artifact payload from a relayed chat turn — dropping it");
            return;
        }

        handle.Emit(payload);

        if (toolContext is null)
            return;
        if (payload.TryGetProperty("document", out _)) toolContext.AddCreatedDocuments(1);
        else if (payload.TryGetProperty("task", out _)) toolContext.AddCreatedTasks(1);
        else if (payload.TryGetProperty("diagram", out _)) toolContext.AddCreatedDiagrams(1);
        else if (payload.TryGetProperty("designAsset", out _)) toolContext.AddCreatedDesignAssets(1);
    }

    private async Task<AgentRunOutcome> StreamAgentRunOnceAsync(
        ChatTurnSpec spec, ChatRunHandle handle, string systemPrompt, string agentMessage,
        string? resumeSessionId, IChatToolServer? toolServer, ChatTurnToolContext? toolContext,
        CancellationToken ct)
    {
        var fullResponse = new StringBuilder();
        string? error = null;
        var faulted = false;
        var cancelled = false;
        var receivedOutput = false;
        var inputTokens = 0;
        var outputTokens = 0;
        var cacheReadTokens = 0;
        var cacheWriteTokens = 0;
        string? sessionId = null;
        decimal? vendorCostUsd = null;
        var servedByRunner = false;

        try
        {
            // The identity travels beside the live server: a turn relayed to the user's own
            // machine cannot use an object in this process, so the Worker rebuilds the same
            // tool server from this and forwards the runner's calls to it.
            var toolIdentity = toolContext is null ? null : new ChatToolServerContext(
                toolContext.OrgId, toolContext.ProjectId, toolContext.AgentId,
                toolContext.ConversationId, toolContext.AssistantMessageId,
                toolContext.RequesterUserId, toolContext.RequesterLocale,
                toolContext.AgentRole);

            await foreach (var chunk in chatInvoker.InvokeAgentAsync(
                spec.WorkspacePath, systemPrompt, agentMessage,
                spec.Model, resumeSessionId, spec.ConversationId, toolServer, toolIdentity, ct))
            {
                // Read off every chunk, not just Done: a run that dies mid-stream still has
                // its partial tokens billed by RecordAbortedRunUsageAsync, and those are just
                // as free as a completed turn's when the machine was the user's own.
                servedByRunner |= chunk.ServedByRunner;

                switch (chunk.Type)
                {
                    case ChatChunkType.Thinking:
                        receivedOutput = true;
                        handle.Emit(new { thinking = chunk.Content });
                        break;
                    case ChatChunkType.Text:
                        // CLI error notices ("API Error: Connection lost mid-response…")
                        // stream as assistant text but are not agent prose — keep them
                        // out of the reply, and out of receivedOutput so a run that
                        // produced nothing real stays eligible for a plain rerun.
                        if (ClaudeCliErrors.IsStreamErrorNotice(chunk.Content))
                            break;
                        receivedOutput = true;
                        fullResponse.Append(chunk.Content);
                        handle.Emit(new { text = chunk.Content });
                        break;
                    case ChatChunkType.ToolUse:
                        receivedOutput = true;
                        handle.Emit(new { tool_use = chunk.Content });
                        break;
                    case ChatChunkType.Error:
                        // Not emitted here: the retry wrapper surfaces the error only once
                        // the outcome settles (retries exhausted or non-transient).
                        error = chunk.Content;
                        break;
                    case ChatChunkType.Artifact:
                        // A relayed turn's tools ran on the Worker; its emissions arrive here
                        // as chunks instead of going straight to the SSE stream. Forwarded
                        // verbatim so the panel renders them exactly as on a cloud turn, and
                        // counted so the fenced-block extractors below stay out of the way.
                        receivedOutput = true;
                        EmitRelayedArtifact(handle, toolContext, chunk.Content);
                        break;
                    case ChatChunkType.Done:
                        inputTokens = chunk.PromptTokens;
                        outputTokens = chunk.CompletionTokens;
                        cacheReadTokens = chunk.CacheReadTokens;
                        cacheWriteTokens = chunk.CacheWriteTokens;
                        vendorCostUsd = chunk.VendorCostUsd;
                        if (!string.IsNullOrWhiteSpace(chunk.SessionId))
                            sessionId = chunk.SessionId;
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Only an explicit user cancel or host shutdown reaches here — the SSE
            // observer's disconnect never cancels this token.
            cancelled = true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error streaming agent chat response for conversation {ConversationId}", spec.ConversationId);

            // Known CLI failure signatures (in-process invoker throws raw SDK exceptions,
            // e.g. "Control request timeout: initialize") get an actionable message.
            faulted = true;
            error = ClaudeCliErrors.Describe(ex.Message)
                ?? (ex.Message.Length > 500 ? ex.Message[..500] : ex.Message);
        }

        return new AgentRunOutcome(
            fullResponse.ToString(), error, faulted, cancelled, receivedOutput,
            inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens, sessionId, vendorCostUsd,
            servedByRunner);
    }

    /// <summary>Bills token usage carried by an outcome on exit paths that skip the normal
    /// usage recording (cancelled/faulted). Transient retries fold every failed attempt's
    /// usage into the outcome, so even a run with no visible reply may have burned
    /// billable tokens. Best-effort — these paths are already failing.</summary>
    private async Task RecordAbortedRunUsageAsync(ChatTurnSpec spec, AgentRunOutcome outcome)
    {
        if (outcome.InputTokens == 0 && outcome.OutputTokens == 0
            && outcome.CacheReadTokens == 0 && outcome.CacheWriteTokens == 0)
            return;

        try
        {
            await usageRecorder.RecordUsageAsync(new RecordUsageCommand(
                OrgId: spec.OrgId,
                ProjectId: spec.ProjectId,
                TaskId: spec.TaskId,
                AgentId: spec.AgentId,
                ExecutionId: null,
                Model: spec.Model,
                InputTokens: outcome.InputTokens,
                OutputTokens: outcome.OutputTokens,
                CacheReadTokens: outcome.CacheReadTokens,
                CacheWriteTokens: outcome.CacheWriteTokens,
                FlatCostBase: outcome.ServedByRunner ? 0m : null,
                UserId: spec.RequesterUserId,
                VendorReportedCostUsd: outcome.VendorCostUsd), CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to record usage for aborted chat run in conversation {ConversationId}",
                spec.ConversationId);
        }
    }

    private async Task<AgentMessage> SaveAssistantMessageAsync(
        ChatTurnSpec spec, Ulid messageId, string content, CancellationToken ct)
    {
        // The id is pre-allocated before streaming: tool-created artifacts reference it as
        // their SourceMessageId, and Agents-module artifacts (diagrams, design assets) have
        // real FKs to it — the first such tool call inserts the row as an empty placeholder,
        // which this save fills in. Turns without tool artifacts insert fresh as before.
        var existing = await db.AgentMessages.FirstOrDefaultAsync(
            m => m.Id == messageId && m.OrgId == spec.OrgId, ct);
        if (existing is not null)
        {
            existing.Content = content;
            await db.SaveChangesAsync(ct);
            return existing;
        }

        var assistantMessage = new AgentMessage
        {
            Id = messageId,
            OrgId = spec.OrgId,
            ConversationId = spec.ConversationId,
            Role = "assistant",
            Content = content
        };
        db.AgentMessages.Add(assistantMessage);
        await db.SaveChangesAsync(ct);
        return assistantMessage;
    }

    /// <summary>Pushes the persisted message to the conversation's SignalR group so a
    /// reopened chat panel receives detached replies live. Best-effort — the message is
    /// already in the DB and a reopen fetches it anyway.</summary>
    private async Task PushChatMessageAsync(ChatTurnSpec spec, AgentMessage message)
    {
        try
        {
            await taskNotifier.SendAgentChatMessageAsync(spec.ConversationId, new AgentChatMessageEvent(
                message.Id.ToString(), spec.ConversationId.ToString(), message.Role,
                message.Content, message.CreatedAt));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Realtime chat-message push failed for conversation {ConversationId}",
                spec.ConversationId);
        }
    }

    /// <summary>The bell notification for completed runs. Fires when the run outlived its
    /// SSE observer (the user never saw the reply) OR when it shipped deliverables
    /// (diagrams, tasks, documents, design assets) — artifact runs are long and users tab
    /// away with the stream still attached, so "connected" doesn't mean "watching".
    /// Text-only connected replies stay silent: the reply already streamed to the
    /// user's screen.</summary>
    private async Task NotifyCompletionAsync(
        ChatTurnSpec spec, AgentConversation conversation, ChatRunHandle handle, int deliverables)
    {
        if (!handle.IsDetached && deliverables == 0)
            return;

        await eventBus.PublishAsync(new AgentChatReplyCompleted(
            spec.OrgId, spec.ProjectId, spec.AgentId, spec.ConversationId,
            spec.AgentName, conversation.Title, spec.RequesterUserId));
    }

    /// <summary>
    /// One utility completion for the chat's own housekeeping (titles, the rolling summary),
    /// on the requester's own runner — their subscription, $0. Null when the runner cannot
    /// serve it: there is no platform-key fallback, and housekeeping simply waits — the
    /// conversation stays untitled until a later turn, the summary folds in on the next one.
    /// </summary>
    private async Task<AnthropicResponse?> CompleteUtilityAsync(
        ChatTurnSpec spec, AnthropicRequest request, string purpose, CancellationToken ct)
    {
        var response = await runnerUtility.TryCompleteAsync(
            spec.OrgId, spec.RequesterUserId, request, ct);
        if (response is null)
        {
            logger.LogInformation(
                "Skipped the chat {Purpose} for conversation {ConversationId} — {Reason}",
                purpose, spec.ConversationId,
                RunnerUtilityUnavailableException.MessageFor($"let it run the chat {purpose}"));
        }
        return response;
    }

    private const int MaxConversationTitleLength = 100;
    private const int TitleContextChars = 1500;

    private async Task<string?> GenerateTitleAsync(
        ChatTurnSpec spec, string assistantContent, CancellationToken ct)
    {
        try
        {
            var model = anthropicOptions.Value.FastUtilityModel;
            var response = await CompleteUtilityAsync(spec, new AnthropicRequest
            {
                Model = model,
                MaxTokens = 60,
                // The requester's UI language wins when we know it — a user with a Portuguese
                // interface who types one English message still reads the sidebar in Portuguese.
                System = "You title conversations. Given the first exchange of a chat, reply with ONLY a short title " +
                         "(3-6 words) summarizing what the conversation is about. " +
                         (AgentLanguage.DisplayName(spec.RequesterLocale) is { } titleLanguage
                             ? $"Write the title in {titleLanguage}, whatever language the exchange is in. "
                             : "Write the title in the same language the user writes in. ") +
                         "No quotes, no trailing punctuation, no explanations.",
                Messages =
                [
                    new AnthropicMessage("user",
                        $"User message:\n{Truncate(spec.UserMessage, TitleContextChars)}\n\n" +
                        $"Assistant reply:\n{Truncate(assistantContent, TitleContextChars)}")
                ],
            }, "title", ct);
            if (response is null) return null;

            await usageRecorder.RecordUsageAsync(new RecordUsageCommand(
                OrgId: spec.OrgId,
                ProjectId: spec.ProjectId,
                TaskId: spec.TaskId,
                AgentId: spec.AgentId,
                ExecutionId: null,
                Model: model,
                InputTokens: response.InputTokens,
                OutputTokens: response.OutputTokens,
                CacheReadTokens: response.CacheReadTokens,
                CacheWriteTokens: response.CacheWriteTokens,
                FlatCostBase: 0m,
                UserId: spec.RequesterUserId), ct);

            var title = response.Text.Trim().Trim('"', '“', '”').Trim();
            if (title.Length == 0) return null;
            return title.Length > MaxConversationTitleLength ? title[..MaxConversationTitleLength] : title;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to generate conversation title");
            return null;
        }
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private const int SummaryCharCap = 2_000;
    private const int SummarySourceCharCap = 12_000;
    private const int HistoryMessageCharCap = 3_000;

    // Folds messages older than the recent window into the conversation's rolling summary,
    // advancing the SummarizedMessageCount watermark. Failures only delay summarization
    // until the next message — never the chat itself.
    private async Task UpdateContextSummaryAsync(
        ChatTurnSpec spec, AgentConversation conversation, CancellationToken ct)
    {
        try
        {
            var options = summaryOptions.Value;

            // While a resumable session is stored, the summary is dead weight — it is only
            // consumed on a resume-failure fresh rebuild. Skip the fold (a paid utility
            // call; on Orkka Local a CLI spawn on the user's subscription) and let the
            // backlog fold in one call when the session invalidates.
            if (options.SkipWhileResumable && !string.IsNullOrWhiteSpace(conversation.ClaudeSessionId))
                return;

            var totalCount = await db.AgentMessages.AsNoTracking()
                .CountAsync(m => m.OrgId == spec.OrgId && m.ConversationId == conversation.Id, ct);

            var summarizeThrough = totalCount - options.KeepRecentMessages;
            if (summarizeThrough - conversation.SummarizedMessageCount < options.BatchMinMessages)
                return;

            var batch = await db.AgentMessages.AsNoTracking()
                .Where(m => m.OrgId == spec.OrgId && m.ConversationId == conversation.Id)
                .OrderBy(m => m.CreatedAt)
                .Skip(conversation.SummarizedMessageCount)
                .Take(summarizeThrough - conversation.SummarizedMessageCount)
                .Select(m => new { m.Role, m.Content })
                .ToListAsync(ct);

            var source = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(conversation.ContextSummary))
            {
                source.Append("Existing summary of even earlier messages:\n");
                source.Append(conversation.ContextSummary);
                source.Append("\n\nNew messages to fold in:\n");
            }

            // Always include at least one message so the watermark advances even when a
            // single message exceeds the source budget
            var included = 0;
            foreach (var msg in batch)
            {
                var role = msg.Role == "user" ? "User" : "Assistant";
                var entry = $"\n{role}:\n{Truncate(msg.Content, HistoryMessageCharCap)}\n";
                if (included > 0 && source.Length + entry.Length > SummarySourceCharCap) break;
                source.Append(entry);
                included++;
            }
            if (included == 0) return;

            var model = anthropicOptions.Value.FastUtilityModel;
            var response = await CompleteUtilityAsync(spec, new AnthropicRequest
            {
                Model = model,
                MaxTokens = 700,
                System = "You maintain a running summary of a conversation between a user and an AI project agent. " +
                         "Merge the existing summary (if present) and the new messages into ONE updated summary. " +
                         "Preserve decisions, requirements, names, file paths, numbers, and open questions. " +
                         // The summary is re-injected into the next prompt, so a stray English
                         // fold is also one more nudge pulling the agent's replies off-language.
                         (AgentLanguage.DisplayName(spec.RequesterLocale) is { } summaryLanguage
                             ? $"Write the summary in {summaryLanguage}. "
                             : "Write in the conversation's language. ") +
                         "Reply with ONLY the summary text, under 300 words.",
                Messages = [new AnthropicMessage("user", source.ToString())],
            }, "summary", ct);
            // Runner unavailable: the watermark stays put and the backlog folds in next turn.
            if (response is null) return;

            await usageRecorder.RecordUsageAsync(new RecordUsageCommand(
                OrgId: spec.OrgId,
                ProjectId: spec.ProjectId,
                TaskId: spec.TaskId,
                AgentId: spec.AgentId,
                ExecutionId: null,
                Model: model,
                InputTokens: response.InputTokens,
                OutputTokens: response.OutputTokens,
                CacheReadTokens: response.CacheReadTokens,
                CacheWriteTokens: response.CacheWriteTokens,
                FlatCostBase: 0m,
                UserId: spec.RequesterUserId), ct);

            var summary = response.Text.Trim();
            if (summary.Length == 0) return;

            conversation.ContextSummary = Truncate(summary, SummaryCharCap);
            conversation.SummarizedMessageCount += included;
            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "Updated context summary for conversation {ConversationId} — {Summarized}/{Total} messages folded in",
                conversation.Id, conversation.SummarizedMessageCount, totalCount);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to update context summary for conversation {ConversationId}", conversation.Id);
        }
    }

    private async Task<List<ArchitectureDiagram>> ExtractDiagramsAsync(
        ChatTurnSpec spec, Ulid messageId, string responseText, CancellationToken ct)
    {
        var created = new List<ArchitectureDiagram>();

        var result = DiagramBlockParser.Parse(responseText);
        foreach (var reason in result.SkipReasons)
            logger.LogWarning("Skipping invalid excalidraw block in message {MessageId}: {Reason}", messageId, reason);

        foreach (var block in result.Blocks)
        {
            var diagram = new ArchitectureDiagram
            {
                // Explicit id: adding multiple entities with a default key throws
                // "same key already tracked" before the interceptor can assign ids
                Id = Ulid.NewUlid(),
                OrgId = spec.OrgId,
                ProjectId = spec.ProjectId,
                AgentId = spec.AgentId,
                ConversationId = spec.ConversationId,
                MessageId = messageId,
                Title = block.Title,
                Type = DiagramType.SystemArchitecture,
                DiagramData = block.DiagramJson,
            };

            db.ArchitectureDiagrams.Add(diagram);
            db.ArchitectureDiagramVersions.Add(DiagramSnapshots.FromCurrentState(
                diagram, changedByUserId: null, changedByAgentId: spec.AgentId,
                requestedByUserId: spec.RequesterUserId,
                sourceConversationId: spec.ConversationId,
                sourceMessageId: messageId));
            created.Add(diagram);
        }

        if (created.Count == 0) return created;

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Extracted {Count} diagram(s) from agent response", created.Count);
        return created;
    }

    private static readonly JsonSerializerOptions PmTaskJsonOptions = new() { PropertyNameCaseInsensitive = true };

    // Task rows live in the legacy/Tasks-module store — creation (number reservation +
    // transactional insert + board broadcast) goes through ITasksApi.
    private async Task<IReadOnlyList<CreatedChatTask>> ExtractTasksAsync(
        ChatTurnSpec spec, Ulid messageId, string responseText, CancellationToken ct)
    {
        var specs = new List<PmTaskSpec>();
        foreach (Match match in TasksBlockRegex().Matches(responseText))
        {
            try
            {
                var block = JsonSerializer.Deserialize<PmTasksBlock>(match.Groups[1].Value.Trim(), PmTaskJsonOptions);
                if (block?.Tasks is { Count: > 0 })
                    specs.AddRange(block.Tasks.Where(t => !string.IsNullOrWhiteSpace(t.Title)));
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Skipping invalid PM tasks block in message {MessageId}", messageId);
            }
        }

        if (specs.Count == 0) return [];

        // Resolve repos against ALL project repos, not just those with a Ready workspace
        var projectRepos = (await projectsApi.ListReposAsync(spec.ProjectId, ct))
            .Where(r => r.OrgId == spec.OrgId)
            .Select(r => new ChatRepoRef(r.Id, r.Name))
            .ToList();

        if (projectRepos.Count == 0)
        {
            logger.LogWarning("PM agent emitted a tasks block but project {ProjectId} has no repos — skipping task creation", spec.ProjectId);
            return [];
        }

        // No milestones passed, deliberately: milestone assignment is tool-only. The
        // fenced-block prompt never mentions milestones, so a `milestone` field here would
        // be the model improvising — and an unrecognized name resolves to the Backlog,
        // which is the right outcome. Do not "fix" this by threading milestones in.
        var taskSpecs = ChatArtifactSanitizers.SanitizeTasks(specs, projectRepos);

        var created = await tasksApi.CreateChatTasksAsync(
            spec.OrgId, spec.ProjectId, messageId,
            spec.RequesterUserId, spec.RequesterLocale, taskSpecs, ct);

        logger.LogInformation("Created {Count} task(s) from PM agent chat message {MessageId}", created.Count, messageId);
        return created;
    }

    [GeneratedRegex(@"```tasks\s*\n([\s\S]*?)```", RegexOptions.Multiline)]
    private static partial Regex TasksBlockRegex();

    // Document rows live in the Documents module — creation (revision 1 included) goes
    // through IDocumentsApi. Runs for EVERY agent role, unlike diagrams (Architect) and
    // tasks (PM): any agent can be asked to document something. Block extraction lives in
    // DocumentBlockParser — document content contains its own ``` fences, so it needs
    // more than a regex.
    /// <param name="Salvaged">Documents that came from a block the reply's output ceiling cut
    /// off — complete before the cut, saved, but not the whole block.</param>
    /// <param name="CutOffUnsaved">A document block was cut off before a single document in it
    /// was complete; nothing from it was saved.</param>
    private sealed record DocumentExtraction(
        IReadOnlyList<CreatedChatDocument> Documents, int Salvaged, bool CutOffUnsaved)
    {
        public static readonly DocumentExtraction None = new([], 0, false);
    }

    private async Task<DocumentExtraction> ExtractDocumentsAsync(
        ChatTurnSpec spec, Ulid messageId, string responseText, CancellationToken ct)
    {
        var specs = DocumentBlockParser.Parse(responseText, out var salvaged)
            .Where(b => b.Documents is { Count: > 0 })
            .SelectMany(b => b.Documents!)
            .ToList();

        if (specs.Count == 0)
        {
            if (FencedBlockMarkers.IndexOf(responseText, "```document", 0) >= 0)
            {
                logger.LogWarning("Message {MessageId} contains a document block that failed to parse", messageId);
                return DocumentExtraction.None with { CutOffUnsaved = true };
            }
            return DocumentExtraction.None;
        }

        if (salvaged > 0)
            logger.LogWarning(
                "Message {MessageId}: the last document block was cut off — salvaged {Count} complete document(s) from it",
                messageId, salvaged);

        var documentSpecs = ChatArtifactSanitizers.SanitizeDocuments(specs);
        if (documentSpecs.Count == 0) return DocumentExtraction.None;

        var created = await documentsApi.CreateChatDocumentsAsync(
            spec.OrgId, spec.ProjectId, spec.AgentId, spec.ConversationId, messageId,
            spec.RequesterUserId, documentSpecs, ct);

        logger.LogInformation("Created {Count} document(s) from agent chat message {MessageId}", created.Count, messageId);
        return new DocumentExtraction(created, salvaged, false);
    }

    /// <summary>What the user reads when a document block was cut at the model's output
    /// ceiling. The remedy is the same either way: smaller batches. The prompt now asks for
    /// them, but a long consolidation can still overrun, and silence here was an eighteen-minute
    /// turn that "did nothing".</summary>
    private static string CutOffDocumentsNote(DocumentExtraction extraction) =>
        extraction.Salvaged > 0
            ? $"\n\n⚠️ This reply hit the output limit inside its document block. The {extraction.Salvaged} "
              + "document(s) completed before the cut were saved; the rest were not. Ask for the remaining "
              + "ones in smaller batches (a few documents per message)."
            : "\n\n⚠️ This reply hit the output limit before any document in its block was complete, so "
              + "nothing was saved. Ask again in smaller batches (a few documents per message).";
}
