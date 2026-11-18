namespace Orkka.IntegrationTests.Fixtures;

using Orkka.Modules.SourceControl.Contracts;

/// <summary>Resolution-only stand-in for IGitWorkspaceManager in hosts that don't register
/// it (the API host). Every operation fails or no-ops — tests that need real git behavior
/// must not reach these members.</summary>
public sealed class StubGitWorkspaceManager : IGitWorkspaceManager
{
    private const string NotSupported = "Git workspaces are not available in this test host";

    public Task<Result<string>> EnsureBareCloneAsync(
        Ulid orgId, Ulid repoId, string gitUrl, string? localCheckoutPath = null, CancellationToken ct = default)
        => Task.FromResult(Result<string>.Failure(NotSupported));

    public Task<Result<string>> CreateWorktreeAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string branchName,
        string baseBranch, string? worktreeName = null, CancellationToken ct = default)
        => Task.FromResult(Result<string>.Failure(NotSupported));

    public Task<Result<MergeConflictReport>> GetMergeConflictsAsync(
        Ulid orgId, Ulid repoId, string headBranch, string baseBranch, CancellationToken ct = default)
        => Task.FromResult(Result<MergeConflictReport>.Failure(NotSupported));

    public Task<Result<MergeStrategyOutcome>> ApplyMergeStrategyAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string baseBranch, string strategy,
        IReadOnlyDictionary<string, string>? fileChoices, CancellationToken ct = default)
        => Task.FromResult(Result<MergeStrategyOutcome>.Failure(NotSupported));

    public Task<Result<WorktreeCommandOutcome>> RunWorktreeCommandAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string command, RepoCommandKind kind,
        int timeoutSeconds = 600, CancellationToken ct = default)
        => Task.FromResult(Result<WorktreeCommandOutcome>.Failure(NotSupported));

    public Task<Result<MergeVerification>> VerifyMergeAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string baseBranch, CancellationToken ct = default)
        => Task.FromResult(Result<MergeVerification>.Failure(NotSupported));

    public Task<Result<BaseSyncOutcome>> SyncBaseBranchAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string baseBranch,
        bool leaveConflictsInProgress, bool commitLeftovers = false,
        CancellationToken ct = default)
        => Task.FromResult(Result<BaseSyncOutcome>.Failure(NotSupported));

    public Task<Result> CommitAndPushAsync(
        string worktreePath, string message, string branchName, CancellationToken ct = default)
        => Task.FromResult(Result.Failure(NotSupported));

    public Task CleanupWorktreeAsync(Ulid orgId, Ulid repoId, Ulid taskId)
        => Task.CompletedTask;

    public Task<int> CleanupStaleWorktreesAsync(TimeSpan maxAge, CancellationToken ct = default)
        => Task.FromResult(0);

    public IReadOnlyList<(Ulid OrgId, Ulid RepoId)> EnumerateRepoWorkspaces() => [];

    public Task RemoveRepoWorkspaceAsync(Ulid orgId, Ulid repoId, CancellationToken ct = default)
        => Task.CompletedTask;

    public string GetWorktreePath(Ulid orgId, Ulid repoId, Ulid taskId)
        => Path.Combine(Path.GetTempPath(), "orkka-tests", orgId.ToString(), repoId.ToString(), taskId.ToString());

    public string GetBareRepoPath(Ulid orgId, Ulid repoId)
        => Path.Combine(Path.GetTempPath(), "orkka-tests", orgId.ToString(), repoId.ToString(), "bare");

    public Task<Result> EnsurePushedAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string branchName, CancellationToken ct = default)
        => Task.FromResult(Result.Failure(NotSupported));

    public Task<bool> RemoteBranchExistsAsync(
        Ulid orgId, Ulid repoId, string branchName, CancellationToken ct = default)
        => Task.FromResult(false);

    public Task<Result<string>> GetDiffAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string baseBranch,
        int maxChars = 60_000, string? headBranch = null, CancellationToken ct = default)
        => Task.FromResult(Result<string>.Failure(NotSupported));

    public Task<Result<string>> GetWorkingTreeDiffAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string baseBranch,
        int maxChars = 60_000, CancellationToken ct = default, string? baseSha = null)
        => Task.FromResult(Result<string>.Failure(NotSupported));

    public Task<Result> WriteWorktreeContextAsync(
        Ulid orgId, Ulid repoId, Ulid taskId,
        IReadOnlyList<WorktreeContextFile> files, CancellationToken ct = default)
        => Task.FromResult(Result.Failure(NotSupported));

    /// <summary>Reports "nothing to prepare" rather than a failure: provisioning tests have
    /// no setup command configured, and a failure here would look like a broken workspace.</summary>
    public Task<Result<SetupCommandOutcome>> RunSetupCommandAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string command,
        CancellationToken ct = default)
        => Task.FromResult<Result<SetupCommandOutcome>>(
            new SetupCommandOutcome(Skipped: true, 0, null, 0));
}
