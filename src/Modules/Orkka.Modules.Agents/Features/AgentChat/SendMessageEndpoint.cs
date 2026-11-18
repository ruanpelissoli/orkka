namespace Orkka.Modules.Agents.Features.AgentChat;

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Domain;
using Orkka.Modules.Agents.Infrastructure;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.SharedKernel.Ai;
using Orkka.SharedKernel.Security;
using Orkka.SharedKernel.Utilities;

/// <summary>
/// Accepts a chat message, prepares the turn (workspace, prompts), then starts a DETACHED
/// run via <see cref="ChatRunCoordinator"/> and merely relays its events over SSE. The run
/// itself — agent invocation, persistence, extraction, usage — executes on a background
/// scope with a token linked only to host shutdown, so closing the chat panel never cancels
/// the reply or loses the tokens it burned; the relay just stops and the run finishes
/// detached (completing with a bell notification instead of a stream).
/// </summary>
public sealed partial class SendMessageEndpoint(
    AgentsDbContext db,
    ICurrentUser currentUser,
    IProjectResolver projectResolver,
    ChatContextResolver contextResolver,
    ChatTurnPreparer preparer,
    ChatRunCoordinator coordinator,
    ILogger<SendMessageEndpoint> logger)
    : Endpoint<SendMessageRequest>
{
    public override void Configure()
    {
        Post("/api/projects/{projectId}/agents/{agentId:ulid}/conversations/{conversationId:ulid}/messages");
    }

    public override async Task HandleAsync(SendMessageRequest req, CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.AgentsChat))
            return;

        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        var agentId = Ulid.Parse(Route<string>("agentId"));
        var conversationId = Ulid.Parse(Route<string>("conversationId"));

        var conversation = await db.AgentConversations
            .FirstOrDefaultAsync(c => c.Id == conversationId && c.ProjectId == projectId && c.AgentId == agentId
                && c.OrgId == currentUser.OrgId && c.TaskId == null, ct);

        if (conversation is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // One turn at a time per conversation — concurrent replies would interleave history.
        if (coordinator.IsRunning(conversationId))
        {
            ThrowError("The agent is still working on a reply — wait for it to finish or cancel it first.", 409);
            return;
        }

        // Save user message
        var userMessage = new AgentMessage
        {
            OrgId = currentUser.OrgId,
            ConversationId = conversationId,
            Role = "user",
            Content = req.Content
        };
        db.AgentMessages.Add(userMessage);
        // Marks the conversation Modified so the audit interceptor stamps UpdatedAt —
        // conversation ordering and "most recent" auto-selection follow last activity
        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        // Load agent
        var agent = await db.ProjectAgents
            .FirstAsync(a => a.Id == agentId && a.OrgId == currentUser.OrgId, ct);

        // Design assets the user referenced via [design-asset:...] markers — any agent can
        // be given an asset as context (Muse iterates on it; Archie/devs read its intent).
        var designAssetContext = await BuildDesignAssetContextAsync(projectId, req.Content, ct);

        // Linked documents and uploaded attachments ([document:...] / [attachment:...]
        // markers) expand into a per-turn context block on the agent's copy of the message.
        var linkedContext = await contextResolver.ResolveAsync(
            currentUser.OrgId, projectId, conversationId, req.Content, ct);

        // A leading "/slug" invokes any enabled project skill explicitly for this turn.
        var projectSkills = await preparer.ListEnabledProjectSkillsAsync(currentUser.OrgId, projectId, ct);
        var invokedSkill = Skills.SkillInvocation.TryParse(req.Content, projectSkills);

        // The stream opens HERE, before the turn is prepared, because preparation is not
        // free: on a runner-routed project it clones the repos onto the user's machine, and
        // the first clone of a big project can take many minutes. Behind a closed response
        // that was minutes of a spinner with no words and then a bare 500 ("Luna is
        // unresponsive"). Now the panel gets a line saying what is happening, and a
        // preparation that fails arrives as an error event the panel can show — and as a
        // message in the conversation, so it survives a reload.
        HttpContext.Response.ContentType = "text/event-stream";
        HttpContext.Response.Headers.CacheControl = "no-cache";
        HttpContext.Response.Headers.Connection = "keep-alive";

        ChatTurnSpec spec;
        try
        {
            // Memories, skills, history, workspace, session resume, tools, prompts — the same
            // preparation a scheduled playbook turn gets.
            var preparing = preparer.PrepareAsync(new ChatTurnInput(
                currentUser.OrgId,
                currentUser.UserId,
                currentUser.Locale,
                projectId,
                agent,
                conversation,
                req.Content,
                ExcludeMessageId: userMessage.Id,
                DesignAssetContext: designAssetContext,
                DesignFolderPath: req.DesignFolderPath,
                ContextBlock: linkedContext.ContextBlock,
                InvokedSkillBlock: invokedSkill is null ? null : Skills.SkillInvocation.Block(invokedSkill.Skill)), ct);

            // Only SLOW preparation is worth a word. On a warm workspace this finishes in
            // well under a second, and announcing it every turn made "Preparing the
            // workspace…" the first thing the user read on every single question.
            if (await OutlastsAsync(preparing, PreparingNoticeAfter, ct))
                await WriteEventAsync(new { thinking = PreparingNotice }, ct);

            spec = await preparing;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Chat turn preparation failed for conversation {ConversationId}", conversationId);
            var reason = $"I couldn't get the workspace ready for this turn: {ex.Message}";
            db.AgentMessages.Add(new AgentMessage
            {
                OrgId = currentUser.OrgId,
                ConversationId = conversationId,
                Role = "assistant",
                Content = reason,
            });
            await db.SaveChangesAsync(CancellationToken.None);
            await WriteEventAsync(new { error = reason, done = true }, ct);
            return;
        }

        // The marker is committed BEFORE the run starts so a reopened panel can never
        // miss the "thinking" state; the run clears it on every exit path.
        conversation.ActiveRunStartedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        var handle = coordinator.TryStartRun(spec);
        if (handle is null)
        {
            // Lost a start race with a concurrent send — that run owns the marker now. The
            // headers are out, so this is an event rather than a 409.
            await WriteEventAsync(new
            {
                error = "The agent is still working on a reply — wait for it to finish or cancel it first.",
                done = true,
            }, ct);
            return;
        }

        // Relay the run's events over SSE. From here on the run no longer needs this
        // connection: a disconnect only stops the relay (never the run), and the run
        // notices via MarkDetached and finishes with a bell notification instead.
        try
        {
            await foreach (var payload in handle.Events.ReadAllAsync(ct))
            {
                var sseData = JsonSerializer.Serialize(payload);
                await HttpContext.Response.WriteAsync($"data: {sseData}\n\n", ct);
                await HttpContext.Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            handle.MarkDetached();
        }
        catch (Exception ex)
        {
            // Write failures on a half-closed connection surface as IO/ObjectDisposed
            // exceptions rather than cancellation — same meaning: the observer is gone.
            handle.MarkDetached();
            logger.LogDebug(ex, "SSE relay ended for conversation {ConversationId} — run continues detached", conversationId);
        }
    }

    /// <summary>What the panel shows while the turn is being prepared, and only once
    /// preparation has actually taken <see cref="PreparingNoticeAfter"/> — the case it
    /// describes (a first clone of every repo onto the user's machine) is minutes long, and a
    /// spinner with no words reads as "broken" long before it reads as "cloning". A warm
    /// workspace never shows it.</summary>
    private const string PreparingNotice =
        "Preparing the workspace — the first time on a large project this includes cloning "
        + "its repositories and can take a few minutes.";

    /// <summary>
    /// How long preparation may take before it is worth saying so.
    ///
    /// A warm turn is NOT instant: the host fetches every linked repo and the runner syncs its
    /// own copy, which on a six-repo project runs to several seconds every time. Three seconds
    /// therefore announced a clone on turns that were only syncing — the notice was back on
    /// every message, which is the thing it was gated to stop. Only a genuine first clone
    /// crosses this.
    /// </summary>
    private static readonly TimeSpan PreparingNoticeAfter = TimeSpan.FromSeconds(20);

    /// <summary>True when <paramref name="work"/> is still running after <paramref name="after"/>
    /// — the "this is taking a while, say something" test. Never observes the task's own
    /// result or exception: the caller awaits it afterwards and owns both.</summary>
    internal static async Task<bool> OutlastsAsync(Task work, TimeSpan after, CancellationToken ct)
    {
        if (work.IsCompleted)
            return false;

        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delay = Task.Delay(after, timer.Token);
        var first = await Task.WhenAny(work, delay);
        if (first == work)
        {
            await timer.CancelAsync();
            return false;
        }

        return true;
    }

    private async Task WriteEventAsync(object payload, CancellationToken ct)
    {
        await HttpContext.Response.WriteAsync($"data: {JsonSerializer.Serialize(payload)}\n\n", ct);
        await HttpContext.Response.Body.FlushAsync(ct);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private const int MaxReferencedDesignAssets = 3;
    private const int ReferencedPromptCharCap = 600;

    // The iterate deep-link prepends explicit "[design-asset:<ulid> ...]" marker lines to the
    // user's message (visible content — no hidden seeding, so it survives in history). Resolve
    // them into a system-prompt section so Muse can iterate with basedOnAssetId.
    private async Task<string?> BuildDesignAssetContextAsync(
        Ulid projectId, string userContent, CancellationToken ct)
    {
        var ids = new List<Ulid>();
        foreach (Match match in DesignAssetMarkerRegex().Matches(userContent))
        {
            if (Ulid.TryParse(match.Groups[1].Value, out var id) && !ids.Contains(id))
                ids.Add(id);
            if (ids.Count == MaxReferencedDesignAssets) break;
        }
        if (ids.Count == 0) return null;

        var assets = await db.DesignAssets.AsNoTracking()
            .Where(a => ids.Contains(a.Id) && a.OrgId == currentUser.OrgId && a.ProjectId == projectId)
            .ToListAsync(ct);
        if (assets.Count == 0) return null;

        var sb = new StringBuilder();
        sb.Append("\n\n## Design Assets Referenced In This Conversation\n");
        sb.Append("The user attached these existing assets. When iterating on one, set " +
                  "\"basedOnAssetId\" to its id in your design block so the current image is edited " +
                  "instead of regenerated from scratch. When creating a NEW asset that must show " +
                  "the same character/object (sprite sheets, other facings, scenes), put the id in " +
                  "\"referenceAssetIds\" instead — its image is passed to the model as a visual " +
                  "reference; without it the model cannot reproduce the design.\n");
        foreach (var asset in assets.OrderBy(a => ids.IndexOf(a.Id)))
        {
            sb.Append($"\n- id: {asset.Id} — \"{asset.Name}\" " +
                      $"({asset.Kind.ToString().ToLowerInvariant()}, v{asset.Version}, {asset.Status.ToString().ToLowerInvariant()})");
            if (!string.IsNullOrWhiteSpace(asset.Prompt))
                sb.Append($"\n  Prompt it was generated from: {Truncate(asset.Prompt, ReferencedPromptCharCap)}");
        }
        sb.Append('\n');
        return sb.ToString();
    }

    [GeneratedRegex(@"\[design-asset:([0-9A-Za-z]{26})")]
    private static partial Regex DesignAssetMarkerRegex();

    private const int HistoryCharBudget = 10_000;
    private const int HistoryMessageCharCap = 3_000;
    // The Agent SDK passes the whole system prompt as ONE claude CLI argument; Windows
    // caps the entire command line at ~32K chars and the spawn dies SILENTLY past it
    // (CLI never answers the initialize handshake). Per-section caps bound each part,
    // but nothing bounded the SUM — this is the hard ceiling, with headroom for the
    // other CLI args (model, cwd, allowed-tools).
    private const int SystemPromptHardCap = 24_000;
    private const int UserInstructionsCharCap = 8_000;
    internal const int TaskContextCharCap = 9_000;

    /// <summary>
    /// A project created from a base folder: where the developer's own checkouts are, and the
    /// rule that they are theirs. The workspace already holds clones of every repo; the base
    /// folder may hold uncommitted work and is never the agent's to change.
    /// </summary>
    internal static string BaseFolderSection(ProjectSummary? project)
    {
        if (project?.BaseFolder is not { Length: > 0 } baseFolder)
            return "";

        var sb = new StringBuilder();
        sb.Append("\n\n## The developer's base folder\n");
        sb.Append($"This project's repositories live on the developer's machine in `{baseFolder}`");
        sb.Append(project.BaseFolderShape == BaseFolderShape.MultiRepo
            ? " (one folder per repository). You may read it for reference"
            : " (the folder is the repository).");
        if (project.BaseFolderShape == BaseFolderShape.MultiRepo)
            sb.Append(" — it is the developer's own checkout and may hold uncommitted work, so prefer the clones in your working directory.");
        sb.Append(" Never edit, create or delete anything there, and never read `.env` files.\n");
        return sb.ToString();
    }
    // Recent history is the most valuable volatile context — always reserve room for it.
    private const int HistoryReserve = 4_000;

    // Internal for tests — injection order (persona vs runtime guardrails) is asserted directly.
    // toolsEnabled selects the tool-mode artifact blocks (the orkka MCP tools carry the
    // formats in their schemas, so these are far shorter — significant relief for the
    // command-line hard cap) over the legacy fenced-block instructions.
    internal static string BuildSystemPrompt(
        ProjectAgent agent, IReadOnlyList<MemoryItem> memories,
        IReadOnlyList<dynamic> history, List<RepoSummary> repos,
        string? contextSummary, string? locale,
        string? designAssetContext,
        string? projectName = null, bool toolsEnabled = false,
        BrandKitSummary? brandKit = null,
        IReadOnlyList<AgentSkill>? skills = null,
        ProjectSummary? project = null,
        string? taskContext = null)
    {
        var sb = new StringBuilder();
        sb.Append(agent.SystemPrompt);

        if (!string.IsNullOrWhiteSpace(agent.UserInstructions))
        {
            sb.Append("\n\n");
            // User-editable and unbounded in the DB — cap it so one giant paste can't
            // push the command line over the Windows limit by itself.
            sb.Append(Truncate(agent.UserInstructions, UserInstructionsCharCap));
        }

        // Injected at runtime (not via the DB-stored prompt) so existing and
        // customized PM agents can publish task breakdowns to the board
        if (agent.Role == AgentRole.PM)
            sb.Append(toolsEnabled ? DefaultPrompts.PMTaskCreationTools : DefaultPrompts.PMTaskCreation);

        // Injected at runtime so existing and customized Designer/GameDesigner agents
        // deliver SVG assets via design blocks — and never promise the raster images or
        // GIFs older stored personas still mention (there is no image generation).
        if (agent.Role is AgentRole.Designer or AgentRole.GameDesigner)
        {
            sb.Append(toolsEnabled
                ? DefaultPrompts.DesignerAssetCreationTools
                : DefaultPrompts.DesignerAssetCreation);

            // The project's decided palette. Without it the persona only says "read the
            // project's color scheme first", which asks the agent to rediscover a decision
            // already made — and it guesses differently every time it can't find the files.
            sb.Append(PublicApi.DesignGenerationApi.BrandKitBlock(brandKit));

            // The palette says what colours to use; this says to go and look at the artwork
            // the project already has before making more of it.
            sb.Append(DefaultPrompts.DesignerGrounding(toolsEnabled));
        }

        // Designer only (GameDesigner's scope is game assets, not app screens): every Designer
        // seeded before UX ownership shipped carries the opposite rule in its stored persona.
        // Appended after it so recency wins.
        if (agent.Role == AgentRole.Designer)
            sb.Append(DefaultPrompts.DesignerUxOwnership);

        // Injected at runtime so existing and customized Architect agents emit compact
        // diagram skeletons (expanded client-side) instead of full Excalidraw scenes,
        // overriding the full-scene format frozen in older seeded prompts
        if (agent.Role == AgentRole.SolutionArchitect)
            sb.Append(toolsEnabled
                ? DefaultPrompts.ArchitectDiagramCreationTools
                : DefaultPrompts.ArchitectDiagramCreation);

        // Injected at runtime for ALL agents — any agent can save conversation
        // outcomes to the project's Documents library
        sb.Append(toolsEnabled ? DefaultPrompts.DocumentCreationTools : DefaultPrompts.DocumentCreation);

        // Injected at runtime for ALL agents on tool turns — the agent can recount its own
        // past task work (list_my_tasks / get_task_history) instead of denying it has any
        if (toolsEnabled)
            sb.Append(DefaultPrompts.WorkHistoryTools);

        // Injected at runtime for ALL agents — clarifying questions render as
        // clickable option cards (parsed client-side, never persisted)
        sb.Append(DefaultPrompts.InteractiveQuestions);

        // Injected at runtime for ALL agents: custom-agent personas are user-authored, so
        // injection resistance can't live in the DB-stored prompt. The matching fences go
        // around user messages at the call sites — history below, current turn in
        // ChatTurnRunner.
        sb.Append("\n\n");
        sb.Append(PromptSafety.ChatNotice);

        // Anchor the agent to THIS project by name — without it, an agent whose workspace
        // is empty (or that carries context from earlier turns) has nothing telling it
        // which project it serves and can drift to another project it has seen before.
        if (!string.IsNullOrWhiteSpace(projectName))
        {
            sb.Append("\n\n## Current Project\n");
            sb.Append($"You are working in the project \"{projectName}\". Everything you produce ");
            sb.Append("— designs, documents, diagrams, plans — is for this project only. ");
            sb.Append("Never base your work on any other project or codebase, even if you have seen one before.\n");
        }

        // Repo context — the agent has file system access to these repos
        if (repos.Count > 0)
        {
            sb.Append("\n\n## Available Repositories\n");
            sb.Append("You have read access to the following repositories in your working directory. ");
            sb.Append("Use Read, Glob, and Grep tools to explore them.\n\n");
            foreach (var repo in repos)
            {
                sb.Append($"- **{repo.Name}/** — {repo.GitUrl} (branch: {repo.DefaultBranch ?? "main"})");
                if (!string.IsNullOrWhiteSpace(repo.Description))
                    sb.Append($" — {repo.Description}");
                sb.Append('\n');
            }
        }

        sb.Append(BaseFolderSection(project));

        // Assigned skills (project playbooks) — stable per conversation, so they sit in the
        // cacheable prefix with the other agent configuration.
        if (skills is { Count: > 0 })
            sb.Append(Skills.SkillInvocation.PromptSection(skills));

        // A task's Discuss chat: the task, its ticket, plan, questions, thread and diff — built
        // (and fenced) by TaskChatContextBuilder. Capped so memories and history still fit.
        if (!string.IsNullOrWhiteSpace(taskContext))
        {
            sb.Append("\n\n");
            sb.Append(taskContext.Length <= TaskContextCharCap
                ? taskContext
                : taskContext[..TaskContextCharCap] + "\n[... task context truncated]");
        }

        // Section order matters for the CLI's server-side prompt caching: everything up to
        // here is stable across turns of the same conversation; the sections below change
        // per turn (history every turn, summary when the watermark advances, memories after
        // executions). Volatile content must stay LAST so it doesn't invalidate the cached
        // prefix for the stable sections.

        // Assets referenced via iterate markers in THIS message — volatile by nature
        if (designAssetContext is not null)
            sb.Append(designAssetContext);

        // Project memory — retrieved by relevance (MemoryRetriever), rendered by the shared
        // section so chat and pipeline runs read the same block. Budgeted so the language,
        // summary and (reserved) history sections below still fit under the ceiling. With
        // tools on, the block also tells the agent how to add to the memory (remember/recall).
        if (memories.Count > 0 || toolsEnabled)
        {
            var memoriesLimit = Math.Max(600, Math.Min(4_000, SystemPromptHardCap - HistoryReserve - 2_500 - sb.Length));
            sb.Append('\n');
            sb.Append(MemoryPromptSection.Render(memories, memoriesLimit, includeToolHint: toolsEnabled));
        }

        // Injected at runtime — seeded agent prompts are frozen in the DB
        sb.Append(AgentLanguage.Instruction(locale));

        // Rolling summary of messages older than the recent window (built lazily after
        // responses once the conversation outgrows the window)
        if (!string.IsNullOrWhiteSpace(contextSummary))
        {
            sb.Append("\n\n## Earlier Conversation (summarized)\n");
            sb.Append(contextSummary);
            sb.Append('\n');
        }

        // Conversation history — budgeted, newest messages win. The Agent SDK passes the
        // whole system prompt as a claude.exe command-line argument, and Windows caps the
        // command line at ~32K chars (CreateProcess error 206), so the one unbounded
        // section must not grow with conversation length.
        if (history.Count > 0)
        {
            // Fit within whatever the hard cap has left, never more than the standing budget.
            var historyBudget = Math.Min(HistoryCharBudget, SystemPromptHardCap - sb.Length - 200);
            if (historyBudget > 500)
            {
                var entries = new List<string>();
                var used = 0;
                for (var i = history.Count - 1; i >= 0; i--)
                {
                    var msg = history[i];
                    var isUser = (string)msg.Role == "user";
                    var role = isUser ? "User" : "Assistant";
                    var content = (string)msg.Content;
                    if (content.Length > HistoryMessageCharCap)
                        content = content[..HistoryMessageCharCap] + "\n[... truncated]";
                    // Fence human-authored content (see ChatNotice above); the agent's own
                    // prior replies are not untrusted input.
                    if (isUser)
                        content = PromptSafety.Wrap("user-message", content);

                    var entry = $"\n**{role}:**\n{content}\n";
                    if (used + entry.Length > historyBudget) break;
                    used += entry.Length;
                    entries.Add(entry);
                }
                entries.Reverse();

                sb.Append("\n\n## Conversation History\n");
                if (entries.Count < history.Count)
                    sb.Append($"(showing the most recent {entries.Count} of {history.Count} messages)\n");
                foreach (var entry in entries)
                    sb.Append(entry);
            }
        }

        var prompt = sb.ToString();
        // Emergency floor — should be unreachable given the budgets above, but a silent
        // spawn death is the worst possible failure mode, so never return past the cap.
        return prompt.Length <= SystemPromptHardCap
            ? prompt
            : prompt[..SystemPromptHardCap] + "\n[…context truncated to fit the OS command-line limit]";
    }
}
