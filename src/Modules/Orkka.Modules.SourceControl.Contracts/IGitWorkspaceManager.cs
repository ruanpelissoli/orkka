namespace Orkka.Modules.SourceControl.Contracts;

using Orkka.SharedKernel.Utilities;

/// <summary>One conflicted path from a dry-run merge, with git's explanation (e.g.
/// "CONFLICT (modify/delete): …").</summary>
public sealed record MergeConflictFile(string Path, string Detail);

/// <summary>Result of a dry-run merge (git merge-tree) between a task branch and its base.</summary>
public sealed record MergeConflictReport(
    bool HasConflicts, IReadOnlyList<MergeConflictFile> Files, string? RawDetails);

/// <summary>Result of applying a deterministic merge-resolution strategy in the task worktree.
/// Completed=false means a merge is left in progress with <see cref="RemainingConflicts"/>
/// still unresolved (files the human assigned to AI) — an agent must finish and commit.</summary>
public sealed record MergeStrategyOutcome(bool Completed, IReadOnlyList<string> RemainingConflicts);

/// <summary>
/// What a base-branch sync did to a task worktree.
///
/// <see cref="Conflicted"/> is the only status that leaves state behind: the merge is still in
/// progress with <see cref="BaseSyncOutcome.ConflictedFiles"/> unresolved, waiting for an agent
/// to finish it. Everything else leaves a committed, buildable worktree.
///
/// Serialized by name: this crosses the runner wire, where the two sides are independently
/// versioned and an ordinal would silently re-point if anyone inserted a value.
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum BaseSyncStatus
{
    /// <summary>The branch already contained origin/{base} — nothing to merge.</summary>
    AlreadyCurrent,

    /// <summary>The base merged cleanly (fast-forward or a new merge commit).</summary>
    Merged,

    /// <summary>Conflicted, and the merge was left IN PROGRESS for an agent to resolve.</summary>
    Conflicted,

    /// <summary>Conflicted, and the merge was rolled back — the worktree is as it was.</summary>
    Aborted
}

/// <summary>
/// Result of merging the base branch into a task worktree. <paramref name="CommitsBehind"/> is
/// how far the base had moved past the branch before the merge — 0 whenever the status is
/// <see cref="BaseSyncStatus.AlreadyCurrent"/>, and the size of what the task had been working
/// blind to otherwise.
/// </summary>
public sealed record BaseSyncOutcome(
    BaseSyncStatus Status, IReadOnlyList<string> ConflictedFiles, int CommitsBehind);

/// <summary>
/// What is actually in a worktree after something claimed to have resolved a merge.
///
/// Exists because "the resolver agent finished without erroring" is not evidence that the merge
/// landed. An agent can commit conflict markers, abort the merge, or stop mid-way, and all three
/// look identical from the outside — right up until the markers reach the base branch.
/// </summary>
/// <param name="Completed">Nothing is unmerged, no markers survive, and the branch contains the
/// base. The only state that may be pushed.</param>
/// <param name="UnmergedPaths">Paths git still reports as conflicted (merge left unfinished).</param>
/// <param name="FilesWithMarkers">Tracked files containing conflict markers — the dangerous case,
/// because the tree is clean and git considers the merge done.</param>
/// <param name="BaseMerged">Whether the branch now contains the base branch tip at all. False
/// means the merge was abandoned rather than resolved.</param>
public sealed record MergeVerification(
    bool Completed,
    IReadOnlyList<string> UnmergedPaths,
    IReadOnlyList<string> FilesWithMarkers,
    bool BaseMerged);

/// <summary>
/// Result of running a one-off command in a task worktree. Unlike a setup command this is never
/// cached or skipped — the caller wants to know about the worktree AS IT IS NOW, and a
/// remembered exit code from a previous content would answer a different question.
/// </summary>
/// <param name="ApprovalNeeded">Set when the command did not run because nothing the developer
/// controls vouches for it — not in the repository's orkka.json on its default branch, not
/// approved on their machine. The text says how to approve it there.</param>
public sealed record WorktreeCommandOutcome(
    int ExitCode, string? Output, double DurationSeconds, string? ApprovalNeeded = null)
{
    public bool Succeeded => ApprovalNeeded is null && ExitCode == 0;
}

