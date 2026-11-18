namespace Orkka.LocalRunner.Contracts;

/// <summary>
/// Wire contract between the hosted Worker's runner hub and Orkka.LocalRunner (on the
/// developer's PC). Dependency-free by design: ids travel as strings, filesystem paths only ever appear
/// on the runner side or as opaque tokens the host echoes back. Bump
/// <see cref="ProtocolVersion"/> on ANY breaking change — the host rejects mismatched
/// runners with an explicit "update your runner" error.
/// </summary>
public static class RunnerProtocol
{
    // v3: EnsureBareCloneRequest.SourcePath → CloneUrlOrPath (forge URLs are first-class),
    // RunnerHello.DaemonVersion, the `hybrid` capability, and the last real filesystem
    // paths (UserRepoPath on sync/push/publish/squash-merge ops) replaced by opaque
    // RunnerTokens.UserRepo tokens the runner resolves itself.
    // v4: the runner uses the developer's own git credentials and identity (`developer-git`
    // replaces `platform-git` and `managed-git`); EnsureBareCloneRequest lost the platform
    // identity fields.
    // v5: tracker intake v2 — a poll runs an intake query (assigned to me, intake statuses) and a
    // tracking read (the known keys, any status); TrackerTicket carries InIntake/AssignedToMe/
    // Status/parent/children, which a v4 runner never sets (the host would import nothing). Also
    // AddTrackerConnection / RemoveTrackerConnection and RunnerHello.PendingCommandApprovals.
    public const int ProtocolVersion = 5;

    /// <summary>Hub path on the local host the runner dials into.</summary>
    public const string HubPath = "/hubs/runner";

    // Host → runner (client results / invocations)
    public const string ListDirectory = nameof(ListDirectory);
    public const string ValidateRepoPath = nameof(ValidateRepoPath);
    public const string InitLocalRepo = nameof(InitLocalRepo);
    public const string InitRepoInPlace = nameof(InitRepoInPlace);
    /// <summary>Lists the repositories in a base folder. See
    /// <see cref="RunnerCapabilities.RepoDiscovery"/>.</summary>
    public const string DiscoverRepos = nameof(DiscoverRepos);
    public const string EnsureBareClone = nameof(EnsureBareClone);
    public const string CreateWorktree = nameof(CreateWorktree);
    public const string CommitAndPush = nameof(CommitAndPush);
    public const string EnsurePushed = nameof(EnsurePushed);
    public const string RemoteBranchExists = nameof(RemoteBranchExists);
    public const string GetDiff = nameof(GetDiff);
    /// <summary>Working-tree diff (uncommitted + untracked included) — the Code View's live
    /// picture of a run in progress. See <see cref="RunnerCapabilities.WorkingTreeDiff"/>.</summary>
    public const string GetWorkingTreeDiff = nameof(GetWorkingTreeDiff);
    public const string GetMergeConflicts = nameof(GetMergeConflicts);
    public const string ApplyMergeStrategy = nameof(ApplyMergeStrategy);
    public const string SyncBaseBranch = nameof(SyncBaseBranch);
    public const string VerifyMerge = nameof(VerifyMerge);
    public const string RunWorktreeCommand = nameof(RunWorktreeCommand);
    public const string WriteWorktreeFiles = nameof(WriteWorktreeFiles);
    public const string RunSetupCommand = nameof(RunSetupCommand);
    public const string CleanupWorktree = nameof(CleanupWorktree);
    public const string RemoveRepoWorkspace = nameof(RemoveRepoWorkspace);
    public const string GetFileTree = nameof(GetFileTree);
    public const string GetFileContent = nameof(GetFileContent);
    public const string GetCommitLog = nameof(GetCommitLog);
    public const string ListBranches = nameof(ListBranches);
    public const string EnsureLocalBranch = nameof(EnsureLocalBranch);
    public const string CreateRepoBranch = nameof(CreateRepoBranch);
    public const string GetCommitDiff = nameof(GetCommitDiff);
    public const string ListTree = nameof(ListTree);
    public const string GetRemoteSyncStatus = nameof(GetRemoteSyncStatus);
    public const string PushToRemote = nameof(PushToRemote);
    public const string PublishRepoToGitHub = nameof(PublishRepoToGitHub);
    public const string AttachRemote = nameof(AttachRemote);
    public const string EnsureChatWorkspace = nameof(EnsureChatWorkspace);
    public const string CheckRepoAccess = nameof(CheckRepoAccess);
    public const string OpenPullRequest = nameof(OpenPullRequest);
    public const string GetPullRequestState = nameof(GetPullRequestState);
    /// <summary>A Story's pull request: draft → ready, or back to draft (capability <c>stories</c>).</summary>
    public const string MarkPullRequestReady = nameof(MarkPullRequestReady);
    /// <summary>Rewrites a Story pull request's description (capability <c>stories</c>).</summary>
    public const string UpdatePullRequestBody = nameof(UpdatePullRequestBody);
    /// <summary>A Story member's starting commit on the shared branch (capability <c>stories</c>).</summary>
    public const string ResolveReviewBase = nameof(ResolveReviewBase);
    public const string TrackerQuery = nameof(TrackerQuery);
    /// <summary>Write-only: tests a tracker token the developer typed into the settings page,
    /// stores it in the machine's secret store and answers with metadata only. The one
    /// host → runner message that carries a credential (see <see cref="AddTrackerConnectionRequest"/>).</summary>
    public const string AddTrackerConnection = nameof(AddTrackerConnection);
    public const string RemoveTrackerConnection = nameof(RemoveTrackerConnection);
    public const string SquashMergeIntoUserRepo = nameof(SquashMergeIntoUserRepo);
    public const string UtilityComplete = nameof(UtilityComplete);
    public const string StartAgentRun = nameof(StartAgentRun);
    public const string StartChatRun = nameof(StartChatRun);
    public const string CancelRun = nameof(CancelRun);
    /// <summary>Cancel every run the runner still has in flight for one task (request/
    /// response, returns the count). Addresses runs by TASK rather than by run id so a host
    /// replica that never started the run — after a deploy rolled the replica that did —
    /// can still stop it before touching its worktree. See
    /// <see cref="RunnerCapabilities.TaskCancel"/>.</summary>
    public const string CancelTaskRuns = nameof(CancelTaskRuns);
    public const string StartProviderLogin = nameof(StartProviderLogin);
    public const string OpenInEditor = nameof(OpenInEditor);
    public const string ListEditors = nameof(ListEditors);
    public const string StartAppRun = nameof(StartAppRun);
    public const string StopAppRun = nameof(StopAppRun);
    public const string GetAppRunStatus = nameof(GetAppRunStatus);
    public const string AttachAppRun = nameof(AttachAppRun);
    public const string WriteAppRunInput = nameof(WriteAppRunInput);
    public const string ResizeAppRun = nameof(ResizeAppRun);

