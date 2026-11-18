namespace Orkka.Modules.Agents.Features.AgentChat;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Domain;
using Orkka.Modules.Agents.Infrastructure;
using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Ai;

/// <summary>Everything a chat turn needs beyond the message itself.</summary>
/// <param name="ExcludeMessageId">The just-persisted user message (passed to the agent as the
/// turn input, so it must not also appear in the replayed history). Null for turns whose
/// input isn't persisted as a user message (scheduled playbooks, reminders).</param>
public sealed record ChatTurnInput(
    Ulid OrgId,
    string RequesterUserId,
    string? RequesterLocale,
    Ulid ProjectId,
    ProjectAgent Agent,
    AgentConversation Conversation,
    string Content,
    Ulid? ExcludeMessageId,
    string? DesignAssetContext = null,
    string? ContextBlock = null,
    string? InvokedSkillBlock = null,
    string? DesignFolderPath = null,
    TaskChatTurn? Task = null);

/// <summary>What makes a turn a task's Discuss turn: the task, its context block for the system
/// prompt, and the branch each repo's working copy should be on (the task branch for the
/// task's own repo). The workspace is the task's own, read-only, never its running worktree.</summary>
public sealed record TaskChatTurn(
    Ulid TaskId,
    string ContextBlock,
    IReadOnlyDictionary<Ulid, string> RepoBranches);