/// <summary>Which repository command a worktree command is — the developer approves each kind
/// separately on their machine, and orkka.json declares them under these names.</summary>
public enum RepoCommandKind
{
    /// <summary>The quality gate after a dev run (orkka.json <c>check</c>).</summary>
    Check,

    /// <summary>The build that proves a merge compiles (orkka.json <c>build</c>).</summary>
    Build,
}

/// <summary>
/// Outcome of a worktree setup command. <paramref name="Skipped"/> means the marker showed
/// the same command already succeeded in this worktree, so nothing ran — the common case,
/// since a worktree is reused across planning → developing → testing.
/// </summary>
/// <param name="ApprovalNeeded">See <see cref="WorktreeCommandOutcome"/>.</param>
public sealed record SetupCommandOutcome(
    bool Skipped, int ExitCode, string? Output, double DurationSeconds, string? ApprovalNeeded = null)
{
    public bool Succeeded => ApprovalNeeded is null && (Skipped || ExitCode == 0);
}

public interface IGitWorkspaceManager
{
    /// <summary>
    /// Ensures a bare clone exists for the repo and is current, using the developer's own git
    /// credentials on their machine. A mirror that cannot be refreshed is a failure.
    /// </summary>
    /// <param name="localCheckoutPath">The developer's own checkout of the repository in a
    /// project's base folder, when there is one: a first mirror of a remote is seeded from it,
    /// and the repo's task folders stay in the cache, never next to the checkout.</param>
    Task<Result<string>> EnsureBareCloneAsync(
        Ulid orgId, Ulid repoId, string gitUrl, string? localCheckoutPath = null, CancellationToken ct = default);

