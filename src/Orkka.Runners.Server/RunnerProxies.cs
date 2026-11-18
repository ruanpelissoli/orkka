namespace Orkka.Runners.Server;

using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Execution.Agents;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.SharedKernel.Ai;
using Orkka.SharedKernel.Utilities;

/// <summary>Streams agent runs through the connected runner. The runner runs each vendor CLI
/// on the developer's own sign-in; no provider credential travels with the request.</summary>
public sealed class RunnerAgentInvoker(
    IRunnerLink runner,
    ILogger<RunnerAgentInvoker> logger,
    ProjectToolRegistry? toolRegistry = null) : IAiAgentInvoker
{
    // An old runner ignores ResumeSessionId and would run a slimmed prompt cold —
    // only report resume support when the connected runner advertises it.
    public bool SupportsSessionResume =>
        runner.Status.Hello?.Capabilities?.Contains(RunnerCapabilities.AgentResume) == true;

    // The tool server stays a host-side object; a capable runner forwards each call back
    // over CallProjectTool, which the hub answers from the registry below. Claiming support
    // without both pieces would put tool names in the prompt that no run can call.
    public bool SupportsInProcessToolServer =>
        toolRegistry is not null
        && runner.Status.Hello?.Capabilities?.Contains(RunnerCapabilities.ProjectTools) == true;

    // CancelTaskRuns is request/response and the runner waits for the cancelled agent
    // processes to exit before answering, so this budget is "kill + teardown", not a
    // generation: well under the 5-minute default, well over the runner's own 30s wait.
    private static readonly TimeSpan CancelTaskRunsTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Cancels every run of the task still in flight on the runner — by TASK id, because the
    /// run id was minted by whichever replica started the stream and may have died with it.
    /// An offline runner surfaces as <see cref="RunnerUnavailableException"/> (from the link)
    /// so the caller can keep its work pending; a connected runner that predates the
    /// capability is logged and skipped, since it cannot be asked either way.
    /// </summary>
    public async Task CancelTaskRunsAsync(Ulid taskId, CancellationToken ct = default)
    {
        var (connected, hello) = runner.Status;
        if (connected && hello?.Capabilities?.Contains(RunnerCapabilities.TaskCancel) != true)
        {
            logger.LogWarning(
                "[RunnerProxy] Runner on {Machine} predates task-cancel — cannot stop runs of task {TaskId} it may still be executing",
                hello?.MachineName ?? "?", taskId);
            return;
        }

        // Not connected: InvokeAsync throws RunnerUnavailableException — deliberately
        // propagated, see the summary.
        var result = await runner.InvokeAsync<RunnerOpResult>(
            RunnerProtocol.CancelTaskRuns,
            [new CancelTaskRunsRequest(taskId.ToString())],
            ct,
            CancelTaskRunsTimeout);

        if (!result.Success)
            throw new InvalidOperationException(
                $"The runner could not cancel runs of task {taskId}: {result.Error}");

        if (int.TryParse(result.Value, out var cancelled) && cancelled > 0)
            logger.LogInformation(
                "[RunnerProxy] Cancelled {Count} in-flight run(s) of task {TaskId} on the runner",
                cancelled, taskId);
    }

    public async IAsyncEnumerable<AgentChunk> InvokeAsync(
        AgentRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Pre-flight against the runner's last Hello: fail fast with an actionable reason
        // instead of a cryptic CLI auth failure. FailExecutionAsync persists this as the
        // blocked-task reason.
        if (runner.GetProviderBlocker(request.Provider) is { } blocker)
        {
            logger.LogWarning("[RunnerProxy] Agent run refused — {Blocker}", blocker);
            yield return new AgentChunk(AgentChunkType.Error, blocker);
            yield return new AgentChunk(AgentChunkType.Done, "");
            yield break;
        }

        var projectTools = await SerializeProjectToolsAsync(request, ct);

        var runId = Ulid.NewUlid().ToString();
        var wire = new RunnerAgentRunRequest(
            runId,
            request.SystemPrompt,
            request.UserMessage,
            request.WorktreePath,
            request.Provider.ToString(),
            request.Model,
            request.MaxTokens,
            request.MaxTurns,
            request.Phase.ToString(),
            request.AgentName,
            request.AgentRole,
            request.OrgId.ToString(),
            request.ProjectId.ToString(),
            request.TaskId?.ToString(),
            request.AgentId?.ToString(),
            request.ExecutionId?.ToString(),
            request.ResumeSessionId,
            request.MaxToolCalls,
            request.MaxWallClockSeconds,
            request.Interactive,
            projectTools,
            request.ApiTesting,
            request.TicketConnectionKey is { Length: > 0 } ticketConnection && request.TicketKey is { Length: > 0 } ticketKey
                ? new TrackerTicketRef(ticketConnection, ticketKey)
                : null,
            request.ReadOnlyDirs is { Count: > 0 } readOnlyDirs ? [.. readOnlyDirs] : null);

        // Registered for exactly the life of the stream: CallProjectTool answers only while
        // the run that was handed these tools is actually running.
        var registeredTaskId = projectTools is not null ? request.TaskId?.ToString() : null;
        if (registeredTaskId is not null)
            toolRegistry!.Register(registeredTaskId, request.OrgId, request.ToolServer!);

        try
        {
            runner.OpenRunChannel(runId);
            string? startError = null;
            try
            {
                await runner.SendAsync(RunnerProtocol.StartAgentRun, wire, ct);
            }
            catch (Exception ex)
            {
                runner.CloseRunChannel(runId);
                logger.LogWarning(ex, "[RunnerProxy] Could not start agent run");
                startError = ex is RunnerUnavailableException
                    ? ex.Message
                    : $"Failed to reach the runner: {ex.Message}";
            }

            if (startError is not null)
            {
                yield return new AgentChunk(AgentChunkType.Error, startError);
                yield return new AgentChunk(AgentChunkType.Done, "");
                yield break;
            }

            await foreach (var chunk in runner.ConsumeRunAsync(runId, ct))
                yield return ToAgentChunk(chunk);
        }
        finally
        {
            if (registeredTaskId is not null)
                toolRegistry!.Unregister(registeredTaskId);
        }
    }

    /// <summary>The wire form of this run's tool server, or null when the run carries none,
    /// the runner can't forward calls, or listing failed (a run without context tools beats
    /// no run). Definitions come from the server itself so runner and host cannot drift.</summary>
    private async Task<RunnerToolDefinition[]?> SerializeProjectToolsAsync(
        AgentRequest request, CancellationToken ct)
    {
        if (request.ToolServer is not { } server
            || request.TaskId is null
            || !SupportsInProcessToolServer)
            return null;

        try
        {
            var tools = await server.ListToolsAsync(ct);
            return tools.Count == 0
                ? null
                : [.. tools.Select(t => new RunnerToolDefinition(
                    t.Name, t.Description ?? "", t.InputSchema.GetRawText()))];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[RunnerProxy] Could not list project tools — run proceeds without them");
            return null;
        }
    }

    internal static AgentChunk ToAgentChunk(RunnerChunk chunk) => new(
        Enum.TryParse<AgentChunkType>(chunk.Type, out var type) ? type : AgentChunkType.Text,
        chunk.Content,
        chunk.PromptTokens,
        chunk.CompletionTokens,
        chunk.CacheReadTokens,
        chunk.CacheWriteTokens,
        chunk.SessionId,
        chunk.VendorCostUsd);
}