    // Runner → host (hub methods)
    public const string Hello = nameof(Hello);
    public const string StreamRunChunks = nameof(StreamRunChunks);

    /// <summary>
    /// Interactive sessions: the agent's Bash hook runs inside the RUNNER, but the human who has
    /// to rule on a command is at a browser talking to the HOST. This is the only runner-initiated
    /// request that blocks on a person, so its timeout is a human's attention span rather than a
    /// network one.
    /// </summary>
    public const string RequestApproval = nameof(RequestApproval);

    /// <summary>
    /// Project-context tools: the agent's CLI runs inside the RUNNER, but the tools it calls
    /// (list_documents, get_task_history, …) answer from the HOST's database. The runner
    /// forwards each call here and relays the text back to the CLI. Short-lived reads —
    /// unlike RequestApproval, nothing on this path waits for a human.
    /// </summary>
    public const string CallProjectTool = nameof(CallProjectTool);

    /// <summary>Runner → host: renew this connection's token when it is close to expiry
    /// (<see cref="RunnerTokenRotation"/>). The runner stores the new one in the OS secret
    /// store and redials with it; the old one keeps working for a short grace period.</summary>
    public const string RotateToken = nameof(RotateToken);

    /// <summary>Runner → host: which ticket sources to poll now (takes the poll lease).</summary>
    public const string SyncTrackers = nameof(SyncTrackers);

    /// <summary>Runner → host: the tickets one poll found.</summary>
    public const string ReportTickets = nameof(ReportTickets);

    /// <summary>Runner → host: which status moves and pull-request checks from the last sync
    /// were done.</summary>
    public const string AckTransitions = nameof(AckTransitions);
}

/// <summary>One project-context tool the host offers a run, definitions included so the
/// runner never hardcodes a tool list that could drift from the host's.</summary>
/// <param name="TimeoutSeconds">How long the runner waits for the host to answer a call to
/// this tool before answering the agent itself. The HOST decides, because only the host knows
/// what its own tool does: a database read answers in milliseconds, while generating four
/// images took 400s in production. Null means the runner's default — which is also what an
/// older host sends, so the field is optional on purpose.</param>
public sealed record RunnerToolDefinition(
    string Name,
    string Description,
    string InputSchemaJson,
    int? TimeoutSeconds = null);

/// <summary>A tool call the runner's agent made, forwarded to the host to answer.</summary>
/// <param name="TaskId">The host's registry key for the run: a task id for pipeline runs,
/// the chat RUN id for agent-chat turns. A lookup key only — the hub authorizes against the
/// connection's own org, never against this value.</param>
public sealed record RunnerToolCallRequest(
    string TaskId,
    string Tool,
    string ArgumentsJson);