    /// <summary>
    /// Creates the task's isolated workspace: a STANDALONE clone of the repository with its own
    /// ref store (the mirror only seeds its objects), checked out on <paramref name="branchName"/>
    /// — continued from the forge when the branch is already there, cut from
    /// <paramref name="baseBranch"/> otherwise. Idempotent: an existing healthy workspace is
    /// reused, a broken one is rebuilt from the forge. <paramref name="worktreeName"/> optionally
    /// names the folder with the task's friendly key (e.g. "BRW-7") instead of the task-{ulid}
    /// default; existing legacy folders keep their name.
    /// </summary>
    Task<Result<string>> CreateWorktreeAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string branchName,
        string baseBranch, string? worktreeName = null, CancellationToken ct = default);

    /// <summary>
    /// Dry-run merge of <paramref name="headBranch"/> into <paramref name="baseBranch"/>
    /// (git merge-tree) against the fresh bare mirror. Fails on old git (&lt; 2.38) or a
    /// missing cache — callers should treat failure as "mergeability unknown".
    /// </summary>
    Task<Result<MergeConflictReport>> GetMergeConflictsAsync(
        Ulid orgId, Ulid repoId, string headBranch, string baseBranch, CancellationToken ct = default);

    /// <summary>
    /// Merges the base branch into the task worktree applying a human-picked side for
    /// conflicts: strategy "task" keeps the task branch's side, "current" keeps the base
    /// branch's side, "perFile" applies <paramref name="fileChoices"/> (path → "task" |
    /// "current" | "ai"). Commits when everything resolves; otherwise leaves the merge in
    /// progress and reports the files assigned to AI.
    /// </summary>
    Task<Result<MergeStrategyOutcome>> ApplyMergeStrategyAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string baseBranch, string strategy,
        IReadOnlyDictionary<string, string>? fileChoices, CancellationToken ct = default);

    /// <summary>
    /// Merges origin/{baseBranch} INTO the task worktree, so the task stops working against the
    /// commit it forked from. Cheap and idempotent: a branch that already contains the base
    /// fetches and returns <see cref="BaseSyncStatus.AlreadyCurrent"/> without writing anything.
    ///
    /// <paramref name="leaveConflictsInProgress"/> decides what a conflict means to the caller.
    /// True (the Developing boundary, and the agent's own mid-run sync) leaves the merge in
    /// progress so the agent that is about to run — the one holding the context for both sides —
    /// resolves it as part of its work. False (every other phase) rolls the merge back and
    /// reports <see cref="BaseSyncStatus.Aborted"/>, because a reviewer or a QA run must never
    /// be handed a worktree with conflict markers in it.
    ///
    /// A dirty worktree is handled by <paramref name="commitLeftovers"/>: true (a phase
    /// boundary) commits whatever the previous run left uncommitted as the platform before
    /// merging — a run that crashed past its publish step must not strand the task; false (the
    /// agent's own mid-run sync) fails without touching the tree, because the agent still owns
    /// it. A fetch that fails is a failure with a code, never "proceed on the old base".
    /// </summary>
    Task<Result<BaseSyncOutcome>> SyncBaseBranchAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string baseBranch,
        bool leaveConflictsInProgress, bool commitLeftovers = false,
        CancellationToken ct = default);

    /// <summary>
    /// Inspects a task worktree after a merge resolution and reports whether it is genuinely
    /// finished — see <see cref="MergeVerification"/> for why the resolver's own word is not
    /// enough. Read-only; never changes the worktree. Failure means "could not verify", which
    /// callers must treat as not-verified rather than as success.
    /// </summary>
    Task<Result<MergeVerification>> VerifyMergeAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string baseBranch, CancellationToken ct = default);

    /// <summary>
    /// Runs a command in the task worktree and returns its exit code and output, every time it
    /// is called. Used for the merge-path build check, where the answer must describe the code
    /// that is in the worktree right now. It runs on the user's own runner, where the command
    /// is theirs.
    /// </summary>
    Task<Result<WorktreeCommandOutcome>> RunWorktreeCommandAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string command, RepoCommandKind kind,
        int timeoutSeconds = 600, CancellationToken ct = default);

    /// <summary>
    /// Commits all changes, pushes the branch to remote.
    /// </summary>
    Task<Result> CommitAndPushAsync(
        string worktreePath, string message, string branchName, CancellationToken ct = default);

    /// <summary>
    /// Cleans up the worktree after task completion.
    /// </summary>
    Task CleanupWorktreeAsync(Ulid orgId, Ulid repoId, Ulid taskId);

    /// <summary>
    /// Removes any worktrees under the base path whose last-write time is older than
    /// <paramref name="maxAge"/>. Backstop for tasks that never reached a clean terminal state
    /// (abandoned, escalated, failed). Returns the number of worktrees removed.
    /// </summary>
    Task<int> CleanupStaleWorktreesAsync(TimeSpan maxAge, CancellationToken ct = default);

    /// <summary>
    /// Enumerates the (orgId, repoId) pairs that currently have a workspace directory on disk.
    /// Used to reconcile on-disk repos against the database and wipe deleted ones.
    /// </summary>
    IReadOnlyList<(Ulid OrgId, Ulid RepoId)> EnumerateRepoWorkspaces();

    /// <summary>
    /// Removes the entire on-disk workspace (bare clone + all worktrees) for a repo. Used when a
    /// repo/project is deleted or an org is offboarded, so customer code is not left on disk.
    /// </summary>
    Task RemoveRepoWorkspaceAsync(Ulid orgId, Ulid repoId, CancellationToken ct = default);

    /// <summary>
    /// Gets the worktree path for a task.
    /// </summary>
    string GetWorktreePath(Ulid orgId, Ulid repoId, Ulid taskId);

    /// <summary>
    /// Gets the bare repo path for a repo.
    /// </summary>
    string GetBareRepoPath(Ulid orgId, Ulid repoId);

    /// <summary>
    /// Ensures the branch is pushed to remote with the developer's own git credentials.
    /// </summary>
    Task<Result> EnsurePushedAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string branchName, CancellationToken ct = default);

    /// <summary>
    /// Checks if a remote branch exists.
    /// </summary>
    Task<bool> RemoteBranchExistsAsync(
        Ulid orgId, Ulid repoId, string branchName,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the diff between the base branch and the task worktree's HEAD
    /// (git diff origin/{baseBranch}...HEAD), truncated to maxChars. When the worktree is
    /// gone (cleaned up, GC'd, or the phase ran elsewhere) and <paramref name="headBranch"/>
    /// is provided, falls back to diffing the pushed branch in the bare mirror — PR diffs
    /// must not depend on a live worktree.
    /// </summary>
    Task<Result<string>> GetDiffAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string baseBranch,
        int maxChars = 60_000, string? headBranch = null, CancellationToken ct = default);

    /// <summary>
    /// A Story member's own change: <c>git diff {baseSha} HEAD</c> in the task worktree (or the
    /// pushed head in the bare mirror once the worktree is gone), where <paramref name="baseSha"/>
    /// is the member's <c>ReviewBaseSha</c> — so the review sees this member's commits, not the
    /// whole shared branch. Implementations that cannot fall back to the full diff.
    /// </summary>
    Task<Result<string>> GetDiffSinceAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string baseBranch, string baseSha,
        int maxChars = 60_000, string? headBranch = null, CancellationToken ct = default)
        => GetDiffAsync(orgId, repoId, taskId, baseBranch, maxChars, headBranch, ct);

    /// <summary>
    /// Where a Story member starts on the shared branch, read in its freshly made worktree: the
    /// head of origin/{branchName} when the branch already existed on the remote (a previous
    /// member pushed it), else the merge-base of HEAD with origin/{baseBranch}. Null when it
    /// cannot be read — the review then shows the full branch diff.
    /// </summary>
    Task<string?> ResolveReviewBaseAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string branchName, string baseBranch, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    /// <summary>
    /// The task's changes <em>as they stand on disk</em>: the working tree — committed,
    /// staged, unstaged and untracked (honouring .gitignore) — diffed against the branch
    /// point with origin/{baseBranch}. This is what the Code View shows while an agent is
    /// mid-run, before anything is committed; <see cref="GetDiffAsync"/> sees commits only.
    /// Fails when the worktree does not exist. Must not mutate the worktree's own index —
    /// the agent's git commands are using it concurrently. <paramref name="baseSha"/>: a Story
    /// member's review base — diff against it (moved past any default-branch sync, like
    /// <see cref="GetDiffSinceAsync"/>) so the live view shows only that member's change, not
    /// the earlier sub-tasks' work on the shared branch.
    /// </summary>
    Task<Result<string>> GetWorkingTreeDiffAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string baseBranch,
        int maxChars = 60_000, CancellationToken ct = default, string? baseSha = null);

    /// <summary>
    /// Materializes prompt context (docs, plan, diff) as files under .orkka/context/ inside
    /// the task worktree, so agents read the bytes on demand instead of every phase paying
    /// for them inline. The .orkka/ folder is excluded from commits via the bare repo's
    /// info/exclude. Failure (e.g. a local runner without the context-files capability)
    /// means the caller must fall back to inlining the content in the prompt.
    /// </summary>
    Task<Result> WriteWorktreeContextAsync(
        Ulid orgId, Ulid repoId, Ulid taskId,
        IReadOnlyList<WorktreeContextFile> files, CancellationToken ct = default);

    /// <summary>
    /// Runs the repo's setup command in the task worktree so the app can actually start —
    /// dependencies installed, env in place. A fresh worktree has none of that, which today
    /// makes the QA lane's run command fail and leaves a developer unable to run what the
    /// agent just wrote.
    ///
    /// Runs once per worktree: a marker under .orkka/ records the command's hash and exit
    /// code, and a re-entered phase skips. Re-runs when the command text changed or the last
    /// attempt failed — a failure is never cached as done.
    ///
    /// It runs on the user's own runner: their machine and their filesystem, the same trust
    /// model as them typing the command themselves.
    /// </summary>
    Task<Result<SetupCommandOutcome>> RunSetupCommandAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string command,
        CancellationToken ct = default);
}

/// <summary>One context file to write under a worktree's .orkka/context/ folder.
/// RelativePath is relative to that folder — never absolute, never escaping it.</summary>
public sealed record WorktreeContextFile(string RelativePath, string Content);
