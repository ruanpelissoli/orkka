namespace Orkka.LocalRunner.Contracts;

// Parameter records for host→runner client-result invocations. One record per op keeps
// the SignalR method signatures stable (single argument) and versionable.

public sealed record ValidateRepoPathRequest(string Path);

/// <summary>Creates a brand-new git repository (dir + `git init` + initial commit) under
/// ParentPath on the runner's machine — the "create repo" flow in the repos UI.</summary>
public sealed record InitLocalRepoRequest(string ParentPath, string Name);

/// <param name="ExcludedPaths">What the import deliberately kept out of the repository —
/// dependency folders, build output, .env. Left on disk, but outside the project's history,
/// which is precisely the thing a user has to be told rather than discover.</param>
/// <param name="SnapshotPath">Where the folder was copied before anything changed. Null when
/// no copy was needed (an existing repository already has its own history) or possible.</param>
public sealed record InitLocalRepoResult(
    bool Success, string? Path, string? DefaultBranch, string? Error,
    string[]? ExcludedPaths = null, string? SnapshotPath = null);

/// <summary>Turns an EXISTING directory on the runner's machine into a usable git
/// repository: `git init` if needed, then an initial commit of its current contents so
/// the bare mirror has something to clone. A no-op when the repo already has commits —
/// the "link a plain folder" flow in the repos UI.</summary>
public sealed record InitRepoInPlaceRequest(string Path);

/// <summary>Null/empty path lists the filesystem roots (drives on Windows, / and ~ on
/// Unix) so the folder picker has a starting point.</summary>
public sealed record ListDirectoryRequest(string? Path);

public sealed record DirectoryEntry(string Name, string FullPath, bool IsGitRepo);

public sealed record DirectoryListing(
    string? Path,
    string? ParentPath,
    DirectoryEntry[] Entries,
    string? Error);

/// <summary>CloneUrlOrPath is either a git remote URL (cloned with the developer's own git
/// credentials) or a local directory path on the runner's machine (a linked folder). The
/// runner treats both as a git remote. No credential travels: the runner's git uses the
/// developer's own sign-in.</summary>
/// <param name="LocalCheckoutPath">Additive: the developer's own checkout of this repository in
/// a project's base folder. The runner seeds a first mirror of a remote from it (no network for
/// the bulk of the objects) and keeps the repo's task folders under its own cache, never next to
/// the checkout. Null for repos that were not discovered from a base folder.</param>
public sealed record EnsureBareCloneRequest(
    string OrgId, string RepoId, string CloneUrlOrPath, string? LocalCheckoutPath = null);

/// <summary>Discovers the git repositories in a base folder on the runner's machine: the folder
/// itself (a monorepo), or the repositories up to two levels below it. Read-only — the runner
/// never runs <c>git init</c> or writes anything here.</summary>
public sealed record DiscoverReposRequest(string Path);

/// <summary>One repository found in a base folder.</summary>
/// <param name="RelativePath">Relative to the base folder, forward slashes; "" for a monorepo.</param>
/// <param name="OriginUrl">The <c>origin</c> remote with any embedded credential stripped; null
/// when the repository has no origin (it is then worked as a local folder).</param>
/// <param name="DefaultBranch"><c>origin/HEAD</c>'s branch, else the current branch.</param>
/// <param name="HasUnpushedCommits">Commits on the current branch that its upstream (or, with no
/// upstream, any origin branch) does not have.</param>
/// <param name="Description">From <c>orkka.json</c>'s <c>description</c>, else the README's first
/// paragraph (at most 300 characters); null when neither has one.</param>
/// <param name="Stack">A cheap stack hint (a RepoStacks name) when one is recognisable.</param>
public sealed record DiscoveredRepo(
    string RelativePath,
    string Name,
    string? OriginUrl,
    string? DefaultBranch,
    string? CurrentBranch,
    bool IsDirty,
    bool HasUnpushedCommits,
    bool HasCommits,
    string? Description,
    string? Stack);

/// <param name="Shape"><see cref="DiscoveredShapes"/>; null when discovery failed.</param>
/// <param name="Areas">Monorepo only: its top-level directories.</param>
public sealed record DiscoverReposResult(
    string? Path,
    string? Shape,
    DiscoveredRepo[] Repos,
    string[] Areas,
    string? Error);

public static class DiscoveredShapes
{
    public const string Monorepo = "monorepo";
    public const string MultiRepo = "multirepo";
}