/// <summary>The host's answer. Failures are error RESULTS, never exceptions — the agent
/// should read "the tool failed" and continue, not lose its run to a transport fault.</summary>
public sealed record RunnerToolCallResult(string Text, bool IsError);

/// <summary>A command an interactive session is asking a human to rule on.</summary>
public sealed record RunnerApprovalRequest(
    string TaskId,
    string? ExecutionId,
    string ToolUseId,
    string Tool,
    string Command,
    string ApprovalKey);

/// <summary>The ruling. <paramref name="Outcome"/> is an
/// <c>ApprovalOutcome</c> name: Allowed | AllowedForSession | Denied | Expired. Anything the
/// runner cannot parse is treated as a refusal.</summary>
public sealed record RunnerApprovalResult(string Outcome);

/// <summary>
/// CLI keys for the vendor CLIs the runner can drive, plus the mapping from AiProvider
/// enum names (kept as strings — this assembly stays dependency-free).
/// </summary>
public static class RunnerProviders
{
    public const string Claude = "claude";
    public const string Codex = "codex";
    public const string Gemini = "gemini";
    public const string Copilot = "copilot";
    // Not a subscription CLI: a local Ollama server driven agentically through the codex
    // CLI. "Ready" means the server is reachable AND the codex CLI is installed.
    public const string Ollama = "ollama";
    // xAI's Grok Build CLI. A normal subscription CLI (SuperGrok / X Premium Plus), except
    // that it also accepts a plain XAI_API_KEY — so a saved Settings key counts as auth.
    public const string Grok = "grok";
    // The inverse of Ollama: a harness with no models. "Ready" means the CLI is installed
    // AND at least one provider is authenticated (`opencode auth login`), because an
    // OpenCode with no logins can start but cannot answer.
    public const string OpenCode = "opencode";

    public static readonly string[] All = [Claude, Codex, Gemini, Copilot, Ollama, Grok, OpenCode];

    /// <summary>Providers whose selectable models are discovered on the runner (reported in
    /// <see cref="RunnerProviderStatus.Models"/>) instead of coming from the static catalog.
    /// Ollama reports pulled tags; OpenCode reports the models its authenticated providers
    /// expose.</summary>
    public static readonly string[] DiscoversModels = [Ollama, OpenCode];

    /// <summary>Maps an AiProvider enum name ("Anthropic" | "OpenAI" | "Google" |
    /// "GitHubCopilot" | "Ollama" | "XAI" | "OpenCode") to the CLI key. Unknown names fall
    /// back to claude, matching provider-resolution fallback.</summary>
    public static string FromAiProviderName(string? name) => name?.ToLowerInvariant() switch
    {
        "openai" => Codex,
        "google" => Gemini,
        "githubcopilot" => Copilot,
        "ollama" => Ollama,
        "xai" => Grok,
        "opencode" => OpenCode,
        _ => Claude,
    };
}

/// <summary>Sent by the runner immediately after connecting.
/// <see cref="ClaudeCliFound"/>/<see cref="ClaudeAuthenticated"/> mirror the claude entry
/// in <see cref="Providers"/> for existing log/UI consumers. <see cref="Capabilities"/>
/// is additive feature-negotiation: the host only uses newer op semantics when the
/// runner advertises them (null = old runner, baseline behavior).</summary>
public sealed record RunnerHello(
    int ProtocolVersion,
    string MachineName,
    bool ClaudeCliFound,
    bool ClaudeAuthenticated,
    string[] ActiveRunIds,
    RunnerProviderStatus[] Providers,
    string[]? Capabilities = null,
    // Additive: the daemon's release version (informational — protocol compatibility is
    // decided by ProtocolVersion alone). Surfaced in status UIs and update nudges.
    string? DaemonVersion = null,
    // Additive: random id minted once per runner process. Lets the host tell a reconnect
    // of the SAME runner apart from a SECOND runner process silently competing for the
    // single link slot — with two runners alternating Hellos, ops land on whichever spoke
    // last and failures look like "the runner never received the op". Old runners send
    // null and keep today's indistinct replace behavior.
    string? InstanceId = null,
    // Additive: which GitHub account this machine pushes as. Null from older runners.
    RunnerGitIdentity? GitIdentity = null,
    // Which forge CLIs (gh, az, glab) are installed and signed in — what opens the developer's
    // pull requests. Null from older runners, and on a Hello sent before the first probe.
    RunnerForgeCliStatus[]? ForgeClis = null,
    // The tracker connections this machine holds — metadata only, never a token. Null from
    // runners older than tracker support; empty when none has been added.
    RunnerTrackerConnection[]? Trackers = null,
    // Repository commands this machine refused (not in orkka.json, not approved here), most
    // recent first, at most 20 — what the Machine page lists with the `./orkka commands
    // approve <id>` to run. Approval itself stays on the machine's terminal.
    CommandApproval[]? PendingCommandApprovals = null);