/// <summary>Agent chat via the runner: workspace prep + read-only streamed runs.
/// <paramref name="ensureBareClones"/> makes the runner clone the repos first when it may never
/// have seen them; a caller that already synced them through the runner leaves it off, since
/// repeating it would cost a second fetch per repo per turn. Clones use the developer's own git
/// credentials on their machine.</summary>
public sealed class RunnerChatInvoker(
    IRunnerLink runner,
    ILogger<RunnerChatInvoker> logger,
    bool ensureBareClones = false,
    BareCloneFailureCache? cloneFailures = null,
    ProjectToolRegistry? toolRegistry = null,
    Ulid orgId = default,
    // Folders the chat agent may read besides its workspace (a folder-of-repos project's base
    // folder), resolved by the API host for the turn's project.
    string[]? readOnlyDirs = null) : IChatAgentInvoker
{
    // Platform tools on a relayed chat turn, by exactly the route pipeline runs use: the
    // server stays here (it answers from the platform's database), its definitions travel
    // with the run, and the runner forwards each call back over CallProjectTool. Both halves
    // are required — claiming support without the registry would advertise tools to the
    // model that nothing can answer.
    public bool SupportsChatTools =>
        toolRegistry is not null
        && runner.Status.Hello?.Capabilities?.Contains(RunnerCapabilities.ChatTools) == true;
    // Shared by default: the hosted relay news up an invoker per request, so a per-instance
    // cache would forget the failure before the next turn asked about it.
    private readonly BareCloneFailureCache _cloneFailures = cloneFailures ?? BareCloneFailureCache.Shared;

    // Claude resume has flowed over RunnerChatRunRequest since v0.2; codex resume needs
    // runner-side invoker support (cli-resume capability). Ollama rides the same codex
    // `exec resume` machinery, and any runner that reports the ollama provider also has
    // cli-resume. Copilot, Grok and OpenCode stay off the list until their resume flags are
    // verified against the real CLI. All three invokers already build those flags
    // (`--resume`, `--session`), so enabling one is a single line here — but only after
    // someone confirms a resumed run really does reload its own transcript. A resume that
    // silently starts cold drops the whole conversation and reads as an agent with amnesia,
    // which is worse than not offering resume at all.
    public bool SupportsSessionResume(AiProvider provider) => provider switch
    {
        AiProvider.Anthropic => true,
        AiProvider.OpenAI or AiProvider.Ollama =>
            runner.Status.Hello?.Capabilities?.Contains(RunnerCapabilities.CliResume) == true,
        _ => false,
    };

    public async Task<ChatWorkspacePreparation> EnsureChatWorkspaceAsync(
        Ulid orgId, Ulid projectId, IReadOnlyList<ChatWorkspaceRepo> repos,
        Ulid? taskId = null, CancellationToken ct = default)
    {
        // The bare clones have to exist on the RUNNER, and the caller's sync pass ran on
        // the host's disk — a different machine entirely on the hosted route. Without this
        // the runner finds no bare clone, silently skips the repo, and the agent chats in
        // an empty folder while the prompt promises it the whole codebase. Repos land here
        // only when a task has happened to clone them, which is why chat in a fresh project
        // saw nothing at all.
        //
        // Exactly as task runs do it: the clone runs on the user's machine with their own git
        // credentials.
        var available = new List<ChatWorkspaceRepo>(repos.Count);
        var unavailable = new List<UnavailableChatRepo>();

        foreach (var repo in repos)
        {
            if (!ensureBareClones || string.IsNullOrWhiteSpace(repo.GitUrl))
            {
                available.Add(repo);
                continue;
            }

            // A clone that just failed for a reason the user has to fix — no credential
            // helper on their machine, a repo that isn't there — will fail identically on
            // this turn. Replaying the remembered failure keeps the repo correctly excluded
            // (and correctly reported to the user) without paying for the attempt.
            if (_cloneFailures.TryGet(orgId, repo.Id) is { } cached)
            {
                logger.LogDebug(
                    "[RunnerProxy] Chat workspace excludes repo {Repo} ({RepoId}) — {Reason}, remembered from a recent attempt",
                    repo.Name, repo.Id, cached.Reason);
                unavailable.Add(new UnavailableChatRepo(
                    repo.Id, repo.Name, cached.Reason, cached.Error, OnUserMachine: true));
                continue;
            }

            var clone = await SafeInvokeAsync(
                RunnerProtocol.EnsureBareClone,
                new EnsureBareCloneRequest(orgId.ToString(), repo.Id.ToString(), repo.GitUrl),
                ct);

            if (clone.Success)
            {
                // Whatever was wrong is over — a later failure should cost a real attempt.
                _cloneFailures.Clear(orgId, repo.Id);
                available.Add(repo);
                continue;
            }

            var reason = RepoAccessDiagnostics.Classify(clone.Error);
            _cloneFailures.Record(orgId, repo.Id, reason, clone.Error);

            logger.LogWarning(
                "[RunnerProxy] Chat workspace excludes repo {Repo} ({RepoId}) — {Reason}: the runner could not clone {GitUrl}: {Error}",
                repo.Name, repo.Id, reason, repo.GitUrl, clone.Error);
            // OnUserMachine: this proxy IS the user's machine — the clone ran there under
            // their git credentials, so they are the one who can fix it.
            unavailable.Add(new UnavailableChatRepo(
                repo.Id, repo.Name, reason, clone.Error, OnUserMachine: true));
        }

        // Ulid.Empty means "no project" (relayed from a pre-project-scoping host) — send
        // null so the runner keeps its legacy org-level workspace for that request.
        var projectIdWire = projectId == Ulid.Empty ? null : projectId.ToString();
        // A task's Discuss chat: its own workspace, each repo on its requested branch. A task
        // needs a project — without one the runner keeps the legacy layout and no branch.
        var taskIdWire = taskId is { } task && projectIdWire is not null ? task.ToString() : null;
        var request = new EnsureChatWorkspaceRequest(
            orgId.ToString(),
            available.Select(r => new ChatWorkspaceRepoDto(
                r.Id.ToString(), r.Name, r.DefaultBranch, taskIdWire is null ? null : r.Branch)).ToArray(),
            projectIdWire,
            taskIdWire);

        var result = await runner.InvokeAsync<RunnerOpResult>(
            RunnerProtocol.EnsureChatWorkspace, [request], ct);

        // Older runner binaries ignore ProjectId and answer with the legacy org token —
        // pass whatever the runner returned straight back on the chat run so the token
        // always matches the workspace the runner actually prepared.
        return result.Success
            ? new ChatWorkspacePreparation(
                result.Value ?? RunnerTokens.ChatWorkspace(orgId.ToString(), projectIdWire, taskIdWire),
                unavailable)
            : throw new InvalidOperationException(result.Error ?? "Chat workspace preparation failed on the runner.");
    }

    /// <summary>A clone that fails must cost this repo its place in the workspace, not the
    /// whole turn — a dropped link or one unreachable remote still leaves the other repos
    /// worth chatting about.</summary>
    private async Task<RunnerOpResult> SafeInvokeAsync(string op, object request, CancellationToken ct)
    {
        try
        {
            return await runner.InvokeAsync<RunnerOpResult>(op, [request], ct, RunnerOpBudgets.For(op));
        }
        catch (RunnerUnavailableException ex)
        {
            return RunnerOpResult.Fail(ex.Message);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // An op that outran its budget surfaces as a bare "Invocation canceled by the
            // server" — before this it escaped as an unhandled exception and turned the whole
            // chat turn into a 500 with nothing in the conversation. One repo that could not be
            // prepared in time is one repo the agent goes without, reported by name.
            return RunnerOpResult.Fail(
                $"{op} did not finish on your runner within its time budget ({ex.Message}). "
                + "A first clone of a large repository keeps running in the background — send again in a few minutes.");
        }
    }

    // conversationId (the warm-process pool key) is accepted but unused: runner runs are
    // one-shot StartChatRun ops today. Moving the WarmChatSessionPool onto the runner needs
    // a "follow-up to conversation X" protocol op — until then Orkka Local keeps the
    // spawn-per-turn --resume path.
    public async IAsyncEnumerable<ChatChunk> InvokeAgentAsync(
        string workspacePath, string systemPrompt, string userMessage, string model,
        string? resumeSessionId = null, Ulid? conversationId = null,
        IChatToolServer? toolServer = null,
        // Unused: the caller (the Worker's relay) built the server and hands it in directly.
        ChatToolServerContext? toolContext = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Chat routes per-provider too: chatting with a Gemini-configured agent runs gemini.
        var provider = ModelProviders.FromModelId(model);
        if (runner.GetProviderBlocker(provider) is { } blocker)
        {
            logger.LogWarning("[RunnerProxy] Chat run refused — {Blocker}", blocker);
            yield return new ChatChunk(ChatChunkType.Error, blocker);
            yield return new ChatChunk(ChatChunkType.Done, "");
            yield break;
        }

        var runId = Ulid.NewUlid().ToString();

        // The tools this turn may call, and the registration that answers them. Registered
        // BEFORE the run starts and removed when its stream ends — a tool call can only be
        // answered while the run that was handed the definitions is the one asking.
        var chatTools = SerializeChatTools(toolServer);
        if (chatTools is not null)
            toolRegistry!.Register(runId, orgId, new ChatToolServerMcpAdapter(toolServer!));

        var wire = new RunnerChatRunRequest(
            runId, workspacePath, systemPrompt, userMessage, provider.ToString(), model,
            resumeSessionId, chatTools, readOnlyDirs is { Length: > 0 } ? readOnlyDirs : null);

        runner.OpenRunChannel(runId);
        string? startError = null;
        try
        {
            await runner.SendAsync(RunnerProtocol.StartChatRun, wire, ct);
        }
        catch (Exception ex)
        {
            runner.CloseRunChannel(runId);
            logger.LogWarning(ex, "[RunnerProxy] Could not start chat run");
            startError = ex is RunnerUnavailableException
                ? ex.Message
                : $"Failed to reach the runner: {ex.Message}";
        }

        if (startError is not null)
        {
            if (chatTools is not null)
                toolRegistry!.Unregister(runId);
            yield return new ChatChunk(ChatChunkType.Error, startError);
            yield return new ChatChunk(ChatChunkType.Done, "");
            yield break;
        }

        try
        {
            await foreach (var chunk in runner.ConsumeRunAsync(runId, ct))
            {
                yield return new ChatChunk(
                    Enum.TryParse<ChatChunkType>(chunk.Type, out var type) ? type : ChatChunkType.Text,
                    chunk.Content,
                    chunk.PromptTokens,
                    chunk.CompletionTokens,
                    chunk.CacheReadTokens,
                    chunk.CacheWriteTokens,
                    chunk.SessionId,
                    chunk.VendorCostUsd,
                    // Every chunk this proxy yields came off a runner link: the run happened
                    // on the user's machine under their own subscription, so the turn's
                    // tokens are recorded at $0.
                    ServedByRunner: true);
            }
        }
        finally
        {
            if (chatTools is not null)
                toolRegistry!.Unregister(runId);
        }
    }

    /// <summary>This turn's tools on the wire, or null when the turn carries none or the
    /// connected runner cannot forward calls. Definitions come from the server itself, so
    /// what the model is offered and what the host can answer cannot drift.</summary>
    private RunnerToolDefinition[]? SerializeChatTools(IChatToolServer? toolServer)
    {
        if (toolServer is null || !SupportsChatTools)
            return null;

        try
        {
            var tools = toolServer.ListTools();
            return tools.Count == 0
                ? null
                : [.. tools.Select(t => new RunnerToolDefinition(
                    t.Name, t.Description, t.InputSchemaJson, t.TimeoutSeconds))];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[RunnerProxy] Could not list chat tools — the turn runs without them");
            return null;
        }
    }
}