public sealed record CreateWorktreeRequest(
    string OrgId, string RepoId, string TaskId, string BranchName, string BaseBranch,
    string? WorktreeName = null);

public sealed record CommitAndPushRequest(
    string WorktreeToken, string Message, string BranchName);

/// <summary>Pushes the task branch with the developer's own git credentials.</summary>
public sealed record EnsurePushedRequest(
    string OrgId, string RepoId, string TaskId, string BranchName);

public sealed record RemoteBranchExistsRequest(string OrgId, string RepoId, string BranchName);

public sealed record GetDiffRequest(
    string OrgId, string RepoId, string TaskId, string BaseBranch, int MaxChars,
    // Enables the bare-mirror fallback when the worktree no longer exists.
    string? HeadBranch = null,
    // A Story member's starting commit: diff BaseSha..HEAD (that member's change only) instead
    // of origin/{BaseBranch}...HEAD. Capability "stories"; older runners ignore it.
    string? BaseSha = null);

public sealed record GetWorkingTreeDiffRequest(
    string OrgId, string RepoId, string TaskId, string BaseBranch, int MaxChars,
    // A Story member's review base: diff the worktree against it, not the branch point with
    // origin/{BaseBranch}. Capability "stories"; older runners ignore it.
    string? BaseSha = null);

public sealed record GetMergeConflictsRequest(
    string OrgId, string RepoId, string HeadBranch, string BaseBranch);

/// <summary>ChoicesJson: JSON object of path → "task" | "current" | "ai" (perFile only).</summary>
public sealed record ApplyMergeStrategyRequest(
    string OrgId, string RepoId, string TaskId, string BaseBranch,
    string Strategy, string? ChoicesJson);

/// <summary>Merges origin/{BaseBranch} into the task worktree. LeaveConflictsInProgress=true
/// keeps a conflicted merge staged for the agent that is about to run; false rolls it back.
/// CommitLeftovers (additive): commit whatever the previous run left uncommitted before
/// merging — set at phase boundaries, never for the agent's own mid-run sync.</summary>
public sealed record SyncBaseBranchRequest(
    string OrgId, string RepoId, string TaskId, string BaseBranch, bool LeaveConflictsInProgress,
    bool CommitLeftovers = false);

/// <summary>Read-only check that a resolved merge in the task worktree is actually finished.</summary>
public sealed record VerifyMergeRequest(
    string OrgId, string RepoId, string TaskId, string BaseBranch);

/// <summary>Runs a one-off repository command (the quality gate, or a merge build) in a task
/// worktree and reports how it went. Never cached — unlike RunSetupCommand, the caller is
/// asking about the worktree as it is right now. The runner runs it only when the repository's
/// orkka.json on its default branch declares it or the developer approved it on the machine;
/// otherwise the result carries <see cref="RunWorktreeCommandResult.Approval"/>.</summary>
public sealed record RunWorktreeCommandRequest(
    string OrgId, string RepoId, string TaskId, string Command, int TimeoutSeconds, CommandKind Kind);

/// <param name="Approval">Set when the command did not run because the developer has not
/// approved it on the machine — the host tells them how.</param>
public sealed record RunWorktreeCommandResult(
    int ExitCode, string? Output, double DurationSeconds, string? Error, CommandApproval? Approval = null);

/// <summary>Runs the repo's setup command in a task worktree so the app can actually start
/// (dependencies, env). Same vouching rule as <see cref="RunWorktreeCommandRequest"/>.</summary>
public sealed record RunSetupCommandRequest(
    string OrgId, string RepoId, string TaskId, string Command);

/// <summary>Skipped = the marker showed this exact command already succeeded here.</summary>
public sealed record RunSetupCommandResult(
    bool Skipped, int ExitCode, string? Output, double DurationSeconds, string? Error,
    CommandApproval? Approval = null);

public sealed record CleanupWorktreeRequest(string OrgId, string RepoId, string TaskId);

public sealed record RemoveRepoWorkspaceRequest(string OrgId, string RepoId);

public sealed record GetFileTreeRequest(string OrgId, string RepoId, string Branch);

public sealed record GetFileContentRequest(string OrgId, string RepoId, string Branch, string FilePath);