/// <summary>
/// One forge CLI on the developer's machine. Orkka opens pull requests with it, under the
/// developer's own account — so "installed but not signed in" is a publish that will fail,
/// and the settings page says so before a task gets there.
/// </summary>
/// <param name="Cli">"gh", "az" or "glab".</param>
/// <param name="Accounts">The signed-in accounts the CLI reports, when it reports them
/// (gh lists one per host). Logins only, never tokens.</param>
/// <param name="Detail">Why it is not ready, in the CLI's own terms, or a caveat (the Azure
/// CLI without its azure-devops extension cannot open pull requests).</param>
public sealed record RunnerForgeCliStatus(
    string Cli, bool Found, bool Authenticated, string[]? Accounts = null, string? Detail = null);

/// <summary>Capability keys a runner may advertise in <see cref="RunnerHello.Capabilities"/>.</summary>
public static class RunnerCapabilities
{
    /// <summary>The runner honors <c>RunnerUtilityRequest.Provider</c> (utility calls can
    /// run on codex/gemini, not only claude).</summary>
    public const string UtilityProvider = "utility-provider";

    /// <summary>The runner honors <c>RunnerAgentRunRequest.ResumeSessionId</c> (pipeline
    /// runs can resume a previous phase's claude session). The host must NOT slim the
    /// prompt for runners without this — they'd run cold with missing context.</summary>
    public const string AgentResume = "agent-resume";

    /// <summary>The runner supports the <c>WriteWorktreeFiles</c> op (host materializes
    /// task context as files under .orkka/context/ instead of inlining it in prompts).</summary>
    public const string ContextFiles = "context-files";


    /// <summary>The runner supports <c>SyncBaseBranch</c> — merging the base branch into a
    /// task worktree so the task stops working against the commit it forked from. Without it
    /// the host skips the sync and the branch only meets the base at merge time, which is the
    /// stale-base behavior this capability exists to end.</summary>
    public const string BaseSync = "base-sync";

    /// <summary>The runner supports <c>VerifyMerge</c> — checking that a resolved merge is
    /// genuinely finished (nothing unmerged, no conflict markers committed, base actually
    /// merged) before it is pushed. Without it the host pushes on the resolver's own word,
    /// which is the pre-verification behavior.</summary>
    public const string MergeVerify = "merge-verify";

    /// <summary>The runner supports <c>GetWorkingTreeDiff</c> — diffing the worktree as it
    /// stands (uncommitted and untracked included) so the Code View can show a run's changes
    /// while it happens. Without it the host skips live snapshots; the browser's own
    /// tool-call preview and the end-of-run snapshot still work.</summary>
    public const string WorkingTreeDiff = "working-tree-diff";

    /// <summary>The runner's non-Claude CLI invokers honor resume session ids (codex
    /// today; copilot scaffolded). Without this, the host must keep sending the full
    /// system prompt + history on every chat turn for those providers.</summary>
    public const string CliResume = "cli-resume";

    /// <summary>The runner speaks the hybrid-mode dialect: forge clone URLs in
    /// EnsureBareClone and RunnerTokens.UserRepo tokens on the user-repo ops. A hosted
    /// (multi-tenant) runner hub refuses runners without this capability.</summary>
    public const string Hybrid = "hybrid";

    /// <summary>The runner vets every repository command (setup, check, build, run targets)
    /// against the repository's orkka.json on its default branch and the developer's approvals
    /// on the machine, and reports a refused one as a <see cref="CommandApproval"/>. The host
    /// sends no command at all to a runner without it.</summary>
    public const string CommandTrust = "command-trust";

    /// <summary>The runner supports the app-run ops (<c>StartAppRun</c> / <c>StopAppRun</c> /
    /// <c>GetAppRunStatus</c> / <c>WriteAppRunInput</c> / <c>ResizeAppRun</c>) — starting the
    /// user's own project on their machine from the Run button. Output rides the regular
    /// <c>StreamRunChunks</c> channel as "Pty" chunks, one channel per target. Without this
    /// capability the UI hides the Run button rather than failing the call.</summary>
    public const string AppRun = "app-run";

    /// <summary>The runner's app-run has the prepare step: `.env` provisioned from
    /// `.env.example`, <c>InstallCommand</c> run when dependencies are missing, the Docker
    /// probe for infra targets, dependency port readiness, <c>StopCommand</c>, and the
    /// <c>Ready</c> flag in status. Purely informational for the host — the fields are
    /// additive and an older runner simply ignores them.</summary>
    public const string AppRunPrepare = "app-run-prepare";