/// <summary>Utility AI (task decomposition, dependency analysis, titles, summaries) as
/// one-shot headless CLI runs on the runner. Claude when connected (byte-identical to
/// the original behavior); otherwise the first connected provider with a tier-mapped
/// model — so a codex-only or gemini-only setup still gets titles, summaries, and task
/// decomposition.</summary>
public sealed class RunnerAnthropicClient(
    IRunnerLink runner, IProviderReadiness readiness,
    IModelCatalog? modelCatalog = null)
{
    public async Task<AnthropicResponse> CreateMessageAsync(
        AnthropicRequest request, CancellationToken ct = default)
    {
        var userMessage = string.Join("\n\n", request.Messages
            .Where(m => m.Role == "user")
            .SelectMany(m => m.Content)
            .OfType<AnthropicTextContent>()
            .Select(c => c.Text));

        var provider = AiProvider.Anthropic;
        var model = request.Model;
        if (readiness.GetBlocker(AiProvider.Anthropic) is not null)
        {
            var connectedProviders = readiness.Connected;
            if (connectedProviders.Count == 0)
                throw new InvalidOperationException(
                    readiness.GetAnyProviderBlocker() ?? "No AI provider is connected.");
            provider = connectedProviders[0];
            // Requested claude models map by tier: haiku-class work goes to the
            // provider's fast model, everything else to its standard utility model.
            // A null catalog (hosts that don't compose the Agents module) falls back to the
            // shipped tier defaults.
            var catalog = modelCatalog is null
                ? ModelCatalogSnapshot.Static
                : await modelCatalog.GetAsync(ct);
            model = request.Model.Contains("haiku", StringComparison.OrdinalIgnoreCase)
                ? catalog.FastModel(provider)
                : catalog.UtilityModel(provider);

            // Old runners execute utilities on claude regardless of the Provider field —
            // fail actionably instead of silently invoking an unauthenticated CLI.
            var (_, hello) = runner.Status;
            if (hello?.Capabilities?.Contains(RunnerCapabilities.UtilityProvider) is not true)
                throw new InvalidOperationException(
                    $"The connected runner can't run utility calls on {provider} — " +
                    "update Orkka (`git pull`, then `./orkka start`).");
        }

        // Utility generations (brief/PRD synthesis, decomposition, brand kits) can
        // legitimately run past the default 5-minute op ceiling on thinking models —
        // killing them mid-generation wastes the whole spend, so give them real headroom.
        var result = await runner.InvokeAsync<RunnerUtilityResult>(
            RunnerProtocol.UtilityComplete,
            [new RunnerUtilityRequest(model, request.System, userMessage, request.MaxTokens,
                provider == AiProvider.Anthropic ? null : provider.ToString())],
            ct, opTimeout: TimeSpan.FromMinutes(15));

        if (result.Error is not null)
            throw new InvalidOperationException($"Utility run failed on the runner: {result.Error}");

        return new AnthropicResponse
        {
            Text = result.Text ?? "",
            InputTokens = result.InputTokens,
            OutputTokens = result.OutputTokens,
            CacheReadTokens = result.CacheReadTokens,
            CacheWriteTokens = result.CacheWriteTokens,
        };
    }
}