/// <summary>ProjectId scopes the workspace per project so one project's chat never sees
/// another project's worktrees — additive with a default so older runners/hosts interop
/// without a protocol bump (null falls back to the legacy org-level workspace).
/// TaskId (additive, needs ProjectId) asks for a task's Discuss workspace instead: separate from
/// the project's chat workspace and from the task's own worktree, each repo detached at its
/// <see cref="ChatWorkspaceRepoDto.Branch"/> when that exists on the remote. An older runner
/// ignores it and answers with the project token — the chat then reads the default branch.</summary>
public sealed record EnsureChatWorkspaceRequest(
    string OrgId, ChatWorkspaceRepoDto[] Repos, string? ProjectId = null, string? TaskId = null);

/// <summary>Squash-merges an agent branch into the user's working repository. The branch
/// already lives there (agents push to the bare cache, whose origin IS the user's repo);
/// preconditions (clean tree, base branch checked out) are enforced runner-side.
/// RepoToken is a <see cref="RunnerTokens.UserRepo"/> token — the runner resolves the
/// real path; no filesystem path crosses the wire.</summary>
public sealed record SquashMergeRequest(
    string RepoToken, string HeadBranch, string BaseBranch, string CommitMessage);

public sealed record RunnerFileTreeResult(bool Success, string[]? Paths, string? Error);

public sealed record GetCommitLogRequest(
    string OrgId, string RepoId, string Branch, int Skip, int Take, string? FilePath);

public sealed record ListBranchesRequest(string OrgId, string RepoId);

/// <summary>Materializes a remote-only branch as a local ref in the user's repository
/// (`git branch <name> origin/<name>` — never touches the working tree), then refreshes
/// the bare mirror so the branch is usable as a worktree/diff base.</summary>
public sealed record EnsureLocalBranchRequest(string OrgId, string RepoId, string Branch);

/// <summary>Creates a new branch ref in the user's repository from an existing branch
/// (local or remote-tracking) — never touches the working tree.</summary>
public sealed record CreateRepoBranchRequest(
    string OrgId, string RepoId, string NewBranch, string BaseBranch);

public sealed record GetCommitDiffRequest(string OrgId, string RepoId, string Sha, int MaxChars);

/// <summary>Wire copy of GitCommitInfo — this assembly stays dependency-free. Author name
/// only: e-mail addresses of the repository's contributors do not leave the machine.</summary>
public sealed record RunnerCommitInfo(
    string Sha, string Author, DateTimeOffset Date, string Subject);

public sealed record RunnerCommitLogResult(bool Success, RunnerCommitInfo[]? Commits, string? Error);

/// <summary>Wire copy of GitBranchInfo. IsRemote marks branches that exist only on the
/// user repo's own remote (remote-tracking refs, never checked out locally) — additive
/// with a default so older runners/hosts interop without a protocol bump.</summary>
public sealed record RunnerBranchInfo(
    string Name, string Sha, string? LastCommitSubject, DateTimeOffset? LastCommitDate,
    bool IsRemote = false);

public sealed record RunnerBranchListResult(bool Success, RunnerBranchInfo[]? Branches, string? Error);

public sealed record ListTreeRequest(string OrgId, string RepoId, string Branch, string? DirPath);

/// <summary>Wire copy of RepoTreeEntry — one lazily loaded tree level.</summary>
public sealed record RunnerTreeEntry(string Name, string Path, bool IsDirectory);

public sealed record RunnerTreeResult(bool Success, RunnerTreeEntry[]? Entries, string? Error);

/// <summary>RepoToken is a <see cref="RunnerTokens.UserRepo"/> token.</summary>
public sealed record RemoteSyncStatusRequest(string RepoToken);

/// <summary>How the user's working repo relates to its own origin remote.</summary>
public sealed record RunnerRemoteSyncStatus(
    bool IsGitRepo,
    bool HasRemote,
    bool HasUpstream,
    string? Branch,
    int Ahead,
    int Behind,
    string? Error,
    string? RemoteUrl = null);

/// <summary>RepoToken is a <see cref="RunnerTokens.UserRepo"/> token. The push uses the
/// machine's own git credentials.</summary>
public sealed record PushToRemoteRequest(string RepoToken, string Branch);

/// <summary>Creates a brand-new GitHub repository for a local repo that has no origin yet,
/// via the user's own `gh` CLI login (`gh repo create --source --push`) — the runner never
/// holds GitHub tokens itself. Name defaults to the repo's folder name. On success the
/// op result's Value is the new repository's URL. RepoToken is a
/// <see cref="RunnerTokens.UserRepo"/> token.</summary>
public sealed record PublishRepoToGitHubRequest(
    string RepoToken, string? Name, bool Private);