    /// <summary>The runner supports <c>ListEditors</c> / <c>OpenInEditor</c> — opening a
    /// task's worktree in a desktop editor installed on the user's machine. Without this
    /// capability the UI hides the "open in editor" action rather than failing the call.</summary>
    public const string Editor = "editor";

    /// <summary>The runner honors <c>RunnerAgentRunRequest.ProjectTools</c>: it registers an
    /// in-process MCP server on the spawned CLI and forwards each tool call back over
    /// <c>CallProjectTool</c>. Without this, the host must not send tool definitions — a
    /// prompt that advertises tools the run doesn't have teaches the agent its tools are
    /// broken.</summary>
    public const string ProjectTools = "project-tools";

    /// <summary>The runner honors <c>RunnerChatRunRequest.ProjectTools</c> — the same
    /// forwarding as <see cref="ProjectTools"/>, for AGENT CHAT turns relayed to this
    /// machine. Without it a relayed turn has no platform tools and the host must fall back
    /// to the fenced-block prompt: the agent writes ```document JSON into its reply and the
    /// host parses it out. That fallback is what a 26-document consolidation hit the model's
    /// output ceiling in the middle of on 2026-09-07, saving nothing — a tool call per
    /// document has no such ceiling.</summary>
    public const string ChatTools = "chat-tools";

    /// <summary>The runner supports the local-folder ops over the hybrid relay:
    /// <c>ListDirectory</c> / <c>ValidateRepoPath</c> / <c>InitLocalRepo</c> /
    /// <c>InitRepoInPlace</c> / <c>AttachRemote</c>. This is what lets a HOSTED project link
    /// a folder that only exists on the user's machine, git-init it, and later give it an
    /// origin. Without this capability the web UI hides the "add a local folder" action
    /// rather than failing the call.</summary>
    public const string LocalFolders = "local-folders";

    /// <summary>The runner supports <c>CancelTaskRuns</c> and, independently, refuses to
    /// delete a worktree out from under a live run: <c>CleanupWorktree</c> first cancels the
    /// task's in-flight runs and waits for their agent processes to exit. Without this the
    /// host can only address runs it started itself (CancelRun by run id), so a worktree
    /// cleanup issued by a different replica may race an agent still reading the tree.</summary>
    public const string TaskCancel = "task-cancel";

    /// <summary>The runner supports <c>CheckRepoAccess</c> — asking git on this machine
    /// whether it can actually reach a set of remotes, without cloning. This is the only
    /// preflight for the precondition nothing else states: a runner clones unmanaged repos
    /// under the USER's own git credentials, so a machine with none fails every task and
    /// every chat turn for those repos, and until it is asked, nobody finds out until a run
    /// has already failed. Without the capability the UI hides the check rather than
    /// failing it.</summary>
    public const string RepoAccessCheck = "repo-access-check";

    /// <summary>The runner runs every git command through the platform engine with the
    /// developer's own credentials and identity (their credential manager, SSH setup and git
    /// config), and honours <c>SyncBaseBranchRequest.CommitLeftovers</c>. A host refuses git
    /// work on a runner without it rather than degrading.</summary>
    public const string DeveloperGit = "developer-git";

    /// <summary>The runner supports <c>OpenPullRequest</c> / <c>GetPullRequestState</c>:
    /// opening and reading pull requests with the developer's own forge CLI (gh, az repos,
    /// glab). Without it the host cannot publish a reviewed task to the forge.</summary>
    public const string ForgeCli = "forge-cli";

    /// <summary>The runner holds tracker connections (Jira Cloud, Azure DevOps Services), reports
    /// their metadata in its Hello, and answers <c>TrackerQuery</c> — what the project's
    /// "Ticket source" settings are built from. The tokens never leave the machine.</summary>
    public const string TrackerConfig = "tracker-config";

    /// <summary>The runner polls the ticket sources the host assigns it (<c>SyncTrackers</c> /
    /// <c>ReportTickets</c>) and copies a ticket's cached attachments into a run's worktree
    /// (<see cref="RunnerAgentRunRequest.TicketAttachments"/>).</summary>
    public const string TrackerPoll = "tracker-poll";

    /// <summary>The runner answers <c>DiscoverRepos</c> (a project's base folder: a monorepo, or
    /// the repositories in it), seeds a first mirror from
    /// <c>EnsureBareCloneRequest.LocalCheckoutPath</c>, and honours
    /// <c>ReadOnlyDirs</c> on agent and chat runs. Without it the UI cannot create a project from
    /// a folder and says to run an update (<c>git pull</c>, then <c>./orkka start</c>).</summary>
    public const string RepoDiscovery = "repo-discovery";