/// <summary>Git workspace ops proxied to the runner. Worktree "paths" are opaque tokens
/// (see RunnerTokens) that only the runner resolves against its cache layout.</summary>
/// <param name="ledger">Where the proxied operations are recorded — the host's ledger, stamped
/// with the runner topology so the dashboard counts them where the git actually ran.</param>
public sealed class RunnerGitWorkspaceManager(
    IRunnerLink runner,
    Orkka.Git.IGitOperationLedger? ledger = null) : IGitWorkspaceManager
{
    private readonly Orkka.Git.IGitOperationLedger _ledger =
        ledger ?? new Orkka.Git.NullGitOperationLedger(Orkka.Git.GitTopologies.UserRunner);

    /// <summary>The ledger kind for a wire op, so a proxied operation counts like a local one.</summary>
    private static Orkka.Git.GitOperationKind KindOf(string method) => method switch
    {
        RunnerProtocol.EnsureBareClone => Orkka.Git.GitOperationKind.EnsureMirror,
        RunnerProtocol.CreateWorktree => Orkka.Git.GitOperationKind.CreateWorkspace,
        RunnerProtocol.SyncBaseBranch => Orkka.Git.GitOperationKind.SyncBase,
        RunnerProtocol.CommitAndPush => Orkka.Git.GitOperationKind.Publish,
        RunnerProtocol.EnsurePushed => Orkka.Git.GitOperationKind.Push,
        RunnerProtocol.GetMergeConflicts => Orkka.Git.GitOperationKind.MergeCheck,
        RunnerProtocol.ApplyMergeStrategy => Orkka.Git.GitOperationKind.ApplyStrategy,
        RunnerProtocol.VerifyMerge => Orkka.Git.GitOperationKind.VerifyMerge,
        RunnerProtocol.GetDiff or RunnerProtocol.GetWorkingTreeDiff => Orkka.Git.GitOperationKind.Diff,
        RunnerProtocol.RemoteBranchExists => Orkka.Git.GitOperationKind.RemoteBranchExists,
        RunnerProtocol.CleanupWorktree or RunnerProtocol.RemoveRepoWorkspace => Orkka.Git.GitOperationKind.Cleanup,
        _ => Orkka.Git.GitOperationKind.Other,
    };
    /// <summary>
    /// A runner that predates developer git stamps the platform identity. The host refuses git work on such a runner, with the remedy, rather
    /// than degrade into it.
    /// </summary>
    private Result<T>? RequireDeveloperGit<T>()
    {
        var (_, hello) = runner.Status;
        return hello?.Capabilities?.Contains(RunnerCapabilities.DeveloperGit) == true
            ? null
            : Result<T>.Failure(
                "The connected runner is too old to work with repositories — update Orkka on that machine (`git pull`, then `./orkka start`).");
    }

    /// <summary>
    /// The runner clones with the developer's own credentials and writes its own identity.
    /// </summary>
    public async Task<Result<string>> EnsureBareCloneAsync(
        Ulid orgId, Ulid repoId, string gitUrl, string? localCheckoutPath = null, CancellationToken ct = default)
    {
        if (RequireDeveloperGit<string>() is { } refused)
            return refused;

        return ToResultOfString(await SafeInvoke(
            RunnerProtocol.EnsureBareClone,
            new EnsureBareCloneRequest(orgId.ToString(), repoId.ToString(), gitUrl, localCheckoutPath), ct));
    }

    public async Task<Result<string>> CreateWorktreeAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string branchName, string baseBranch,
        string? worktreeName = null, CancellationToken ct = default)
    {
        // Older runner binaries ignore the extra WorktreeName property and keep the
        // legacy task-{ulid} folder naming — consistent on their side either way.
        var result = await SafeInvoke(
            RunnerProtocol.CreateWorktree,
            new CreateWorktreeRequest(
                orgId.ToString(), repoId.ToString(), taskId.ToString(), branchName, baseBranch, worktreeName),
            ct);
        // The run request carries the token, not the runner's real path.
        return result.Success
            ? Result<string>.Success(RunnerTokens.Worktree(orgId.ToString(), repoId.ToString(), taskId.ToString()))
            : Result<string>.Failure(result.Error!);
    }

    public async Task<Result<MergeConflictReport>> GetMergeConflictsAsync(
        Ulid orgId, Ulid repoId, string headBranch, string baseBranch, CancellationToken ct = default)
    {
        // Older runners without this op fail the invoke — callers treat that as
        // "mergeability unknown" and keep today's behavior.
        var result = await SafeInvoke(
            RunnerProtocol.GetMergeConflicts,
            new GetMergeConflictsRequest(orgId.ToString(), repoId.ToString(), headBranch, baseBranch),
            ct);
        if (!result.Success)
            return Result<MergeConflictReport>.Failure(result.Error ?? "Merge check failed on the runner.");

        try
        {
            var report = System.Text.Json.JsonSerializer.Deserialize<MergeConflictReport>(
                result.Value ?? "", new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return report is null
                ? Result<MergeConflictReport>.Failure("Runner returned an empty merge check result.")
                : Result<MergeConflictReport>.Success(report);
        }
        catch (System.Text.Json.JsonException ex)
        {
            return Result<MergeConflictReport>.Failure($"Could not parse the runner merge check result: {ex.Message}");
        }
    }

    public async Task<Result<BaseSyncOutcome>> SyncBaseBranchAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string baseBranch,
        bool leaveConflictsInProgress, bool commitLeftovers = false,
        CancellationToken ct = default)
    {
        if (RequireDeveloperGit<BaseSyncOutcome>() is { } refused)
            return refused;

        var result = await SafeInvoke(
            RunnerProtocol.SyncBaseBranch,
            new SyncBaseBranchRequest(
                orgId.ToString(), repoId.ToString(), taskId.ToString(), baseBranch,
                leaveConflictsInProgress,
                commitLeftovers),
            ct);
        if (!result.Success)
            return Result<BaseSyncOutcome>.Failure(result.Error ?? "Base branch sync failed on the runner.");

        try
        {
            var outcome = System.Text.Json.JsonSerializer.Deserialize<BaseSyncOutcome>(
                result.Value ?? "", new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return outcome is null
                ? Result<BaseSyncOutcome>.Failure("Runner returned an empty base sync result.")
                : Result<BaseSyncOutcome>.Success(outcome);
        }
        catch (System.Text.Json.JsonException ex)
        {
            return Result<BaseSyncOutcome>.Failure($"Could not parse the runner base sync result: {ex.Message}");
        }
    }

    public async Task<Result<WorktreeCommandOutcome>> RunWorktreeCommandAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string command, RepoCommandKind kind,
        int timeoutSeconds = 600, CancellationToken ct = default)
    {
        // A runner without command trust would run whatever it is sent. It gets nothing.
        var (_, hello) = runner.Status;
        if (hello?.Capabilities?.Contains(RunnerCapabilities.CommandTrust) != true)
        {
            return Result<WorktreeCommandOutcome>.Failure(
                "The connected runner is too old to run repository commands — update Orkka (`git pull`, then `./orkka start`).");
        }

        var result = await SafeInvoke(
            RunnerProtocol.RunWorktreeCommand,
            new RunWorktreeCommandRequest(
                orgId.ToString(), repoId.ToString(), taskId.ToString(), command, timeoutSeconds,
                kind == RepoCommandKind.Build ? CommandKind.Build : CommandKind.Check),
            ct);
        if (!result.Success)
            return Result<WorktreeCommandOutcome>.Failure(result.Error ?? "The command failed on the runner.");

        var payload = result.Value is null
            ? null
            : System.Text.Json.JsonSerializer.Deserialize<RunWorktreeCommandResult>(result.Value);

        if (payload is null)
            return Result<WorktreeCommandOutcome>.Failure("Runner returned no command result.");

        if (payload.Approval is { } approval)
            return new WorktreeCommandOutcome(-1, null, 0, approval.Message);

        return payload.Error is not null
            ? Result<WorktreeCommandOutcome>.Failure(payload.Error)
            : Result<WorktreeCommandOutcome>.Success(
                new WorktreeCommandOutcome(payload.ExitCode, payload.Output, payload.DurationSeconds));
    }

    public async Task<Result<MergeVerification>> VerifyMergeAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string baseBranch, CancellationToken ct = default)
    {
        // An old runner cannot verify. Reported as a failure, never as a pass: callers must
        // treat "could not verify" as not-verified, or the guard silently stops guarding.
        var (_, hello) = runner.Status;
        if (hello?.Capabilities?.Contains(RunnerCapabilities.MergeVerify) != true)
        {
            return Result<MergeVerification>.Failure(
                "The connected runner cannot verify a resolved merge — update Orkka (`git pull`, then `./orkka start`).");
        }

        var result = await SafeInvoke(
            RunnerProtocol.VerifyMerge,
            new VerifyMergeRequest(orgId.ToString(), repoId.ToString(), taskId.ToString(), baseBranch),
            ct);
        if (!result.Success)
            return Result<MergeVerification>.Failure(result.Error ?? "Merge verification failed on the runner.");

        try
        {
            var verification = System.Text.Json.JsonSerializer.Deserialize<MergeVerification>(
                result.Value ?? "", new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return verification is null
                ? Result<MergeVerification>.Failure("Runner returned an empty merge verification.")
                : Result<MergeVerification>.Success(verification);
        }
        catch (System.Text.Json.JsonException ex)
        {
            return Result<MergeVerification>.Failure($"Could not parse the runner merge verification: {ex.Message}");
        }
    }

    public async Task<Result<MergeStrategyOutcome>> ApplyMergeStrategyAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string baseBranch, string strategy,
        IReadOnlyDictionary<string, string>? fileChoices, CancellationToken ct = default)
    {
        // Older runners lack this op — the orchestrator falls back to the resolver agent
        // with the strategy expressed as prompt guidance.
        var choicesJson = fileChoices is null
            ? null
            : System.Text.Json.JsonSerializer.Serialize(fileChoices);
        var result = await SafeInvoke(
            RunnerProtocol.ApplyMergeStrategy,
            new ApplyMergeStrategyRequest(
                orgId.ToString(), repoId.ToString(), taskId.ToString(), baseBranch, strategy, choicesJson),
            ct);
        if (!result.Success)
            return Result<MergeStrategyOutcome>.Failure(result.Error ?? "Merge strategy failed on the runner.");

        try
        {
            var outcome = System.Text.Json.JsonSerializer.Deserialize<MergeStrategyOutcome>(
                result.Value ?? "", new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return outcome is null
                ? Result<MergeStrategyOutcome>.Failure("Runner returned an empty merge strategy result.")
                : Result<MergeStrategyOutcome>.Success(outcome);
        }
        catch (System.Text.Json.JsonException ex)
        {
            return Result<MergeStrategyOutcome>.Failure($"Could not parse the runner merge strategy result: {ex.Message}");
        }
    }

    public async Task<Result> CommitAndPushAsync(
        string worktreePath, string message, string branchName, CancellationToken ct = default)
        => ToResult(await SafeInvoke(
            RunnerProtocol.CommitAndPush,
            new CommitAndPushRequest(worktreePath, message, branchName), ct));

    public async Task CleanupWorktreeAsync(Ulid orgId, Ulid repoId, Ulid taskId)
        => await SafeInvoke(
            RunnerProtocol.CleanupWorktree,
            new CleanupWorktreeRequest(orgId.ToString(), repoId.ToString(), taskId.ToString()),
            CancellationToken.None);

    // Worktree GC and reconciliation live entirely on the runner's own schedule — the host
    // cannot enumerate a filesystem it does not have.
    public Task<int> CleanupStaleWorktreesAsync(TimeSpan maxAge, CancellationToken ct = default)
        => Task.FromResult(0);

    public IReadOnlyList<(Ulid OrgId, Ulid RepoId)> EnumerateRepoWorkspaces() => [];

    public async Task RemoveRepoWorkspaceAsync(Ulid orgId, Ulid repoId, CancellationToken ct = default)
        => await SafeInvoke(
            RunnerProtocol.RemoveRepoWorkspace,
            new RemoveRepoWorkspaceRequest(orgId.ToString(), repoId.ToString()), ct);

    public string GetWorktreePath(Ulid orgId, Ulid repoId, Ulid taskId)
        => RunnerTokens.Worktree(orgId.ToString(), repoId.ToString(), taskId.ToString());

    public string GetBareRepoPath(Ulid orgId, Ulid repoId)
        => $"runner-bare:{orgId}:{repoId}";

    public async Task<Result> EnsurePushedAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string branchName, CancellationToken ct = default)
        => ToResult(await SafeInvoke(
            RunnerProtocol.EnsurePushed,
            new EnsurePushedRequest(orgId.ToString(), repoId.ToString(), taskId.ToString(), branchName), ct));

    public async Task<bool> RemoteBranchExistsAsync(
        Ulid orgId, Ulid repoId, string branchName, CancellationToken ct = default)
    {
        var result = await SafeInvoke(
            RunnerProtocol.RemoteBranchExists,
            new RemoteBranchExistsRequest(orgId.ToString(), repoId.ToString(), branchName), ct);
        return result.Success && result.Value == "true";
    }

    public async Task<Result<string>> GetDiffAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string baseBranch, int maxChars = 60_000,
        string? headBranch = null, CancellationToken ct = default)
        => ToResultOfString(await SafeInvoke(
            RunnerProtocol.GetDiff,
            new GetDiffRequest(
                orgId.ToString(), repoId.ToString(), taskId.ToString(), baseBranch, maxChars, headBranch),
            ct));

    public async Task<Result<string>> GetDiffSinceAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string baseBranch, string baseSha,
        int maxChars = 60_000, string? headBranch = null, CancellationToken ct = default)
        // A runner without "stories" ignores BaseSha and answers the full branch diff — still true.
        => ToResultOfString(await SafeInvoke(
            RunnerProtocol.GetDiff,
            new GetDiffRequest(
                orgId.ToString(), repoId.ToString(), taskId.ToString(), baseBranch, maxChars, headBranch, baseSha),
            ct));

    public async Task<string?> ResolveReviewBaseAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string branchName, string baseBranch, CancellationToken ct = default)
    {
        if (runner.Status.Hello?.Capabilities?.Contains(RunnerCapabilities.Stories) != true)
            return null;
        var result = await SafeInvoke(
            RunnerProtocol.ResolveReviewBase,
            new ResolveReviewBaseRequest(orgId.ToString(), repoId.ToString(), taskId.ToString(), branchName, baseBranch), ct);
        return result.Success && GitArgs.IsSafeSha(result.Value?.Trim()) ? result.Value!.Trim() : null;
    }

    public async Task<Result<string>> GetWorkingTreeDiffAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string baseBranch, int maxChars = 60_000,
        CancellationToken ct = default, string? baseSha = null)
    {
        // Old runners lack the op — fail quietly so the orchestrator skips live snapshots
        // for this run instead of logging an unknown-method error every few seconds.
        var (_, hello) = runner.Status;
        if (hello?.Capabilities?.Contains(RunnerCapabilities.WorkingTreeDiff) != true)
            return Result<string>.Failure("The connected runner does not support working-tree diffs — update Orkka (`git pull`, then `./orkka start`).");

        return ToResultOfString(await SafeInvoke(
            RunnerProtocol.GetWorkingTreeDiff,
            new GetWorkingTreeDiffRequest(
                orgId.ToString(), repoId.ToString(), taskId.ToString(), baseBranch, maxChars, baseSha),
            ct));
    }

    public async Task<Result> WriteWorktreeContextAsync(
        Ulid orgId, Ulid repoId, Ulid taskId,
        IReadOnlyList<WorktreeContextFile> files, CancellationToken ct = default)
    {
        // Old runners lack the op entirely — fail so the caller falls back to inlining.
        var (_, hello) = runner.Status;
        if (hello?.Capabilities?.Contains(RunnerCapabilities.ContextFiles) != true)
            return Result.Failure("The connected runner does not support worktree context files — update Orkka (`git pull`, then `./orkka start`).");

        return ToResult(await SafeInvoke(
            RunnerProtocol.WriteWorktreeFiles,
            new WriteWorktreeFilesRequest(
                orgId.ToString(), repoId.ToString(), taskId.ToString(),
                files.Select(f => new RunnerContextFile(f.RelativePath, f.Content)).ToArray()),
            ct));
    }

    public async Task<Result<SetupCommandOutcome>> RunSetupCommandAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string command,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command))
            return new SetupCommandOutcome(Skipped: true, 0, null, 0);

        // A runner without command trust would run whatever it is sent. Not fatal — the worktree
        // is simply unprepared, and the caller reports that rather than failing the phase.
        var (_, hello) = runner.Status;
        if (hello?.Capabilities?.Contains(RunnerCapabilities.CommandTrust) != true)
        {
            return Result<SetupCommandOutcome>.Failure(
                "The connected runner is too old to run repository commands — update Orkka (`git pull`, then `./orkka start`).");
        }

        var op = await SafeInvoke(
            RunnerProtocol.RunSetupCommand,
            new RunSetupCommandRequest(
                orgId.ToString(), repoId.ToString(), taskId.ToString(), command),
            ct);

        if (!op.Success)
            return Result<SetupCommandOutcome>.Failure(op.Error ?? "Setup command failed.");

        var payload = op.Value is null
            ? null
            : System.Text.Json.JsonSerializer.Deserialize<RunSetupCommandResult>(op.Value);

        if (payload is null)
            return Result<SetupCommandOutcome>.Failure("Runner returned no setup result.");

        if (payload.Approval is { } approval)
            return new SetupCommandOutcome(false, -1, null, 0, approval.Message);

        return payload.Error is not null
            ? Result<SetupCommandOutcome>.Failure(payload.Error)
            : new SetupCommandOutcome(
                payload.Skipped, payload.ExitCode, payload.Output, payload.DurationSeconds);
    }

    /// <summary>The ids a wire request names, so a proxied op's ledger row reads like a local
    /// one (repo=…, task=…) instead of the anonymous rows the dashboard showed at first.</summary>
    private static (string? OrgId, string? RepoId, string? TaskId) IdsOf(object request) => request switch
    {
        EnsureBareCloneRequest r => (r.OrgId, r.RepoId, null),
        CreateWorktreeRequest r => (r.OrgId, r.RepoId, r.TaskId),
        CommitAndPushRequest r => RunnerTokens.TryParseWorktree(r.WorktreeToken, out var o, out var p, out var t)
            ? (o, p, t)
            : (null, null, null),
        EnsurePushedRequest r => (r.OrgId, r.RepoId, r.TaskId),
        RemoteBranchExistsRequest r => (r.OrgId, r.RepoId, null),
        GetDiffRequest r => (r.OrgId, r.RepoId, r.TaskId),
        GetWorkingTreeDiffRequest r => (r.OrgId, r.RepoId, r.TaskId),
        GetMergeConflictsRequest r => (r.OrgId, r.RepoId, null),
        ApplyMergeStrategyRequest r => (r.OrgId, r.RepoId, r.TaskId),
        SyncBaseBranchRequest r => (r.OrgId, r.RepoId, r.TaskId),
        VerifyMergeRequest r => (r.OrgId, r.RepoId, r.TaskId),
        RunWorktreeCommandRequest r => (r.OrgId, r.RepoId, r.TaskId),
        RunSetupCommandRequest r => (r.OrgId, r.RepoId, r.TaskId),
        CleanupWorktreeRequest r => (r.OrgId, r.RepoId, r.TaskId),
        RemoveRepoWorkspaceRequest r => (r.OrgId, r.RepoId, null),
        _ => (null, null, null),
    };

    private async Task<RunnerOpResult> SafeInvoke(string method, object request, CancellationToken ct)
    {
        var (orgId, repoId, taskId) = IdsOf(request);
        using var op = Orkka.Git.GitOperationScope.Begin(_ledger, KindOf(method), orgId, repoId, taskId);
        RunnerOpResult result;
        try
        {
            result = await runner.InvokeAsync<RunnerOpResult>(method, [request], ct, RunnerOpBudgets.For(method));
        }
        catch (RunnerUnavailableException ex)
        {
            result = RunnerOpResult.Fail(ex.Message);
            op.Fail(ex.Message, Orkka.Git.GitErrorCode.Network);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = RunnerOpResult.Fail($"Runner operation {method} failed: {ex.Message}");
            op.Fail(result.Error);
            return result;
        }

        if (result.Success) op.Complete(); else op.Fail(result.Error);
        return result;
    }

    private static Result ToResult(RunnerOpResult r)
        => r.Success ? Result.Success() : Result.Failure(r.Error!);

    private static Result<string> ToResultOfString(RunnerOpResult r)
        => r.Success ? Result<string>.Success(r.Value ?? "") : Result<string>.Failure(r.Error!);
}