/// <summary>
/// Gives a local repo that has no origin yet the remote the user already created on the
/// forge, and pushes the current branch to it. The counterpart of
/// <see cref="PublishRepoToGitHubRequest"/>, which uses the user's own `gh` CLI to CREATE the
/// repository; here it already exists, so all that is left is plumbing.
///
/// The push uses the machine's own git credentials.
///
/// RepoToken is a <see cref="RunnerTokens.UserRepo"/> token. On success the op result's
/// Value is the remote URL.
/// </summary>
public sealed record AttachRemoteRequest(
    string RepoToken, string RemoteUrl, bool Push = true);

/// <summary>
/// Opens a task's worktree in a desktop editor on the user's machine — the escape hatch for
/// "the agent is 90% there and I want to finish this myself". WorktreeToken is a
/// <see cref="RunnerTokens.Worktree"/> token; Editor is an allowlisted key from
/// <see cref="RunnerEditors"/> (null = the runner's first installed preference). The op
/// returns as soon as the editor is launched — it does not wait for it to exit.
/// </summary>
/// <param name="RepoToken">Alternative target (additive for protocol compat): a
/// <see cref="RunnerTokens.UserRepo"/> token to open the repository itself on its default
/// branch — the user's working folder on Orkka Local, the runner's persistent main
/// checkout for hosted repos. When set, WorktreeToken is ignored (send it empty).</param>
public sealed record OpenInEditorRequest(
    string WorktreeToken, string? Editor = null, string? RepoToken = null);

/// <summary>One editor the runner found installed, in preference order.</summary>
public sealed record RunnerEditor(string Key, string DisplayName);

public sealed record ListEditorsResult(RunnerEditor[] Editors);

/// <summary>
/// Editor keys the runner will launch. An allowlist, not free text: the key selects a
/// hard-coded command name, so a compromised host can never turn this op into
/// arbitrary process execution on the user's machine.
/// </summary>
public static class RunnerEditors
{
    public const string VsCode = "vscode";
    public const string Cursor = "cursor";
    public const string Windsurf = "windsurf";
    public const string Zed = "zed";
    public const string Sublime = "sublime";
    public const string WebStorm = "webstorm";
    public const string IntelliJ = "intellij";

    public static readonly string[] All =
        [VsCode, Cursor, Windsurf, Zed, Sublime, WebStorm, IntelliJ];
}

/// <summary>
/// Worktree/workspace tokens: the host never interprets filesystem paths — it passes these
/// tokens around and the runner resolves them against its own repo cache layout.
/// </summary>
public static class RunnerTokens
{
    private const string WorktreePrefix = "runner-worktree:";
    private const string ChatPrefix = "runner-chat:";
    private const string UserRepoPrefix = "runner-userrepo:";

    public static string Worktree(string orgId, string repoId, string taskId)
        => $"{WorktreePrefix}{orgId}:{repoId}:{taskId}";

    public static bool TryParseWorktree(
        string token, out string orgId, out string repoId, out string taskId)
    {
        orgId = repoId = taskId = "";
        if (!token.StartsWith(WorktreePrefix, StringComparison.Ordinal))
            return false;
        var parts = token[WorktreePrefix.Length..].Split(':');
        if (parts.Length != 3)
            return false;
        (orgId, repoId, taskId) = (parts[0], parts[1], parts[2]);
        return true;
    }

    /// <summary>Project-scoped chat workspace token; a null/empty projectId produces the
    /// legacy org-level token (pre-project-scoping hosts and requests). A taskId (only with a
    /// project) names that task's Discuss workspace.</summary>
    public static string ChatWorkspace(string orgId, string? projectId = null, string? taskId = null)
        => string.IsNullOrEmpty(projectId)
            ? $"{ChatPrefix}{orgId}"
            : string.IsNullOrEmpty(taskId)
                ? $"{ChatPrefix}{orgId}:{projectId}"
                : $"{ChatPrefix}{orgId}:{projectId}:{taskId}";

    public static bool TryParseChatWorkspace(string token, out string orgId, out string? projectId)
        => TryParseChatWorkspace(token, out orgId, out projectId, out _);

