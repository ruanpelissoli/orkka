namespace Orkka.Modules.Agents.Contracts;

/// <summary>Repo made available to an agent chat workspace (light projection so this
/// contract does not depend on the Projects module's RepoSummary). GitUrl is carried
/// because the machine that materializes the workspace is not always the machine the
/// caller cloned on — a runner-backed invoker has to clone it there itself.</summary>
/// <param name="Branch">The branch to check out instead of the default one, when it exists on
/// the remote — a task's Discuss chat reads the task branch. Null = the default branch.</param>
public sealed record ChatWorkspaceRepo(
    Ulid Id, string Name, string? DefaultBranch, string GitUrl, string? Branch = null);

/// <summary>Why a repo is missing from the chat workspace. Drives the wording the user is
/// shown, so it must distinguish the one failure they can act on — credentials — from the
/// ones they cannot. Wire-serialized across the hosted relay by NAME, so members may be
/// added but never renamed or renumbered.</summary>
public enum RepoAccessFailureReason
{
    /// <summary>Classification failed. The raw error is worth showing, truncated.</summary>
    Unknown,

    /// <summary>Git could not authenticate to the remote — no credential helper on the
    /// machine that ran the clone, expired token, or an SSH key the remote rejects. The
    /// only reason the user can fix themselves, and the one seen in the wild.</summary>
    GitCredentials,

    /// <summary>The remote answered but has no such repository (renamed, deleted, or the
    /// URL is wrong). Note that GitHub also reports this for a private repo the caller
    /// cannot see, so <see cref="GitCredentials"/> is matched first.</summary>
    RepoNotFound,

    /// <summary>No bare clone exists on the machine building the workspace and this
    /// invoker does not clone (the in-process one relies on the caller's sync pass).</summary>
    NotCloned,

    /// <summary>The runner that would hold the code was not reachable for this turn.</summary>
    RunnerUnavailable,
}

/// <summary>A repo that could not be put in the chat workspace, and why.
/// <paramref name="Detail"/> is the underlying error, kept for logs and for the
/// <see cref="RepoAccessFailureReason.Unknown"/> case — never the whole of what the user
/// is shown, since raw git stderr does not tell anyone what to do about it.</summary>
/// <remarks><c>OnUserMachine</c> records WHOSE git failed, because the advice inverts: a
/// runner-routed clone runs under the user's own credentials and they fix it locally, while
/// a cloud clone runs under Orkka's, where "sign in on your machine" would send them to fix
/// something that had nothing to do with it. Stamped where the failure happened rather than
/// inferred later, so a turn mixing both sources still says the right thing about each.</remarks>
public sealed record UnavailableChatRepo(
    Ulid Id, string Name, RepoAccessFailureReason Reason, string? Detail = null,
    bool OnUserMachine = false);

/// <summary>
/// Outcome of preparing a chat workspace: the opaque workspace token, plus the repos that
/// could NOT be materialized on the machine that will run the turn. The caller must drop
/// the unavailable ones from the system prompt — telling an agent it has read access to a
/// repo that is not in its Cwd is the worst of both worlds: it hunts for files that aren't
/// there, then reports the project as broken.
///
/// Dropping them silently is only half the job: the user then asks about their own code and
/// is told it cannot be found, with the reason living in a pod log. Callers that stream a
/// turn are expected to surface <see cref="Unavailable"/> to the human as well — see
/// <see cref="RepoAccessDiagnostics.BuildNotice"/>.
/// </summary>
public sealed record ChatWorkspacePreparation(
    string Token, IReadOnlyList<UnavailableChatRepo> Unavailable)
{
    /// <summary>Ids alone, for callers that only need to filter the repo list.</summary>
    public IReadOnlyList<Ulid> UnavailableRepoIds => [.. Unavailable.Select(r => r.Id)];

    public static ChatWorkspacePreparation Complete(string token) => new(token, []);
}

public sealed record ChatChunk(
    ChatChunkType Type,
    string Content,
    int PromptTokens = 0,
    int CompletionTokens = 0,
    int CacheReadTokens = 0,
    int CacheWriteTokens = 0,
    // Claude CLI session id of the run, reported on the Done chunk. Persisting it and
    // passing it back as resumeSessionId on the next turn resumes the warm session.
    string? SessionId = null,
    // Cost the vendor's own tooling reported for this turn, on the Done chunk. Recorded next
    // to our computed cost purely as a check that our rate table matches reality — never used
    // to bill. Null for providers that report none. See UsageRecord.VendorReportedCostUsd.
    decimal? VendorCostUsd = null,
    // The turn executed on the user's own machine under their subscription, so its tokens
    // cost the platform nothing and must be recorded at $0 (FlatCostBase) — the same
    // convention runner-routed task runs and utility calls use. Stamped by whichever proxy
    // did the routing (the hosted relay, the runner chat proxy); false means this host ran
    // it on the platform key. Additive on the wire: an older Worker that doesn't send it
    // leaves it false, and the hosted relay re-stamps what it routed itself.
    bool ServedByRunner = false);