/// <summary>Repo browsing (file tree/content) via the runner's bare cache.</summary>
public sealed class RunnerGitRepoReader(IRunnerLink runner) : IGitRepoReader
{
    public async Task<Result<List<FileTreeNode>>> GetFileTreeAsync(
        Ulid orgId, Ulid repoId, string branch, CancellationToken ct = default)
    {
        try
        {
            var result = await runner.InvokeAsync<RunnerFileTreeResult>(
                RunnerProtocol.GetFileTree,
                [new GetFileTreeRequest(orgId.ToString(), repoId.ToString(), branch)], ct);

            return result.Success
                ? Result<List<FileTreeNode>>.Success(
                    (result.Paths ?? []).Select(p => new FileTreeNode(p)).ToList())
                : Result<List<FileTreeNode>>.Failure(result.Error!);
        }
        catch (RunnerUnavailableException ex)
        {
            return Result<List<FileTreeNode>>.Failure(ex.Message);
        }
    }

    public async Task<Result<string>> GetFileContentAsync(
        Ulid orgId, Ulid repoId, string branch, string filePath, CancellationToken ct = default)
    {
        try
        {
            var result = await runner.InvokeAsync<RunnerOpResult>(
                RunnerProtocol.GetFileContent,
                [new GetFileContentRequest(orgId.ToString(), repoId.ToString(), branch, filePath)], ct);
            return result.Success
                ? Result<string>.Success(result.Value ?? "")
                : Result<string>.Failure(result.Error!);
        }
        catch (RunnerUnavailableException ex)
        {
            return Result<string>.Failure(ex.Message);
        }
    }