    /// <summary>The runner answers <c>AddTrackerConnection</c> / <c>RemoveTrackerConnection</c>:
    /// tracker connections added from the settings page, stored in its OS secret store.</summary>
    public const string TrackerConnect = "tracker-connect";

    /// <summary>The runner answers the <c>boards</c> tracker query and honours a board on a
    /// preview and on a poll (<see cref="TrackerSyncFeatures.Boards"/>): Jira boards (their filter
    /// is AND-ed onto intake), Azure DevOps teams (their area paths). Without it the settings page
    /// says to update Orkka instead of offering boards.</summary>
    public const string TrackerBoards = "tracker-boards";

    /// <summary>Stories: <c>OpenPullRequestRequest.Draft</c> (<c>gh pr create --draft</c>, <c>az repos
    /// pr create --draft true</c>, <c>glab mr create --draft</c>), <c>MarkPullRequestReady</c>
    /// (and its undo), <c>UpdatePullRequestBody</c>, <c>ResolveReviewBase</c> and
    /// <c>GetDiffRequest.BaseSha</c>. Without it a Story's draft pull request is refused with
    /// "update Orkka" rather than opened as a ready one.</summary>
    public const string Stories = "stories";
}

/// <summary>One remote to probe. RepoId is opaque to the runner — it comes back on the
/// matching result so the host can line answers up with its own repos. The probe uses the
/// machine's own git credentials, exactly as a real clone does.</summary>
public sealed record RepoAccessProbe(string RepoId, string GitUrl);

/// <summary>Asks git on the runner's machine whether it can read each remote with the
/// developer's own credentials — the ones every real run uses.</summary>
public sealed record CheckRepoAccessRequest(string OrgId, RepoAccessProbe[] Repos);

/// <param name="Error">Git's own message when <paramref name="Ok"/> is false. Raw on
/// purpose — the host classifies it (RepoAccessDiagnostics) so one vocabulary covers the
/// clone failures this predicts.</param>
public sealed record RepoAccessResult(string RepoId, bool Ok, string? Error);

public sealed record CheckRepoAccessResponse(RepoAccessResult[] Results);

/// <summary>CLI + subscription-auth state for one AI vendor CLI on the runner.
/// Provider is a lowercase CLI key: "claude" | "codex" | "gemini" | "copilot" | "ollama" |
/// "grok" | "opencode".
/// Both trailing properties are additive (older runners/hosts interop as null):
/// <see cref="Models"/> lists the models this machine can actually run, as namespaced
/// catalog ids. Only the <see cref="RunnerProviders.DiscoversModels"/> providers report it:
/// Ollama sends whatever `ollama list` holds as "ollama/&lt;model:tag&gt;", and OpenCode —
/// which ships no models of its own — sends what its authenticated providers expose as
/// "opencode/&lt;provider&gt;/&lt;model&gt;". <see cref="Detail"/> is an actionable, user-facing reason
/// the provider isn't ready (shown in the UI and in run blockers) when plain
/// installed/authenticated booleans can't express it.
/// The last three are additive too (older runners send null): <see cref="AuthMode"/> is HOW the
/// CLI signs in on this machine for the Hello's workspace — one of <see cref="ProviderAuthModes"/>
/// — and <see cref="AuthDetail"/> a non-secret qualifier (a region, a gateway or enterprise host,
/// the variable name of a token; never a value that authenticates). <see cref="CliVersion"/> is
/// the installed CLI's own version string.</summary>
public sealed record RunnerProviderStatus(
    string Provider,
    bool CliFound,
    bool Authenticated,
    string[]? Models = null,
    string? Detail = null,
    string? AuthMode = null,
    string? AuthDetail = null,
    string? CliVersion = null);

/// <summary>Values of <see cref="RunnerProviderStatus.AuthMode"/>.</summary>
public static class ProviderAuthModes
{
    /// <summary>The CLI's own stored sign-in (claude /login, codex login, copilot login, gh).</summary>
    public const string Login = "login";
    /// <summary>An API key (env, apiKeyHelper, or one the CLI stored).</summary>
    public const string ApiKey = "api-key";
    public const string Bedrock = "bedrock";
    public const string Vertex = "vertex";
    public const string Foundry = "foundry";
    public const string AzureOpenAi = "azure-openai";
    /// <summary>A company LLM gateway / proxy base URL.</summary>
    public const string Gateway = "gateway";
    /// <summary>A long-lived token in the environment (claude setup-token, a GitHub token).</summary>
    public const string Token = "token";
    /// <summary>GitHub Enterprise Cloud with data residency (Copilot).</summary>
    public const string EnterpriseHost = "enterprise-host";
    public const string None = "none";
}

