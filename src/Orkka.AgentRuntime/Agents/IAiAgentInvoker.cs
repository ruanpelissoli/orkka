namespace Orkka.Modules.Execution.Agents;


public sealed record AgentChunk(
    AgentChunkType Type,
    string Content,
    int PromptTokens = 0,
    int CompletionTokens = 0,
    int CacheReadTokens = 0,
    int CacheWriteTokens = 0,
    string? SessionId = null,
    // Cost the vendor's own tooling reported for this run, when it reports one (Claude CLI's
    // total_cost_usd). Carried on the Done chunk only, and only ever recorded for comparison
    // against our computed cost — billing is always ours. Null for every other provider.
    decimal? VendorCostUsd = null,
    // Done chunk only: the run ended because RunCostGuard reached the request's MaxCostUsd —
    // the agent was stopped at a spend checkpoint and wrote a hand-off, it did not finish.
    // The orchestrator parks the task and asks the requester instead of advancing the phase.
    bool SpendLimitReached = false);

public enum AgentChunkType
{
    Thinking,
    Text,
    ToolUse,
    ToolResult,
    Error,
    Done
}

public enum AgentPhase
{
    Planning,
    Developing,
    Review,
    Testing,

    /// <summary>One-shot text generation (brief/PRD synthesis, task decomposition, brand
    /// kits): no repo, no tools. Invokers strip the tool schema entirely so the model
    /// cannot burn turns or wall-clock exploring a scratch directory.</summary>
    Utility,

    /// <summary>Grounded reading: the repo it is asked about plus the open web, and nothing
    /// else. Briefing, brief/PRD synthesis, roadmap and task decomposition run here — all of
    /// them decide things (stack, versions, task shape) that every downstream run inherits,
    /// and all of them used to decide blind from a file-tree snapshot.
    ///
    /// Read-only by construction: no Bash, no Write, no Edit. That is what makes the phase
    /// safe to point at a customer workspace without the sandbox and the Bash guard — there
    /// is no command to guard. Web access is the whole point, so unlike <see cref="Utility"/>
    /// the tool schema is kept.</summary>
    Research
}