    public async Task<Result> EnsureBareCloneAsync(
        Ulid orgId, Ulid repoId, string gitUrl, CancellationToken ct = default)
    {
        try
        {
            var result = await runner.InvokeAsync<RunnerOpResult>(
                RunnerProtocol.EnsureBareClone,
                [new EnsureBareCloneRequest(orgId.ToString(), repoId.ToString(), gitUrl)], ct);
            return result.Success ? Result.Success() : Result.Failure(result.Error!);
        }
        catch (RunnerUnavailableException ex)
        {
            return Result.Failure(ex.Message);
        }
    }

    public async Task<Result<List<GitCommitInfo>>> GetCommitLogAsync(
        Ulid orgId, Ulid repoId, string branch, int skip, int take,
        string? filePath = null, CancellationToken ct = default)
    {
        try
        {
            var result = await runner.InvokeAsync<RunnerCommitLogResult>(
                RunnerProtocol.GetCommitLog,
                [new GetCommitLogRequest(orgId.ToString(), repoId.ToString(), branch, skip, take, filePath)], ct);

            return result.Success
                ? Result<List<GitCommitInfo>>.Success((result.Commits ?? [])
                    .Select(c => new GitCommitInfo(c.Sha, c.Author, c.Date, c.Subject))
                    .ToList())
                : Result<List<GitCommitInfo>>.Failure(result.Error!);
        }
        catch (RunnerUnavailableException ex)
        {
            return Result<List<GitCommitInfo>>.Failure(ex.Message);
        }
    }