/// <summary>
/// Which GitHub account this machine pushes as. On a runner, git auth comes from the developer's
/// own credential helper — so a wrong identity is invisible until an agent finishes its work and
/// fails at the push. Logins only; never tokens.
/// </summary>
/// <param name="DefaultAccount">Account the machine's `gh` login holds, used when no per-URL
/// username is configured. Null when nothing is logged in.</param>
/// <param name="ScopedPerRepoPath">credential.useHttpPath — when false, ONE credential is shared
/// across all of github.com, which is what makes a personal repo push as a work account.</param>
/// <param name="Repos">Per-repo resolution, from git's own config matcher.</param>
/// <param name="Detail">Actionable reason the setup will misbehave, or null when healthy.</param>
public sealed record RunnerGitIdentity(
    string? DefaultAccount,
    bool ScopedPerRepoPath,
    RunnerRepoGitIdentity[] Repos,
    string? Detail = null);

/// <param name="Account">Null = no per-URL username configured, so this repo falls back to
/// <see cref="RunnerGitIdentity.DefaultAccount"/>.</param>
public sealed record RunnerRepoGitIdentity(string RepoUrl, string? Account);

public sealed record RepoPathInfo(
    bool Exists,
    bool IsGitRepo,
    string? DefaultBranch,
    bool IsDirty,
    string? FolderName,
    string? Error);

/// <summary>Generic op result for git operations (mirrors SharedKernel Result without
/// referencing it — this assembly stays dependency-free).</summary>
public sealed record RunnerOpResult(bool Success, string? Value, string? Error)
{
    public static RunnerOpResult Ok(string? value = null) => new(true, value, null);
    public static RunnerOpResult Fail(string error) => new(false, null, error);
}

public sealed record RunnerFileNode(string Path, string Type, long Size);

/// <summary>One prompt-context file to materialize inside a task worktree (see
/// <see cref="RunnerCapabilities.ContextFiles"/>). RelativePath is relative to the
/// worktree's .orkka/context/ folder — never absolute, never escaping it.</summary>
public sealed record RunnerContextFile(string RelativePath, string Content);

public sealed record WriteWorktreeFilesRequest(
    string OrgId, string RepoId, string TaskId, RunnerContextFile[] Files);

/// <summary>Agent run request. The runner runs each vendor CLI on the developer's own CLI
/// sign-in; no provider credential ever travels from the host. Provider travels as the AiProvider enum name
/// ("Anthropic" | "OpenAI" | "Google"), keeping this assembly dependency-free.</summary>
public sealed record RunnerAgentRunRequest(
    string RunId,
    string SystemPrompt,
    string UserMessage,
    string WorktreeToken,
    string Provider,
    string? Model,
    int MaxTokens,
    int MaxTurns,
    string Phase,
    string AgentName,
    string AgentRole,
    string OrgId,
    string ProjectId,
    string? TaskId,
    string? AgentId,
    string? ExecutionId,
    // Additive (see RunnerCapabilities.AgentResume): claude session id of this task's
    // previous run in the same lane. Older runners ignore it and run fresh.
    string? ResumeSessionId = null,
    // Additive per-phase run ceilings set by the host's PhaseBudgets (heavy Developing /
    // scaffold runs get more than a PM refine). Null → the runner uses its own
    // AgentLimitsOptions default, so older hosts and newer runners both behave as before.
    int? MaxToolCalls = null,
    int? MaxWallClockSeconds = null,
    // Additive: this run is a turn of an interactive session, so its Bash guard may ask the human
    // (through RequestApproval) instead of only allowing or refusing. Defaults false, so an older
    // runner that ignores it simply keeps today's allow/deny behaviour rather than misreading it
    // as permission to prompt nobody.
    bool Interactive = false,
    // Additive (see RunnerCapabilities.ProjectTools): the project-context tools this run may
    // call, forwarded back over CallProjectTool. Null/empty = no tool server. Only sent to
    // runners that advertise the capability, so older runners never see it.
    RunnerToolDefinition[]? ProjectTools = null,
    // Additive: this Testing run verifies an HTTP API, not a browser UI — the runner swaps the
    // QA tool set (no Playwright server, still no Write/Edit) to match the prompt the host
    // built. Defaults false, so an older runner spawns a browser it will not use rather than
    // failing the run.
    bool ApiTesting = false,
    // Additive (see RunnerCapabilities.TrackerPoll): the ticket whose attachments, cached on
    // this machine by the poller, are copied into the worktree's .orkka/context/attachments/
    // before the agent starts. Null for tasks not imported from a tracker.
    TrackerTicketRef? TicketAttachments = null,
    // Additive (see RunnerCapabilities.RepoDiscovery): folders the agent may READ besides its
    // worktree — the project's base folder for a folder-of-repos project, so it can look at the
    // sibling repositories. Claude and Copilot get `--add-dir`; writes stay confined to the
    // worktree by the boundary guard. Null/empty = nothing extra.
    string[]? ReadOnlyDirs = null);