/// <remarks>Appended to, never reordered: these cross a process boundary as JSON (the
/// Worker's chat relay streams ChatChunks to the API) where the wire form is the numeric
/// value.</remarks>
public enum ChatChunkType
{
    Thinking,
    Text,
    ToolUse,
    Error,
    Done,
    /// <summary>An artifact a platform tool created during the turn, as the JSON payload the
    /// client already renders (<c>{"document":{"id":…,"title":…}}</c>). Only relayed turns
    /// produce these — an in-process turn's tool server emits straight to the SSE stream.</summary>
    Artifact,
}

/// <summary>
/// Runs a read-only agent-chat turn against a workspace of project repos. The in-process
/// implementation spawns the Claude Agent SDK on this host; alternative hosts may proxy
/// both operations elsewhere (e.g. Orkka Local routes them to the user's machine), so the
/// returned workspace path must be treated as an opaque token — only ever passed back to
/// <see cref="InvokeAgentAsync"/>, never interpreted as a local filesystem path.
/// </summary>
public interface IChatAgentInvoker
{
    /// <summary>
    /// Ensures all given repos have read-only worktrees in the PROJECT's chat workspace.
    /// The workspace must be scoped per project — an org-shared folder accumulates
    /// worktrees from every project ever chatted with, and the agent (whose Cwd is the
    /// workspace root) then reads the wrong project's code and branding.
    /// The implementation is responsible for the bare clones on ITS OWN machine — the
    /// caller's "ensure the repo is synced" pass runs against the host's disk, which is
    /// the wrong disk for a runner-backed invoker.
    /// Returns the workspace root token plus any repo it could not materialize.
    /// </summary>
    /// <param name="taskId">Non-null for a task's Discuss chat: the workspace is the TASK's own
    /// (never the project chat's, never the task's running worktree), and each repo checks out
    /// its <see cref="ChatWorkspaceRepo.Branch"/> when that branch exists on the remote.</param>
    Task<ChatWorkspacePreparation> EnsureChatWorkspaceAsync(
        Ulid orgId, Ulid projectId, IReadOnlyList<ChatWorkspaceRepo> repos,
        Ulid? taskId = null, CancellationToken ct = default);

    /// <summary>
    /// Why a turn for the ambient requester (<c>ChatRequesterContext</c>) cannot run right now —
    /// the runner is offline, the relay is not configured — worded for the human; null when it
    /// can. Lets a caller fail fast with an actionable message before preparing a workspace.
    /// </summary>
    Task<string?> GetRouteBlockerAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);

    /// <summary>
    /// Invokes the agent with read-only tools in the workspace and streams results.
    /// When <paramref name="resumeSessionId"/> is set, the run resumes that Claude CLI
    /// session (the CLI keeps its own conversation state, so the caller should not
    /// re-inject history into the system prompt). Session files live on the machine that
    /// executes the run — a missing session surfaces as an Error chunk (or throws), and
    /// the caller is expected to retry with a fresh session and full history.
    /// <paramref name="conversationId"/> lets implementations keep a warm CLI process per
    /// conversation across turns (purely an accelerator — behavior is identical without it).
    /// <paramref name="toolServer"/> attaches this turn's platform tools (create documents/
    /// tasks/etc.); only invokers reporting <see cref="SupportsChatTools"/> honor it —
    /// others ignore it and the turn falls back to fenced-block extraction.
    /// </summary>
    /// <param name="toolContext">Identity of the turn whose tools these are. Needed only by
    /// invokers that must REBUILD the tool server somewhere else (the hybrid invoker relays
    /// it to the Worker, which constructs the same server against the same database); the
    /// in-process invoker uses <paramref name="toolServer"/> and ignores this.</param>
    IAsyncEnumerable<ChatChunk> InvokeAgentAsync(
        string workspacePath, string systemPrompt, string userMessage,
        string model, string? resumeSessionId = null, Ulid? conversationId = null,
        IChatToolServer? toolServer = null,
        ChatToolServerContext? toolContext = null,
        CancellationToken ct = default);

    /// <summary>
    /// Whether this invoker can attach an <see cref="IChatToolServer"/> to a run. False by
    /// default — only invokers that execute the Claude Agent SDK in THIS process can bridge
    /// the in-process MCP server (relayed/runner-side executions cannot reach it). Callers
    /// use this to decide the prompt variant, so it must be accurate.
    /// </summary>
    bool SupportsChatTools => false;

    /// <summary>
    /// Whether a resumeSessionId is honored end-to-end for the given provider. Callers
    /// send a history-free system prompt when resuming, so this must be false when the
    /// backend would silently run fresh (e.g. a local runner without the cli-resume
    /// capability) — slim + fresh means lost conversation context, not just lost savings.
    /// </summary>
    bool SupportsSessionResume(AiProvider provider) => provider == AiProvider.Anthropic;
}