    public async Task<Result<List<GitBranchInfo>>> ListBranchesAsync(
        Ulid orgId, Ulid repoId, CancellationToken ct = default)
    {
        try
        {
            var result = await runner.InvokeAsync<RunnerBranchListResult>(
                RunnerProtocol.ListBranches,
                [new ListBranchesRequest(orgId.ToString(), repoId.ToString())], ct);

            return result.Success
                ? Result<List<GitBranchInfo>>.Success((result.Branches ?? [])
                    .Select(b => new GitBranchInfo(b.Name, b.Sha, b.LastCommitSubject, b.LastCommitDate, b.IsRemote))
                    .ToList())
                : Result<List<GitBranchInfo>>.Failure(result.Error!);
        }
        catch (RunnerUnavailableException ex)
        {
            return Result<List<GitBranchInfo>>.Failure(ex.Message);
        }
    }

    public async Task<Result> EnsureBranchAsync(
        Ulid orgId, Ulid repoId, string branch, CancellationToken ct = default)
    {
        try
        {
            var result = await runner.InvokeAsync<RunnerOpResult>(
                RunnerProtocol.EnsureLocalBranch,
                [new EnsureLocalBranchRequest(orgId.ToString(), repoId.ToString(), branch)], ct);
            return result.Success ? Result.Success() : Result.Failure(result.Error!);
        }
        catch (RunnerUnavailableException ex)
        {
            return Result.Failure(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An older runner without this op fails the invocation generically.
            return Result.Failure(
                "The connected runner doesn't support checking out remote branches — update Orkka (`git pull`, then `./orkka start`).");
        }
    }

    public async Task<Result> CreateBranchAsync(
        Ulid orgId, Ulid repoId, string newBranch, string baseBranch, CancellationToken ct = default)
    {
        try
        {
            var result = await runner.InvokeAsync<RunnerOpResult>(
                RunnerProtocol.CreateRepoBranch,
                [new CreateRepoBranchRequest(orgId.ToString(), repoId.ToString(), newBranch, baseBranch)], ct);
            return result.Success ? Result.Success() : Result.Failure(result.Error!);
        }
        catch (RunnerUnavailableException ex)
        {
            return Result.Failure(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An older runner without this op fails the invocation generically.
            return Result.Failure(
                "The connected runner doesn't support creating branches — update Orkka (`git pull`, then `./orkka start`).");
        }
    }

    public async Task<Result<string>> GetCommitDiffAsync(
        Ulid orgId, Ulid repoId, string sha, int maxChars = 200_000, CancellationToken ct = default)
    {
        try
        {
            var result = await runner.InvokeAsync<RunnerOpResult>(
                RunnerProtocol.GetCommitDiff,
                [new GetCommitDiffRequest(orgId.ToString(), repoId.ToString(), sha, maxChars)], ct);
            return result.Success
                ? Result<string>.Success(result.Value ?? "")
                : Result<string>.Failure(result.Error!);
        }
        catch (RunnerUnavailableException ex)
        {
            return Result<string>.Failure(ex.Message);
        }
    }

    public async Task<Result<List<RepoTreeEntry>>> ListTreeAsync(
        Ulid orgId, Ulid repoId, string branch, string? dirPath = null, CancellationToken ct = default)
    {
        try
        {
            var result = await runner.InvokeAsync<RunnerTreeResult>(
                RunnerProtocol.ListTree,
                [new ListTreeRequest(orgId.ToString(), repoId.ToString(), branch, dirPath)], ct);
            return result.Success
                ? Result<List<RepoTreeEntry>>.Success((result.Entries ?? [])
                    .Select(e => new RepoTreeEntry(e.Name, e.Path, e.IsDirectory))
                    .ToList())
                : Result<List<RepoTreeEntry>>.Failure(result.Error!);
        }
        catch (RunnerUnavailableException ex)
        {
            return Result<List<RepoTreeEntry>>.Failure(ex.Message);
        }
    }
}