    public static bool TryParseChatWorkspace(
        string token, out string orgId, out string? projectId, out string? taskId)
    {
        orgId = "";
        projectId = null;
        taskId = null;
        if (!token.StartsWith(ChatPrefix, StringComparison.Ordinal))
            return false;
        var parts = token[ChatPrefix.Length..].Split(':');
        if (parts.Length is not (1 or 2 or 3) || parts.Any(p => p.Length == 0))
            return false;
        orgId = parts[0];
        if (parts.Length >= 2)
            projectId = parts[1];
        if (parts.Length == 3)
            taskId = parts[2];
        return true;
    }

    /// <summary>Points at the user's own working repository (not a worktree/cache path) —
    /// used by the embedded terminal to open a shell where the user actually works.</summary>
    public static string UserRepo(string orgId, string repoId)
        => $"{UserRepoPrefix}{orgId}:{repoId}";

    public static bool TryParseUserRepo(string token, out string orgId, out string repoId)
    {
        orgId = repoId = "";
        if (!token.StartsWith(UserRepoPrefix, StringComparison.Ordinal))
            return false;
        var parts = token[UserRepoPrefix.Length..].Split(':');
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
            return false;
        (orgId, repoId) = (parts[0], parts[1]);
        return true;
    }
}

/// <summary>Opens (or finds the open) pull request for <paramref name="HeadBranch"/> into
/// <paramref name="BaseBranch"/> on the repository's forge, with the developer's own CLI.
/// <paramref name="Forge"/> is the ForgeKind name (GitHub, AzureDevOps, GitLab, …).
/// Success carries the pull request's URL.
/// <paramref name="Draft"/> opens it as a draft (a Story after its first sub-task; capability
/// <c>stories</c> — the host never sends it to a runner without that).</summary>
public sealed record OpenPullRequestRequest(
    string OrgId, string RepoId, string HeadBranch, string BaseBranch,
    string Title, string Body, string Forge, string? CliAccount = null, bool Draft = false);

/// <summary>Marks a Story's draft pull request ready for review — or, with <paramref name="Undo"/>,
/// turns a ready one back into a draft (a sub-task joined late).</summary>
public sealed record MarkPullRequestReadyRequest(
    string OrgId, string RepoId, string PullRequestUrl, string Forge, string? CliAccount = null, bool Undo = false);

/// <summary>Rewrites a pull request's description (a Story's checklist as its sub-tasks land).</summary>
public sealed record UpdatePullRequestBodyRequest(
    string OrgId, string RepoId, string PullRequestUrl, string Forge, string Body, string? CliAccount = null);

/// <summary>A Story member's starting point on the shared branch, read in its worktree: the head of
/// origin/{BranchName} when the branch is already on the remote, else the merge-base with
/// origin/{BaseBranch}. Success carries the sha.</summary>
public sealed record ResolveReviewBaseRequest(
    string OrgId, string RepoId, string TaskId, string BranchName, string BaseBranch);

/// <summary>Reads a pull request's state; success carries "open", "merged", "closed" or
/// "unknown".</summary>
public sealed record GetPullRequestStateRequest(
    string OrgId, string RepoId, string PullRequestUrl, string Forge, string? CliAccount = null);

/// <summary>Which kind of repository command the host asked this machine to run.</summary>
public enum CommandKind
{
    /// <summary>The repository's worktree setup command (orkka.json <c>setup</c>).</summary>
    Setup,

    /// <summary>The quality gate run after a dev run (orkka.json <c>check</c>).</summary>
    Check,

    /// <summary>The build that proves a merge compiles (orkka.json <c>build</c>).</summary>
    Build,

    /// <summary>A run target's start, install or stop command (orkka.json <c>run.targets</c>).</summary>
    RunTarget,
}

/// <summary>
/// A command the runner refused because nothing the developer controls vouches for it: it is
/// not in the repository's orkka.json on its default branch and was never approved on this
/// machine. The developer approves it there with <c>./orkka commands approve {Id}</c>.
/// </summary>
public sealed record CommandApproval(string Id, string Repo, string Command, CommandKind Kind, string Machine)
{
    /// <summary>What the developer is told, wherever the refusal surfaces.</summary>
    public string Message =>
        $"{Machine} did not run this {Kind.ToString().ToLowerInvariant()} command because it is not in the "
        + $"repository's orkka.json on its default branch and has not been approved on that machine: "
        + $"`{Command}`. To allow it, run `./orkka commands approve {Id}` there — or commit it to orkka.json.";
}