/// <summary>
/// Turns a message into a runnable <see cref="ChatTurnSpec"/>: loads memories, skills and
/// history, refreshes the repos and the chat workspace, decides session resume and tool
/// availability, and builds both prompt variants. Shared by the chat endpoint (interactive
/// turns) and the schedule processor (playbooks and reminders), so a scheduled turn is
/// prepared exactly like a typed one.
///
/// Side effect: clears the conversation's stored CLI session when it can't be resumed for
/// this turn (the caller's SaveChanges persists it, alongside its own marker writes).
/// </summary>
public sealed class ChatTurnPreparer(
    AgentsDbContext db,
    IChatAgentInvoker chatInvoker,
    IConfiguration configuration,
    IProjectsApi projectsApi,
    IDesignGenerationApi designGeneration,
    ILogger<ChatTurnPreparer> logger,
    Orkka.Modules.Runners.Contracts.IRunnerFleet? runnerFleet = null)
{
    // Comfortably below the ~32K Windows command-line limit, leaving room for the
    // claude.exe path and the SDK's other CLI arguments.
    private const int SystemPromptWarnLength = 26_000;

    public async Task<ChatTurnSpec> PrepareAsync(ChatTurnInput input, CancellationToken ct)
    {
        var (orgId, requesterUserId, locale, projectId, agent, conversation, content, excludeMessageId,
            designAssetContext, contextBlock, invokedSkillBlock, designFolderPath, taskTurn) = input;
        var agentId = agent.Id;
        var conversationId = conversation.Id;

        // Project memory, retrieved by relevance to this message (team + this agent's own).
        var memories = await new Infrastructure.Services.MemoryRetriever(db).RetrieveAsync(
            new MemoryQuery(orgId, projectId, agentId, content, Take: 20), ct);

        // Assigned skills ride in the system prompt.
        var assignedSkills = await ListAssignedSkillsAsync(orgId, projectId, agent, ct);

        // Load conversation history (exclude the just-saved message — it's passed as userMessage
        // to the agent). Messages already folded into the conversation's rolling summary are
        // skipped; the summary itself is injected into the system prompt instead.
        var history = await db.AgentMessages
            .Where(m => m.OrgId == orgId && m.ConversationId == conversationId
                        && (excludeMessageId == null || m.Id != excludeMessageId))
            .OrderBy(m => m.CreatedAt)
            .Skip(conversation.SummarizedMessageCount)
            .Select(m => new { m.Role, m.Content })
            .ToListAsync(ct);

        // Ensure chat workspace with all project repos
        var projectSummary = await projectsApi.GetProjectSummaryAsync(projectId, ct);

        // Agents speak the project's language. The caller's UI locale only decides for
        // projects that predate the language setting (ProjectLanguage.Resolve falls back
        // to it) — the navbar switch is for UI text, not for what the agents write.
        locale = ProjectLanguage.Resolve(projectSummary, locale);
        var repos = (await projectsApi.ListReposAsync(projectId, ct))
            .Where(r => r.OrgId == orgId)
            .ToList();

        // If agent is repo-scoped, filter to its assigned repos (empty scope = all)
        if (agent.RepoIds.Count > 0)
            repos = repos.Where(r => agent.RepoIds.Contains(r.Id.ToGuid())).ToList();

        // Every repo dropped anywhere below, with its reason — the turn tells the user about
        // them once it has them all. Leaving them out of the prompt is right; leaving them out
        // in silence is what left a user asking about their own code, being told it could not
        // be found, and having the actual reason only in a pod log.
        var dropped = new List<UnavailableChatRepo>();

        // Clone whatever has never been cloned, refresh the rest. Selecting on WorkspaceStatus
        // instead would drop every freshly attached repo forever: a repo is created Pending and
        // only this call promotes it, so an agent whose project was wired up entirely through
        // "add repo" would chat on with an empty workspace and no way to say why. A repo that
        // cannot be synced is left out of the turn rather than named in the prompt as something
        // the agent can read.
        var synced = new List<RepoSummary>(repos.Count);
        foreach (var repo in repos)
        {
            var syncError = await projectsApi.EnsureRepoWorkspaceAsync(
                repo.Id, "work on the project's code", ct);

            if (syncError is null)
            {
                synced.Add(repo);
                continue;
            }

            logger.LogWarning(
                "Chat workspace for conversation {ConversationId} excludes repo {RepoName} ({RepoId}): {Error}",
                conversationId, repo.Name, repo.Id, syncError);
            dropped.Add(new UnavailableChatRepo(
                repo.Id, repo.Name, RepoAccessDiagnostics.Classify(syncError), syncError));
        }
        repos = synced;

        // Hybrid routing reads the requester from this ambient (the invoker surface
        // carries no identity) — workspace prep must target the same runner as the run.
        ChatRequesterContext.Set(orgId, requesterUserId);

        var prepared = await chatInvoker.EnsureChatWorkspaceAsync(
            orgId,
            projectId,
            repos.Select(r => new ChatWorkspaceRepo(
                r.Id, r.Name, r.DefaultBranch, r.GitUrl,
                taskTurn?.RepoBranches.GetValueOrDefault(r.Id))).ToList(),
            taskTurn?.TaskId,
            ct);
        var workspacePath = prepared.Token;

        // The sync above ran against THIS host's disk; the workspace may have been built on
        // another machine (a runner-routed turn) where a repo could not be materialized.
        // Drop those too — the prompt below must describe the folder the agent actually gets.
        if (prepared.Unavailable.Count > 0)
        {
            foreach (var missing in prepared.Unavailable)
                logger.LogWarning(
                    "Chat workspace for conversation {ConversationId} excludes repo {RepoName} ({RepoId}): "
                    + "not materialized on the machine running the turn ({Reason})",
                    conversationId, missing.Name, missing.Id, missing.Reason);

            dropped.AddRange(prepared.Unavailable);
            var unavailableIds = prepared.UnavailableRepoIds;
            repos = repos.Where(r => !unavailableIds.Contains(r.Id)).ToList();
        }

        var workspaceNotice = RepoAccessDiagnostics.BuildNotice(dropped);

        // Resume the conversation's stored CLI session when one exists for THIS provider
        // and the backend honors resume for it (claude always; codex behind the runner's
        // cli-resume capability). After a mid-conversation provider switch the session is
        // useless — the new provider's CLI knows nothing about it and needs full context.
        var provider = ModelProviders.FromModelId(agent.Model);
        // Pre-SessionProvider rows only ever stored claude sessions.
        var storedProvider = conversation.SessionProvider ?? nameof(AiProvider.Anthropic);
        var resumeSessionId =
            chatInvoker.SupportsSessionResume(provider) && storedProvider == provider.ToString()
                ? conversation.ClaudeSessionId
                : null;

        // A turn that can't resume the stored session makes it stale (its transcript won't
        // contain this exchange) — drop it so a later switch back rebuilds full context
        // from history instead of resuming with a gap. Persisted by the caller's save.
        if (resumeSessionId is null)
        {
            conversation.ClaudeSessionId = null;
            conversation.SessionProvider = null;
        }

        // Platform tools (create documents/tasks/diagrams/designs as MCP tool calls).
        // Default ON — structured tool calls beat scraping fenced blocks out of prose, which
        // is a protocol with an output ceiling: a 26-document consolidation hit it mid-block
        // on 2026-09-07 and saved nothing. Which route can carry them differs (see below),
        // and the answer decides the PROMPT, so it must be settled before the turn is built.
        var toolsEnabled = configuration.GetValue("AgentChat:Tools:Enabled", true)
            && provider == AiProvider.Anthropic
            && await ToolsAvailableOnRouteAsync(orgId, requesterUserId, ct);

        // The full history-injected prompt is always built: it IS the prompt for fresh
        // sessions, and the fallback when resuming fails. With a resumable session the CLI
        // holds the conversation transcript itself — the resume variant skips re-injecting
        // history and the rolling summary, which also shrinks the prompt.
        // Only the design roles use it, so only they pay the query.
        var brandKit = agent.Role is AgentRole.Designer or AgentRole.GameDesigner
            ? await designGeneration.GetBrandKitAsync(orgId, agent.ProjectId, ct)
            : null;

        var freshPrompt = SendMessageEndpoint.BuildSystemPrompt(
            agent, memories, history, repos, conversation.ContextSummary, locale,
            designAssetContext, projectSummary?.Name, toolsEnabled, brandKit,
            assignedSkills, projectSummary, taskTurn?.ContextBlock);
        var systemPrompt = resumeSessionId is null
            ? freshPrompt
            : SendMessageEndpoint.BuildSystemPrompt(
                agent, memories, [], repos, null, locale,
                designAssetContext, projectSummary?.Name, toolsEnabled, brandKit,
                assignedSkills, projectSummary, taskTurn?.ContextBlock);
        if (systemPrompt.Length > SystemPromptWarnLength)
            logger.LogWarning(
                "Agent chat system prompt is {Length} chars for agent {AgentId} — approaching the " +
                "Windows command-line limit; check agent instructions/memories size",
                systemPrompt.Length, agentId);

        // Measurement side of the token-overhead work: per-turn size and where it comes
        // from. The fresh−resume delta is exactly what session resume saves this turn.
        logger.LogDebug(
            "[PromptSize] chat {ConversationId}: sent={SentChars} fresh={FreshChars} resume={IsResume} " +
            "persona={PersonaChars} instructions={InstructionChars} summary={SummaryChars} " +
            "history={HistoryCount}msgs memories={MemoryCount}",
            conversationId, systemPrompt.Length, freshPrompt.Length, resumeSessionId is not null,
            agent.SystemPrompt.Length, agent.UserInstructions?.Length ?? 0,
            conversation.ContextSummary?.Length ?? 0, history.Count, memories.Count);

        return new ChatTurnSpec(
            orgId,
            requesterUserId,
            locale,
            projectId,
            agentId,
            conversationId,
            agent.Role,
            agent.Name,
            agent.Model,
            workspacePath,
            content,
            systemPrompt,
            freshPrompt,
            resumeSessionId,
            ConversationHasTitle: !string.IsNullOrWhiteSpace(conversation.Title),
            ContextBlock: contextBlock,
            ToolsEnabled: toolsEnabled,
            InvokedSkillBlock: invokedSkillBlock,
            WorkspaceNotice: workspaceNotice,
            DesignFolderPath: designFolderPath,
            TaskId: taskTurn?.TaskId);
    }

    /// <summary>The project's enabled skills — for <c>/slug</c> invocation parsing.</summary>
    public Task<List<AgentSkill>> ListEnabledProjectSkillsAsync(Ulid orgId, Ulid projectId, CancellationToken ct)
        => db.AgentSkills.AsNoTracking()
            .Where(s => s.OrgId == orgId && s.ProjectId == projectId && s.IsEnabled)
            .OrderBy(s => s.Name)
            .ToListAsync(ct);

    private async Task<List<AgentSkill>> ListAssignedSkillsAsync(
        Ulid orgId, Ulid projectId, ProjectAgent agent, CancellationToken ct)
    {
        if (agent.SkillIds.Count == 0) return [];
        var all = await ListEnabledProjectSkillsAsync(orgId, projectId, ct);
        return all.Where(s => agent.SkillIds.Contains(s.Id.ToGuid())).ToList();
    }

    /// <summary>
    /// Whether this turn can carry the orkka tool server. Every hosted turn is relayed to the
    /// requester's own runner, so the answer belongs to that machine: it must advertise
    /// <c>chat-tools</c>, meaning it forwards each call back over the hub to a server the
    /// Worker rebuilds. An older runner falls back to the fenced-block prompt (an offline one
    /// fails the turn anyway). Presence is the source, not the runner link: the API pod holds
    /// no link — the Worker does — and this has to be answered before the prompt is built.
    /// </summary>
    private async Task<bool> ToolsAvailableOnRouteAsync(Ulid orgId, string userId, CancellationToken ct)
    {
        // An invoker that runs turns in this process answers for itself (test hosts).
        if (chatInvoker.SupportsChatTools)
            return true;
        if (runnerFleet is null)
            return false;

        var presence = await runnerFleet.GetPresenceAsync(orgId, userId, ct);
        var capable = presence is { Online: true }
            && Orkka.Modules.Runners.Contracts.RunnerPresenceRules.HasCapability(
                presence.CapabilitiesJson, LocalRunner.Contracts.RunnerCapabilities.ChatTools);

        if (!capable && presence is { Online: true })
            logger.LogInformation(
                "Chat tools are off for {User}: the connected runner on {Machine} does not forward chat tool calls — updating Orkka (`git pull`, then `./orkka start`) enables them",
                userId, presence.MachineName);

        return capable;
    }
}