public sealed record AgentRequest(
    string SystemPrompt,
    string UserMessage,
    string WorktreePath,
    AiProvider Provider,
    string? Model = null,
    int MaxTokens = 4096,
    int MaxTurns = 25,
    AgentPhase Phase = AgentPhase.Planning,
    string AgentName = "agent",
    string AgentRole = "developer",
    Ulid OrgId = default,
    Ulid ProjectId = default,
    Ulid? TaskId = null,
    Ulid? AgentId = null,
    Ulid? ExecutionId = null,
    // Claude CLI session id from a previous run of the same conversation. When set, the
    // run resumes that session (warm start, no history re-injection) instead of cold-
    // booting a new one. Only honored by the Claude invoker; session files live on the
    // machine that ran the previous turn, so callers must fall back to a fresh run when
    // resuming fails there.
    string? ResumeSessionId = null,
    // Per-run overrides for the tool-call and wall-clock ceilings. When null, the invoker
    // falls back to the process-wide AgentLimitsOptions. The orchestrator sets these from
    // its per-phase budgets so a heavy Developing/scaffold run gets more headroom than a
    // PM refine, without loosening the global default that guards ad-hoc callers (chat,
    // utility, doctor). MaxTurns already travels per-run above; these complete the trio so
    // raising turns alone can't just shift the wall into a different ceiling.
    int? MaxToolCalls = null,
    int? MaxWallClockSeconds = null,
    // This run is a turn of an interactive development session, so a human is present for every
    // command. Swaps the Bash guard for InteractiveBashGuard, which keeps the same Layer 3
    // blocklist but can ask about anything the policy does not recognise as ordinary work
    // instead of either refusing it or letting it through unseen.
    bool Interactive = false,
    // Per-run vendor-cost ceiling in USD; <= 0 disables (the shipped default — see
    // PhaseBudgetsOptions for the arming philosophy). Enforced mid-run by RunCostGuard as a
    // SOFT stop: further tool calls are refused with a wrap-up instruction, the run is never
    // killed. Claude-only, like the other hooks.
    decimal MaxCostUsd = 0m,
    // This Testing run verifies an HTTP API rather than a browser UI. The QA lane's normal
    // procedure — navigate, click, assert — has nothing to drive on a repo that serves no UI,
    // so an API run instead runs the repo's own test suite against the started app and probes
    // the changed endpoints. It stays read-only (writing the tests is the dev agent's job) and
    // needs no Playwright server. Older runners default this to false and fall back to the
    // browser tool set — degraded, never broken.
    bool ApiTesting = false,
    // Extra environment for the agent process, applied AFTER the sanitizer so these names
    // win. Today: the commit identity (GIT_AUTHOR_* / GIT_COMMITTER_*). Plain JSON on
    // runner-proxied runs.
    IReadOnlyDictionary<string, string>? Environment = null,
    // A tracker task's ticket (the runner's connection id + the ticket key): the runner copies
    // the attachments it cached for that ticket into the worktree before the agent starts.
    string? TicketConnectionKey = null,
    string? TicketKey = null,
    // Folders the agent may READ besides its worktree: the project's base folder for a
    // folder-of-repos project, so it can look at the sibling repositories for reference.
    // Claude and Copilot get `--add-dir`; Codex already reads anywhere. Writes stay confined to
    // the worktree (AgentWorktreeBoundaryGuard), and dotenv files outside the worktree stay
    // unreadable (DeveloperSecrets).
    IReadOnlyList<string>? ReadOnlyDirs = null,
    // The runner workspace (profile name) the run serves — which workspace's AI sign-in
    // overrides apply (ProviderAuthEnvironment). Set by the runner itself, never by the host.
    string? Workspace = null)
{
    /// <summary>
    /// Optional in-process MCP tool server for this run — how a pipeline agent reaches
    /// project context (document titles, repos, board, task history) on demand instead of
    /// having it all inlined into the prompt. Claude-only today, mirrored on the chat
    /// stack's SdkChatToolServerAdapter registration.
    ///
    /// A live object, so it never crosses a process boundary: JsonIgnore keeps runner-
    /// proxied requests serializable (the property arrives null on the runner, which is
    /// also why the orchestrator only attaches it to in-process routes).
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ih0.Claude.Agent.SDK.ISdkMcpServer? ToolServer { get; init; }

}

public interface IAiAgentInvoker
{
    /// <summary>
    /// Invokes the AI agent and streams response chunks.
    /// </summary>
    IAsyncEnumerable<AgentChunk> InvokeAsync(
        AgentRequest request, CancellationToken ct = default);

    /// <summary>
    /// Whether <see cref="AgentRequest.ResumeSessionId"/> is honored end-to-end. Callers
    /// slim the prompt when resuming, so this must be false when the request would silently
    /// run cold (e.g. a connected runner that predates the agent-resume capability) —
    /// slim + cold means missing context, not just missing savings.
    /// </summary>
    bool SupportsSessionResume => true;

    /// <summary>
    /// Stops any run of <paramref name="taskId"/> still executing on this invoker's backend,
    /// including runs THIS process never started — the case after a deploy rolls the worker
    /// replica that did, leaving an agent on a user's runner reading a worktree the new
    /// replica is about to delete. In-process invokers have nothing to do: their runs die
    /// with the process and live ones are cancelled through the orchestrator's own token.
    /// Runner-backed invokers forward it to the runner, which cancels by task id and waits
    /// for the agent process to exit. Throws <c>RunnerUnavailableException</c> when the
    /// backend is offline, so callers can leave the work pending for retry instead of
    /// proceeding as if the run were gone.
    /// </summary>
    Task CancelTaskRunsAsync(Ulid taskId, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    /// Whether <see cref="AgentRequest.ToolServer"/> reaches the spawned agent. False by
    /// default: the server is a live in-process object that cannot cross a process boundary
    /// (JsonIgnore drops it on the wire), and a prompt that advertises tools the run doesn't
    /// have teaches the agent its tools are broken. A proxy invoker may claim support when
    /// it FORWARDS tool calls back to this process instead (RunnerAgentInvoker does, gated
    /// on the connected runner's project-tools capability).
    /// </summary>
    bool SupportsInProcessToolServer => false;
}