/// <summary>ResumeSessionId (additive, defaults null) carries the claude CLI session id of
/// the conversation's previous turn so the runner resumes the warm session instead of
/// cold-booting. Older runners ignore the property and just run fresh; they also never
/// report a session id back, so a newer host never asks them to resume.</summary>
public sealed record RunnerChatRunRequest(
    string RunId,
    string WorkspaceToken,
    string SystemPrompt,
    string UserMessage,
    string Provider,
    string Model,
    string? ResumeSessionId = null,
    // Additive (see RunnerCapabilities.ChatTools): this turn's platform tools. The runner
    // registers them as an MCP server on the spawned CLI and forwards each call back over
    // CallProjectTool, keyed by RunId. Older runners ignore the field — and the host only
    // sends it to runners that advertise the capability, because a prompt that advertises
    // tools the run cannot call teaches the agent its tools are broken.
    RunnerToolDefinition[]? ProjectTools = null,
    // Additive: folders the chat agent may read besides its workspace (the project's base
    // folder for a folder-of-repos project). See RunnerAgentRunRequest.ReadOnlyDirs.
    string[]? ReadOnlyDirs = null);

/// <summary>See <see cref="RunnerProtocol.CancelTaskRuns"/>. The result's Value is the number
/// of runs that were cancelled (as a string); zero is a perfectly good answer — the run may
/// already have finished, or never reached this runner.</summary>
public sealed record CancelTaskRunsRequest(string TaskId);

public sealed record RunnerChunk(
    string Type,
    string Content,
    int PromptTokens = 0,
    int CompletionTokens = 0,
    int CacheReadTokens = 0,
    int CacheWriteTokens = 0,
    string? SessionId = null,
    // Additive and optional: a runner older than this field simply omits it and the host records
    // null, which is the same as any non-Claude provider. See AgentChunk.VendorCostUsd.
    decimal? VendorCostUsd = null);

/// <summary>One-shot utility call (titles, summaries, decomposition). Provider is ANY
/// AiProvider enum name — the executor parses the full enum and runs the matching vendor
/// CLI, so a Grok-only or Copilot-only machine serves these too. Null means Anthropic —
/// the pre-multi-provider behavior, so old hosts/runners interop unchanged.</summary>
public sealed record RunnerUtilityRequest(
    string Model,
    string? System,
    string UserMessage,
    int MaxTokens,
    string? Provider = null);

public sealed record RunnerUtilityResult(
    string? Text,
    int InputTokens,
    int OutputTokens,
    int CacheReadTokens,
    int CacheWriteTokens,
    string? Error);

/// <param name="Branch">Additive: the branch to check out when it exists on the remote (a task's
/// Discuss chat reads the task branch). Null = the default branch.</param>
public sealed record ChatWorkspaceRepoDto(
    string RepoId, string Name, string? DefaultBranch, string? Branch = null);

/// <summary>
/// Release-version arithmetic for the runner daemon. Lives beside the wire contract
/// because both ends compare the same numbers: the runner decides whether to self-update
/// on start, and the host (local and cloud) decides whether to show an update nudge.
/// Three copies of "is this semver older" is how they end up disagreeing.
/// </summary>
public static class RunnerVersions
{
    /// <summary>Release tags look like <c>local-v0.3.21</c>; strips the prefix.</summary>
    public const string TagPrefix = "local-v";

    public static string FromTag(string tag) =>
        tag.StartsWith(TagPrefix, StringComparison.Ordinal) ? tag[TagPrefix.Length..] : tag;

    /// <summary>
    /// True when <paramref name="current"/> is strictly older than <paramref name="latest"/>.
    /// Anything unparseable on either side is false: an unreadable version is not evidence
    /// of being behind, and guessing would either nag users forever or let a malformed
    /// release overwrite a working install.
    /// </summary>
    public static bool IsNewer(string? current, string? latest)
    {
        if (string.IsNullOrWhiteSpace(current) || string.IsNullOrWhiteSpace(latest))
            return false;

        // Informational versions may carry suffixes ("0.4.0+abc123") — compare the
        // leading numeric triplet only.
        static string Lead(string value)
        {
            var end = value.IndexOfAny(['+', '-', ' ']);
            return end < 0 ? value : value[..end];
        }

        return Version.TryParse(Lead(current), out var running)
            && Version.TryParse(Lead(latest), out var published)
            && running < published;
    }
}

/// <summary>Answer to <see cref="RunnerProtocol.RotateToken"/>: a new raw <c>orkr_</c> token when
/// the presented one was due (null otherwise), and when the token the runner should hold
/// expires.</summary>
public sealed record RunnerTokenRotation(string? NewToken, DateTimeOffset ExpiresAt);
