namespace Orkka.Modules.Execution.Git;

using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orkka.Git;

/// <summary>
/// Task workspaces on disk: a bare mirror per repository and one worktree per task.
///
/// Every git command here goes through <see cref="GitProcess"/>, and three rules hold for all
/// of them. Git authenticates with the developer's own credentials on their machine; nothing
/// is ever written into <c>.git/config</c> — <c>origin</c> is always the clean URL, and a mirror
/// cloned by an earlier version is scrubbed on first touch. Every commit is authored by the platform
/// identity, on the command's environment and in the mirror's config (which every worktree
/// shares, so an agent's own <c>git commit</c> carries it too). And nothing fails quietly:
/// a mirror that cannot be refreshed, a base that cannot be fetched, a push that fails are
/// failures with a <see cref="GitErrorCode"/>, recorded in the operation ledger.
/// </summary>
public sealed class GitWorkspaceManager(
    IConfiguration configuration,
    ILogger<GitWorkspaceManager> logger,
    GitProcess git,
    IGitOperationLedger ledger) : IGitWorkspaceManager
{
    /// <summary>Two-argument form for hosts and tests that have no ledger of their own.</summary>
    public GitWorkspaceManager(IConfiguration configuration, ILogger<GitWorkspaceManager> logger)
        : this(configuration, logger, new GitProcess(NullLogger<GitProcess>.Instance),
            new LoggingGitOperationLedger(logger, GitTopologies.Unknown)) { }

    private readonly string _basePath = configuration.GetValue<string>("Worker:RepoBasePath")
        ?? Path.Combine(Path.GetTempPath(), "orkka-repos");

    // Refuse to run agents against pathologically large repos (cost/DoS bound). 0 = disabled.
    private readonly long _maxRepoSizeBytes =
        (long)configuration.GetValue<int>("Worker:MaxRepoSizeMb", 0) * 1024 * 1024;

    /// <summary>
    /// The author of every commit this process makes, and — written into each mirror's config —
    /// of every commit an agent makes in a worktree: the identity the runner workspace was
    /// configured with, or null — then the developer's own git config decides, as it does for
    /// every other commit they make.
    /// </summary>
    private readonly GitIdentity? _identity =
        GitIdentity.FromConfigurationOrNull(configuration["Git:CommitAuthorName"], configuration["Git:CommitAuthorEmail"]);

    public string GetBareRepoPath(Ulid orgId, Ulid repoId) =>
        Path.Combine(_basePath, orgId.ToString(), repoId.ToString(), "bare");

    public string GetWorktreePath(Ulid orgId, Ulid repoId, Ulid taskId)
    {
        // Local-source repos linked one by one keep worktrees in a sibling folder next to the
        // user's repo ({repo}-worktrees/…) so they're easy to find and inspect. Cloned remotes
        // and every repo of a base-folder project stay inside the cache — a sibling folder
        // would land inside the developer's base folder.
        var root = WorktreesRoot(orgId, repoId);
        var legacyPath = Path.Combine(root, $"task-{taskId}");

        // Existing worktrees keep their legacy task-{ulid} folder; new ones use the task's
        // friendly key (e.g. "BRW-7") recorded at creation time.
        if (Directory.Exists(legacyPath))
            return legacyPath;

        var name = TryReadWorktreeName(orgId, repoId, taskId);
        return name is not null ? Path.Combine(root, name) : legacyPath;
    }

    private string WorktreesRoot(Ulid orgId, Ulid repoId) =>
        ExternalWorktreesRoot(RepoDir(orgId, repoId))
            ?? Path.Combine(RepoDir(orgId, repoId), "worktrees");

    private string RepoDir(Ulid orgId, Ulid repoId) =>
        Path.Combine(_basePath, orgId.ToString(), repoId.ToString());

    // ── Friendly worktree names ──────────────────────────────────────────────
    // Folder names like "BRW-7" beat "task-01KXHV…" for humans browsing the
    // {repo}-worktrees folder, but every other operation only knows the task ULID —
    // so the name chosen at creation is recorded in a marker file inside the cache
    // (never next to the user's repo) and resolved through GetWorktreePath.

    private static string WorktreeNamesDir(string repoDir) => Path.Combine(repoDir, "worktree-names");

    private string WorktreeNameMarker(Ulid orgId, Ulid repoId, Ulid taskId) =>
        Path.Combine(WorktreeNamesDir(RepoDir(orgId, repoId)), taskId.ToString());

    private string? TryReadWorktreeName(Ulid orgId, Ulid repoId, Ulid taskId)
    {
        try
        {
            var marker = WorktreeNameMarker(orgId, repoId, taskId);
            if (!File.Exists(marker))
                return null;
            var name = File.ReadAllText(marker).Trim();
            return SanitizeWorktreeName(name);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Rejects anything that isn't a safe, simple folder name.</summary>
    private static string? SanitizeWorktreeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        name = name.Trim();
        if (name.Length > 64 || name.StartsWith('.') || name.StartsWith("task-", StringComparison.Ordinal))
            return null;
        return name.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.') ? name : null;
    }

    private void TryRecordWorktreeName(Ulid orgId, Ulid repoId, Ulid taskId, string? worktreeName)
    {
        var name = SanitizeWorktreeName(worktreeName);
        if (name is null)
            return;

        try
        {
            var root = WorktreesRoot(orgId, repoId);

            // A legacy folder already exists for this task — renaming a live worktree
            // would orphan git's registration, so keep the old name.
            if (Directory.Exists(Path.Combine(root, $"task-{taskId}")))
                return;

            // Belt and braces: never claim a folder another task already resolved to.
            var target = Path.Combine(root, name);
            if (Directory.Exists(target) && TryReadWorktreeName(orgId, repoId, taskId) != name)
                return;

            var marker = WorktreeNameMarker(orgId, repoId, taskId);
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            File.WriteAllText(marker, name);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not record worktree name for task {TaskId}", taskId);
        }
    }

    private void TryDeleteWorktreeName(Ulid orgId, Ulid repoId, Ulid taskId)
    {
        try
        {
            var marker = WorktreeNameMarker(orgId, repoId, taskId);
            if (File.Exists(marker))
                File.Delete(marker);
        }
        catch { /* best effort */ }
    }

    /// <summary>Folder names recorded as Orkka worktrees for this repo (friendly-named ones).</summary>
    private static HashSet<string> LoadNamedWorktrees(string repoDir)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var dir = WorktreeNamesDir(repoDir);
            if (!Directory.Exists(dir))
                return names;
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var name = SanitizeWorktreeName(File.ReadAllText(file).Trim());
                if (name is not null)
                    names.Add(name);
            }
        }
        catch { /* best effort */ }
        return names;
    }

    /// <summary>True when the folder is one Orkka created — legacy task-{ulid} naming or a
    /// recorded friendly name. External worktree roots sit next to the user's repo, where
    /// anything else must be left alone.</summary>
    private static bool IsOrkkaWorktreeFolder(string worktreePath, HashSet<string> namedWorktrees)
    {
        var folder = Path.GetFileName(worktreePath);
        return folder.StartsWith("task-", StringComparison.Ordinal) || namedWorktrees.Contains(folder);
    }

    /// <summary>
    /// When the clone source is a local directory (Orkka Local), its path is recorded in a
    /// marker file next to the bare mirror so worktree placement can follow the source repo.
    /// </summary>
    private static string SourcePathMarker(string repoDir) => Path.Combine(repoDir, "source-path");

    /// <summary>
    /// Present when the repository was discovered in a project's base folder. Its task folders
    /// then stay under the cache (<c>{repo}/worktrees</c>) whatever the clone source is: a
    /// <c>{repo}-worktrees</c> sibling would be created INSIDE the developer's base folder, where
    /// the next rescan would also have to step around it. Sticky — written by the first
    /// <see cref="EnsureBareCloneAsync"/> that names the checkout, so a later call that does
    /// not (chat, the repo browser) cannot move the folders back out.
    /// </summary>
    private static string BaseFolderMarker(string repoDir) => Path.Combine(repoDir, "base-folder-checkout");

    private static bool IsBaseFolderRepo(string repoDir)
    {
        try
        {
            return File.Exists(BaseFolderMarker(repoDir));
        }
        catch
        {
            return false;
        }
    }

    private static string? TryReadSourcePath(string repoDir)
    {
        try
        {
            var marker = SourcePathMarker(repoDir);
            if (!File.Exists(marker))
                return null;

            var source = File.ReadAllText(marker).Trim();
            // A moved/deleted source falls back to the cache layout rather than creating
            // sibling folders at a stale location.
            return source.Length > 0 && Directory.Exists(source) ? source : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ExternalWorktreesRoot(string repoDir)
    {
        if (IsBaseFolderRepo(repoDir))
            return null;

        var source = TryReadSourcePath(repoDir);
        if (source is null)
            return null;

        var trimmed = source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(trimmed);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name)
            ? null
            : Path.Combine(parent, $"{name}-worktrees");
    }

    private void RecordSourcePath(Ulid orgId, Ulid repoId, string gitUrl, string? localCheckoutPath)
    {
        try
        {
            var repoDir = RepoDir(orgId, repoId);
            if (!string.IsNullOrWhiteSpace(localCheckoutPath) && !IsBaseFolderRepo(repoDir))
                File.WriteAllText(BaseFolderMarker(repoDir), Path.GetFullPath(localCheckoutPath));

            var marker = SourcePathMarker(repoDir);
            if (Directory.Exists(gitUrl))
                File.WriteAllText(marker, Path.GetFullPath(gitUrl));
            else if (File.Exists(marker))
                File.Delete(marker);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not record source path for repo {RepoId}", repoId);
        }
    }

    private async Task<string> GetRemoteUrlAsync(string repoPath, CancellationToken ct)
    {
        var result = await git.RunAsync(new GitCommand(repoPath, "remote", "get-url", "origin"), ct);
        return result.Succeeded ? result.Output : "";
    }

    // ── Mirror ───────────────────────────────────────────────────────────────

    /// <summary>
    /// One refresh of a mirror at a time. The fetch was already gated per repo, but the config
    /// writes around it (<c>remote set-url</c>, the identity stamp) were not — and two tasks
    /// entering a phase in the same second both refreshed the same mirror on the user's
    /// runner, where git's rename of <c>config</c> under a concurrent reader is refused by
    /// Windows: "unable to access 'config': Permission denied", and a task failed for it
    /// (prod, 2026-09-16, StoreFront task 17). The second caller now waits and then finds a
    /// mirror that was refreshed a moment ago.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> MirrorGates =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// One request at a time per worktree path. The Worker restarted while an interactive
    /// session's workspace was being prepared; its replacement asked the runner for the same
    /// worktree while the first clone was still writing, and the second attempt died on "The
    /// process cannot access the file … because it is being used by another process" (prod,
    /// 2026-09-17, StoreFront STO-23). The second caller now waits, then finds the first's
    /// clone healthy and reuses it — or, when the first was killed mid-clone, a directory it
    /// can remove and recreate.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> WorktreeGates =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<Result<string>> EnsureBareCloneAsync(
        Ulid orgId, Ulid repoId, string gitUrl, string? localCheckoutPath = null, CancellationToken ct = default)
    {
        using var op = GitOperationScope.Begin(ledger, GitOperationKind.EnsureMirror,
            orgId.ToString(), repoId.ToString());

        var barePath = GetBareRepoPath(orgId, repoId);
        var gate = MirrorGates.GetOrAdd(barePath, _ => new SemaphoreSlim(1, 1));
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        waitCts.CancelAfter(FetchGateWaitTimeout);
        try
        {
            await gate.WaitAsync(waitCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var timedOut = $"{GitErrorCode.Timeout}: Timed out waiting for another refresh of {barePath} to finish.";
            op.Fail(timedOut, GitErrorCode.Timeout);
            return Result<string>.Failure(timedOut);
        }

        Result<string> result;
        try
        {
            result = await EnsureBareCloneCoreAsync(orgId, repoId, gitUrl, localCheckoutPath, ct);
        }
        finally
        {
            gate.Release();
        }

        if (result.IsSuccess) op.Complete(); else op.Fail(result.Error);
        return result;
    }

    private async Task<Result<string>> EnsureBareCloneCoreAsync(
        Ulid orgId, Ulid repoId, string gitUrl, string? localCheckoutPath, CancellationToken ct)
    {
        var barePath = GetBareRepoPath(orgId, repoId);

        // One guard at the cache root also covers chat workspaces and utility scratch
        // dirs, which live under the base path but outside any worktrees folder.
        Agents.GeminiDotenvGuard.Refresh(_basePath, logger);

        // origin is always the clean URL. A credential that reached this method inside the URL
        // (an older caller) is stripped here.
        var cleanUrl = GitUrls.Clean(gitUrl);
        try
        {
            // A directory that is NOT a repository must never be mistaken for a mirror.
            // `git clone --bare` creates the directory and copies its templates in before it
            // ever contacts the remote, so a clone that failed on authentication leaves a
            // directory behind that looks cloned and is not. Checked with --resolve-git-dir,
            // which inspects this exact directory rather than walking up to a parent
            // repository, and only ever acted on when git itself ran and said no.
            if (Directory.Exists(barePath) && await IsBrokenRepositoryAsync(barePath, ct))
            {
                logger.LogWarning(
                    "Bare repo {RepoId} at {Path} is not a git repository — a previous clone "
                    + "left it half-written. Removing it and cloning again.",
                    repoId, barePath);
                TryDeleteDirectory(barePath);
            }

            if (Directory.Exists(barePath))
            {
                logger.LogInformation("Fetching latest for bare repo {RepoId}", repoId);

                // Re-stamped on every refresh: this is what scrubs a token an earlier version
                // wrote into the URL, and what follows a repository that moved.
                await git.RunAsync(new GitCommand(barePath, "remote", "set-url", "origin", cleanUrl), ct);

                // A mirror that cannot be refreshed is a failure, not a warning. Continuing on
                // a stale mirror is how a milestone branch created on the forge never reached
                // a worktree ("invalid reference origin/…") and how a missing sign-in surfaced
                // only at push time, a whole run later.
                var fetchResult = await FetchOriginAsync(barePath, barePath, ct);
                if (fetchResult.IsFailure)
                    return Result<string>.Failure($"The repository mirror could not be refreshed: {fetchResult.Error}");
            }
            else
            {
                // A first mirror of a local folder that is not on this machine: the folder
                // belongs to another runner (a hybrid project's folder on the user's PC, asked
                // for on Orkka's compute) — a precondition, not something for git to guess at.
                // Only here: a mirror that already exists and whose folder went away is a
                // refresh failure below, and keeps the mirror.
                if (GitUrls.UnreachableLocalFolder(cleanUrl) is { } unreachable)
                    return Result<string>.Failure(unreachable);

                Directory.CreateDirectory(Path.GetDirectoryName(barePath)!);

                // A remote the developer already has checked out in the project's base folder:
                // seed the mirror from that checkout, so the first clone copies objects from
                // the disk and the fetch below only brings what the checkout lacks.
                if (await TrySeedMirrorFromCheckoutAsync(repoId, barePath, cleanUrl, localCheckoutPath, ct))
                {
                    RecordSourcePath(orgId, repoId, gitUrl, localCheckoutPath);
                    var seededIdentity = await ApplyCommitIdentityAsync(barePath, ct);
                    return seededIdentity.IsFailure ? seededIdentity : Result<string>.Success(barePath);
                }

                logger.LogInformation("Cloning bare repo {RepoId} to {Path}", repoId, barePath);
                var cloneResult = await git.RunAsync(
                    new GitCommand(Path.GetDirectoryName(barePath)!,
                        ["clone", "--bare", cleanUrl, "bare"], _identity, CloneTimeout), ct);
                // Every failure from here leaves a directory that is not a usable mirror, and
                // leaving one behind is what turns a retryable clone failure into a permanently
                // broken repository (see the guard above). Clean up, so the next attempt starts
                // from nothing.
                if (!cloneResult.Succeeded)
                {
                    TryDeleteDirectory(barePath);
                    return Result<string>.Failure(cloneResult.Describe());
                }

                // Bare clone doesn't set up remote tracking branches by default.
                // Configure fetch refspec so `origin/main` etc. are available for worktrees.
                var refspecResult = await git.RunAsync(new GitCommand(barePath,
                    "config", "remote.origin.fetch", "+refs/heads/*:refs/remotes/origin/*"), ct);
                if (!refspecResult.Succeeded)
                {
                    TryDeleteDirectory(barePath);
                    return Result<string>.Failure(refspecResult.Describe());
                }

                // Fetch again to populate remote tracking branches: a fresh clone with no
                // remote-tracking refs has nothing to branch from.
                var fetchResult = await FetchOriginAsync(barePath, barePath, ct);
                if (fetchResult.IsFailure)
                {
                    TryDeleteDirectory(barePath);
                    return Result<string>.Failure(fetchResult.Error!);
                }
            }

            // Idempotent, so a refresh also corrects a repo cloned before the identity existed.
            var identityResult = await ApplyCommitIdentityAsync(barePath, ct);
            if (identityResult.IsFailure)
                return identityResult;

            RecordSourcePath(orgId, repoId, gitUrl, localCheckoutPath);
            return Result<string>.Success(barePath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to ensure bare clone for repo {RepoId}", repoId);
            return Result<string>.Failure($"Git clone failed: {ex.Message}");
        }
    }

    /// <summary>A large repository on a slow link; anything past this is not going to finish.</summary>
    private static readonly TimeSpan CloneTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Seeds a first mirror of a REMOTE from the developer's own checkout of it:
    /// <c>git clone --mirror &lt;checkout&gt;</c>, then <c>origin</c> re-pointed at the remote, the
    /// refspec set to the cache's layout (<c>refs/remotes/origin/*</c>) and a fetch from the
    /// remote. False — with nothing left behind — whenever any step fails, and the caller
    /// clones from the remote as it always did. Never for a folder repo: its checkout IS the
    /// clone source already.
    /// </summary>
    private async Task<bool> TrySeedMirrorFromCheckoutAsync(
        Ulid repoId, string barePath, string remoteUrl, string? localCheckoutPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(localCheckoutPath)
            || Directory.Exists(remoteUrl)
            || !Directory.Exists(localCheckoutPath)
            || !(Directory.Exists(Path.Combine(localCheckoutPath, ".git")) || File.Exists(Path.Combine(localCheckoutPath, ".git"))))
            return false;

        var checkout = Path.GetFullPath(localCheckoutPath);
        logger.LogInformation("Seeding the mirror of repo {RepoId} from the local checkout {Checkout}", repoId, checkout);

        var parent = Path.GetDirectoryName(barePath)!;
        var steps = new (string Label, GitCommand Command)[]
        {
            ("clone the local checkout", new GitCommand(parent, ["clone", "--mirror", checkout, "bare"], _identity, CloneTimeout)),
            ("point origin at the remote", new GitCommand(barePath, "remote", "set-url", "origin", remoteUrl)),
            // The cache layout every other operation expects: remote branches under
            // refs/remotes/origin/*, not a mirror that overwrites refs/* on each fetch.
            ("leave mirror mode", new GitCommand(barePath, "config", "--unset", "remote.origin.mirror")),
            ("set the fetch refspec", new GitCommand(barePath, "config", "--replace-all", "remote.origin.fetch", "+refs/heads/*:refs/remotes/origin/*")),
        };

        foreach (var (label, command) in steps)
        {
            var result = await git.RunAsync(command, ct);
            if (!result.Succeeded)
            {
                logger.LogInformation(
                    "Seeding the mirror of repo {RepoId} from {Checkout} failed to {Step} ({Error}) — cloning from the remote instead",
                    repoId, checkout, label, result.Describe());
                TryDeleteDirectory(barePath);
                return false;
            }
        }

        // The checkout's own remote-tracking refs came along as refs/remotes/origin/*; the
        // fetch brings them up to date with the remote. A fetch that fails here (no network,
        // no sign-in) falls back to the plain clone, which then reports the real reason.
        var fetch = await FetchOriginAsync(barePath, barePath, ct);
        if (fetch.IsFailure)
        {
            logger.LogInformation(
                "Seeded mirror of repo {RepoId} could not fetch from its remote ({Error}) — cloning from the remote instead",
                repoId, fetch.Error);
            TryDeleteDirectory(barePath);
            return false;
        }

        return true;
    }

    public async Task<Result<string>> CreateWorktreeAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string branchName,
        string baseBranch, string? worktreeName = null, CancellationToken ct = default)
    {
        using var op = GitOperationScope.Begin(ledger, GitOperationKind.CreateWorkspace,
            orgId.ToString(), repoId.ToString(), taskId.ToString());
        var gate = WorktreeGates.GetOrAdd(GetWorktreePath(orgId, repoId, taskId), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var result = await CreateWorktreeCoreAsync(orgId, repoId, taskId, branchName, baseBranch, worktreeName, ct);
            if (result.IsSuccess) op.Complete(); else op.Fail(result.Error);
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<Result<string>> CreateWorktreeCoreAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string branchName,
        string baseBranch, string? worktreeName, CancellationToken ct)
    {
        var barePath = GetBareRepoPath(orgId, repoId);
        TryRecordWorktreeName(orgId, repoId, taskId, worktreeName);
        var worktreePath = GetWorktreePath(orgId, repoId, taskId);

        if (!Directory.Exists(barePath))
            return Result<string>.Failure("Bare repo does not exist. Call EnsureBareCloneAsync first.");

        // Guard the worktrees root (cache-internal or {repo}-worktrees sibling) against
        // the gemini CLI's upward dotenv walk — refreshed every run, including worktree
        // reuse, so a rotated API key propagates. See GeminiDotenvGuard.
        Agents.GeminiDotenvGuard.Refresh(Path.GetDirectoryName(worktreePath)!, logger);

        // Legacy linked worktrees share the mirror's info/exclude; standalone clones get their
        // own below. One line keeps the .orkka/ context folder (docs/plan/diff materialized
        // for agents) out of every `git add -A` without touching the repo's own .gitignore.
        EnsureOrkkaExcluded(barePath);

        if (_maxRepoSizeBytes > 0)
        {
            var sizeBytes = DirectorySizeBytes(barePath);
            if (sizeBytes > _maxRepoSizeBytes)
            {
                var sizeMb = sizeBytes / (1024 * 1024);
                var capMb = _maxRepoSizeBytes / (1024 * 1024);
                logger.LogWarning("Repo {RepoId} is {SizeMb} MB, exceeding the {CapMb} MB cap — refusing worktree",
                    repoId, sizeMb, capMb);
                return Result<string>.Failure(
                    $"Repository is {sizeMb} MB, which exceeds the {capMb} MB limit for agent runs.");
            }
        }

        try
        {
            // Reuse an existing healthy worktree — re-entering a phase (e.g. after review
            // feedback or an answered question) must NOT discard prior work.
            if (Directory.Exists(worktreePath))
            {
                var statusResult = await git.RunAsync(new GitCommand(worktreePath, "status", "--porcelain"), ct);
                if (statusResult.Succeeded)
                {
                    await RepairPartialCloneAsync(worktreePath, barePath, ct);
                    logger.LogInformation("Reusing existing worktree for task {TaskId} at {Path}",
                        taskId, worktreePath);
                    return Result<string>.Success(worktreePath);
                }

                // Corrupt/stale worktree — remove and recreate below
                logger.LogWarning("Existing worktree for task {TaskId} is unhealthy — recreating", taskId);
                if (!await RemoveWorktreeDirectoryAsync(barePath, worktreePath, ct))
                    return Result<string>.Failure(
                        $"{GitErrorCode.Locked}: The previous worktree is locked by another process (an agent may still be " +
                        "shutting down). Try again in a few seconds.");
            }

            // A STANDALONE clone, not a linked worktree. Every task used to share the mirror's
            // ref store — N worktrees, chat worktrees, GC and the agent's own git all
            // compare-and-swapping the same refs, across two processes on a user's machine.
            // "cannot lock ref", "invalid reference origin/…", "not a git repository" and
            // locked worktrees all lived there. A clone owns its refs, fetches on its own, and
            // can be deleted and rebuilt from the forge at any moment.
            //
            // The mirror stays as an OBJECT cache: --reference-if-able makes the clone
            // near-instant for a repository this machine has seen, --dissociate then copies
            // the borrowed objects in so the clone never depends on the mirror's lifetime.
            //
            // A FULL clone, never a partial one. A blobless clone fetches file contents lazily
            // from the forge inside whatever command first needs them — a merge, a diff, a
            // checkout — and that lazy fetch runs inside whatever command needs it, including the
            // agent's own git. Seen live: a phase-entry base merge died
            // on "could not fetch … from promisor remote". The mirror already holds every
            // object, so a full clone costs disk, not network, and nothing fetches by surprise.
            var originUrl = await GetRemoteUrlAsync(barePath, ct);
            var branchExistsRemotely = await RemoteBranchExistsAsync(orgId, repoId, branchName, ct);
            var startPoint = branchExistsRemotely ? $"origin/{branchName}" : $"origin/{baseBranch}";

            Directory.CreateDirectory(Path.GetDirectoryName(worktreePath)!);
            List<string> cloneArgs = ["clone", "--no-checkout", "--reference-if-able", barePath, "--dissociate", originUrl, worktreePath];

            var clone = await git.RunAsync(new GitCommand(
                Path.GetDirectoryName(worktreePath)!, cloneArgs, _identity, CloneTimeout), ct);
            if (!clone.Succeeded)
            {
                // Tolerant of a lock (git's own children letting go, an indexer): the clone's
                // failure is the answer, never an IOException from tidying up after it.
                await RemoveWorktreeDirectoryAsync(barePath, worktreePath, ct);
                return Result<string>.Failure(clone.Describe());
            }

            // The clone's own config carries this process's identity for the agent's commits (none
            // on a developer's machine left to its own git config) and keeps .orkka/ out of
            // `git add -A`.
            if (_identity is not null)
            {
                await git.RunAsync(new GitCommand(worktreePath, "config", "user.name", _identity.Name), ct);
                await git.RunAsync(new GitCommand(worktreePath, "config", "user.email", _identity.Email), ct);
            }
            EnsureOrkkaExcluded(Path.Combine(worktreePath, ".git"));

            // If the branch already exists on the remote (prior run pushed commits), continue
            // from it instead of starting over from the base branch.
            var checkout = await git.RunAsync(new GitCommand(worktreePath,
                "checkout", "-B", branchName, startPoint), ct);
            if (!checkout.Succeeded)
            {
                await RemoveWorktreeDirectoryAsync(barePath, worktreePath, ct);
                return Result<string>.Failure(checkout.Describe());
            }

            // Point the branch at its OWN remote counterpart. Left alone, git's autoSetupMerge
            // keeps origin/{base} as upstream and every editor reports the task's commits as
            // "1 ahead, N behind" against the base branch forever.
            if (branchExistsRemotely)
            {
                await git.RunAsync(new GitCommand(worktreePath,
                    "branch", $"--set-upstream-to=origin/{branchName}", branchName), ct);
            }
            else
            {
                // --set-upstream-to refuses a ref that does not exist yet (nothing pushed this
                // branch), so write the two config keys it would have written.
                await git.RunAsync(new GitCommand(worktreePath, "config", $"branch.{branchName}.remote", "origin"), ct);
                await git.RunAsync(new GitCommand(worktreePath,
                    "config", $"branch.{branchName}.merge", $"refs/heads/{branchName}"), ct);
            }

            logger.LogInformation("Created workspace clone for task {TaskId} at {Path} from {StartPoint}",
                taskId, worktreePath, startPoint);
            return Result<string>.Success(worktreePath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create worktree for task {TaskId}", taskId);
            return Result<string>.Failure($"Worktree creation failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Backfills a clone that was made partial (blobless) by an earlier version of this class,
    /// so no command in it ever fetches lazily again. The objects come from the LOCAL mirror —
    /// <c>--refetch</c> ignores what the clone thinks it has and re-downloads the reachable
    /// history from the path, no network, no credential — and the promisor configuration is
    /// dropped. Best-effort: a clone this cannot repair is left as it was and logged; the next
    /// operation that needs a missing blob still fails loudly rather than silently.
    /// </summary>
    private async Task RepairPartialCloneAsync(string worktreePath, string barePath, CancellationToken ct)
    {
        if (!IsStandaloneClone(worktreePath) || !Directory.Exists(barePath))
            return;

        var promisor = await git.RunAsync(new GitCommand(worktreePath, "config", "--get", "remote.origin.promisor") { Probe = true }, ct);
        if (!promisor.Succeeded || !string.Equals(promisor.Output, "true", StringComparison.OrdinalIgnoreCase))
            return;

        logger.LogInformation("Backfilling the partial clone at {Path} from the mirror", worktreePath);
        var refetch = await git.RunAsync(new GitCommand(worktreePath,
            ["fetch", "--refetch", "--no-tags", barePath, "+refs/remotes/origin/*:refs/remotes/origin/*"],
            Timeout: CloneTimeout), ct);
        if (!refetch.Succeeded)
        {
            logger.LogWarning("Could not backfill the partial clone at {Path}: {Error}", worktreePath, refetch.Error);
            return;
        }

        await git.RunAsync(new GitCommand(worktreePath, "config", "--unset", "remote.origin.promisor"), ct);
        await git.RunAsync(new GitCommand(worktreePath, "config", "--unset", "remote.origin.partialclonefilter"), ct);
    }

    /// <summary>
    /// True only when git ran and told us this directory is not a repository. An unreadable
    /// answer (git missing, a transient spawn failure) returns false: the caller deletes on a
    /// true, and destroying a working cache over an inconclusive probe is far worse than
    /// leaving a broken one in place for one more attempt.
    /// </summary>
    private async Task<bool> IsBrokenRepositoryAsync(string barePath, CancellationToken ct)
    {
        try
        {
            // --resolve-git-dir inspects THIS directory. Plain rev-parse would walk up to a
            // parent repository and call a broken cache healthy.
            var probe = await git.RunAsync(new GitCommand(barePath, "rev-parse", "--resolve-git-dir", ".") { Probe = true }, ct);
            return !probe.Succeeded && probe.Code == GitErrorCode.NotARepository;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not probe {Path} for a git repository", barePath);
            return false;
        }
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            DeleteDirectoryTree(path);
        }
        catch (Exception ex)
        {
            // The next attempt hits the same guard and tries again. Worth a line, never a throw.
            logger.LogWarning(ex, "Could not remove the unusable repository directory {Path}", path);
        }
    }

    /// <summary>
    /// Deletes a tree that may hold a git object store. Pack files are written read-only, and
    /// on Windows <see cref="Directory.Delete(string, bool)"/> refuses a read-only file — which
    /// is how a standalone clone would outlive its task. Attributes are cleared first.
    /// </summary>
    public static void DeleteDirectoryTree(string path)
    {
        if (!Directory.Exists(path))
            return;

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                if ((File.GetAttributes(file) & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(file, FileAttributes.Normal);
            }
            catch { /* the delete below reports what it cannot remove */ }
        }

        Directory.Delete(path, recursive: true);
    }

    /// <summary>True for a standalone clone (its own <c>.git</c> directory) as opposed to a
    /// legacy linked worktree (a <c>.git</c> file pointing into the mirror).</summary>
    private static bool IsStandaloneClone(string worktreePath)
        => Directory.Exists(Path.Combine(worktreePath, ".git"));

    private void EnsureOrkkaExcluded(string gitDir)
    {
        try
        {
            var infoDir = Path.Combine(gitDir, "info");
            Directory.CreateDirectory(infoDir);
            var excludeFile = Path.Combine(infoDir, "exclude");
            const string rule = ".orkka/";

            if (File.Exists(excludeFile)
                && File.ReadAllLines(excludeFile).Any(l => l.Trim() == rule))
                return;

            File.AppendAllText(excludeFile, $"{Environment.NewLine}{rule}{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            // Worst case the agent commits .orkka/ files — reviewable, not fatal.
            logger.LogWarning(ex, "Could not add .orkka/ to info/exclude in {GitDir}", gitDir);
        }
    }

    public async Task<Result> WriteWorktreeContextAsync(
        Ulid orgId, Ulid repoId, Ulid taskId,
        IReadOnlyList<WorktreeContextFile> files, CancellationToken ct = default)
    {
        var worktreePath = GetWorktreePath(orgId, repoId, taskId);
        if (!Directory.Exists(worktreePath))
            return Result.Failure("Worktree does not exist.");

        try
        {
            var contextRoot = Path.GetFullPath(Path.Combine(worktreePath, ".orkka", "context"));
            Directory.CreateDirectory(contextRoot);

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();

                var fullPath = Path.GetFullPath(Path.Combine(contextRoot, file.RelativePath));
                if (!fullPath.StartsWith(contextRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    return Result.Failure($"Context file path escapes the context folder: {file.RelativePath}");

                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                await File.WriteAllTextAsync(fullPath, file.Content, ct);
            }

            return Result.Success();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write worktree context files for task {TaskId}", taskId);
            return Result.Failure($"Could not write context files: {ex.Message}");
        }
    }

    /// <summary>Wall-clock ceiling for a setup command. A cold `npm ci` is minutes; a wedged
    /// one is forever, and it would hold the phase open behind it.</summary>
    private static readonly TimeSpan SetupTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Output kept from a setup command — enough to diagnose a failure, not enough
    /// to blow up a task timeline with an install log.</summary>
    private const int SetupOutputCap = 8_000;

    public async Task<Result<SetupCommandOutcome>> RunSetupCommandAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string command,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command))
            return new SetupCommandOutcome(Skipped: true, 0, null, 0);

        var worktreePath = GetWorktreePath(orgId, repoId, taskId);
        if (!Directory.Exists(worktreePath))
            return Result<SetupCommandOutcome>.Failure("Worktree does not exist.");

        var markerPath = Path.Combine(worktreePath, ".orkka", "setup");
        var commandHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(command)));

        // A worktree is reused across planning → developing → testing. Re-installing
        // dependencies once per phase would be pure waste, so a recorded success short-circuits.
        // A recorded FAILURE never does — caching "it didn't work" as done would strand the
        // worktree for the rest of the task.
        if (TryReadSetupMarker(markerPath) is { } marker
            && marker.Hash == commandHash && marker.ExitCode == 0)
        {
            return new SetupCommandOutcome(Skipped: true, 0, null, 0);
        }

        var startedAt = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(SetupTimeout);

            var (exitCode, output) = await RunShellAsync(worktreePath, command, timeout.Token);
            startedAt.Stop();

            WriteSetupMarker(markerPath, commandHash, exitCode);

            if (exitCode != 0)
            {
                logger.LogWarning(
                    "Setup command failed (exit {ExitCode}) for task {TaskId} in {RepoId}",
                    exitCode, taskId, repoId);
            }

            return new SetupCommandOutcome(
                Skipped: false, exitCode, Truncate(output, SetupOutputCap),
                startedAt.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            startedAt.Stop();
            // Timed out rather than the caller cancelling. Record it as a failure so the next
            // phase retries instead of inheriting a half-prepared worktree.
            WriteSetupMarker(markerPath, commandHash, exitCode: -1);
            return new SetupCommandOutcome(
                Skipped: false, -1,
                $"Setup command timed out after {SetupTimeout.TotalMinutes:0} minutes.",
                startedAt.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            startedAt.Stop();
            logger.LogWarning(ex, "Setup command errored for task {TaskId}", taskId);
            return Result<SetupCommandOutcome>.Failure($"Setup command could not run: {ex.Message}");
        }
    }

    private sealed record SetupMarker(string Hash, int ExitCode);

    private static SetupMarker? TryReadSetupMarker(string markerPath)
    {
        try
        {
            if (!File.Exists(markerPath))
                return null;
            var parts = File.ReadAllText(markerPath).Split(' ', 2);
            return parts.Length == 2 && int.TryParse(parts[1], out var exitCode)
                ? new SetupMarker(parts[0], exitCode)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private void WriteSetupMarker(string markerPath, string hash, int exitCode)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
            File.WriteAllText(markerPath, $"{hash} {exitCode}");
        }
        catch (Exception ex)
        {
            // Losing the marker costs a redundant re-run, not correctness.
            logger.LogDebug(ex, "Could not write setup marker at {MarkerPath}", markerPath);
        }
    }

    /// <summary>Runs a user-authored command line through the platform shell, merging stdout
    /// and stderr — a failed install says why on stderr, and the two interleaved is what the
    /// user would have seen in their own terminal.</summary>
    /// <remarks>Runs on the developer's machine, inside the runner, after
    /// <c>RunnerOperations</c> has checked the command against the repository's orkka.json
    /// and the developer's approvals — <paramref name="kind"/> mattered there, not here.</remarks>
    public async Task<Result<WorktreeCommandOutcome>> RunWorktreeCommandAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string command, RepoCommandKind kind,
        int timeoutSeconds = 600, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command))
            return Result<WorktreeCommandOutcome>.Failure("No command to run.");

        var worktreePath = GetWorktreePath(orgId, repoId, taskId);
        if (!Directory.Exists(worktreePath))
            return Result<WorktreeCommandOutcome>.Failure("Worktree does not exist.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 30, 1800)));

        var started = DateTimeOffset.UtcNow;
        try
        {
            var (exitCode, output) = await RunShellAsync(worktreePath, command, timeout.Token);
            var duration = (DateTimeOffset.UtcNow - started).TotalSeconds;

            logger.LogInformation(
                "Worktree command for task {TaskId} exited {ExitCode} in {Duration:0}s",
                taskId, exitCode, duration);

            return Result<WorktreeCommandOutcome>.Success(
                new WorktreeCommandOutcome(exitCode, Truncate(output, SetupOutputCap), duration));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Result<WorktreeCommandOutcome>.Failure(
                $"The command did not finish within {timeoutSeconds}s.");
        }
        catch (Exception ex)
        {
            return Result<WorktreeCommandOutcome>.Failure($"The command could not be run: {ex.Message}");
        }
    }

    private static async Task<(int ExitCode, string Output)> RunShellAsync(
        string workingDirectory, string command, CancellationToken ct)
    {
        var (file, args) = OperatingSystem.IsWindows()
            ? ("cmd.exe", $"/c {command}")
            : ("/bin/sh", $"-c \"{command.Replace("\"", "\\\"")}\"");

        var psi = new ProcessStartInfo(file, args)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start the setup command.");

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }

        return (process.ExitCode, string.Concat(await stdout, await stderr));
    }

    private static string Truncate(string value, int maxChars) =>
        value.Length <= maxChars
            ? value
            : value[..(maxChars / 2)] + "\n…\n" + value[^(maxChars / 2)..];

    // ── Merging ──────────────────────────────────────────────────────────────

    public async Task<Result<MergeConflictReport>> GetMergeConflictsAsync(
        Ulid orgId, Ulid repoId, string headBranch, string baseBranch, CancellationToken ct = default)
    {
        using var op = GitOperationScope.Begin(ledger, GitOperationKind.MergeCheck,
            orgId.ToString(), repoId.ToString());

        var barePath = GetBareRepoPath(orgId, repoId);
        if (!Directory.Exists(barePath))
        {
            op.Fail("Bare repo does not exist.", GitErrorCode.Precondition);
            return Result<MergeConflictReport>.Failure("Bare repo does not exist.");
        }

        // Freshest view of both sides — origin is the forge (hosted) or the user's repo (local).
        // A refresh that fails leaves the check running on the mirror as it is: the answer is
        // then "as of the last fetch", which the caller treats as advisory anyway.
        await FetchOriginAsync(barePath, barePath, ct);

        // Prefer the pushed state of the branch; a prior run may have left a stale local ref.
        var remoteHead = await git.RunAsync(new GitCommand(barePath,
            "rev-parse", "--verify", "--quiet", $"refs/remotes/origin/{headBranch}") { Probe = true }, ct);
        var headRef = remoteHead.Succeeded ? $"origin/{headBranch}" : headBranch;

        // Exit 1 is "conflicts", read below — a probe, not a failure to log.
        var mergeTree = await git.RunAsync(new GitCommand(barePath,
            "merge-tree", "--write-tree", "--name-only", $"origin/{baseBranch}", headRef) { Probe = true }, ct);

        switch (mergeTree.ExitCode)
        {
            case 0:
                op.Complete();
                return Result<MergeConflictReport>.Success(new MergeConflictReport(false, [], null));
            case 1:
                op.Complete();
                return Result<MergeConflictReport>.Success(ParseMergeTreeConflicts(mergeTree.Stdout));
            default:
                // Old git without merge-tree --write-tree (< 2.38), bad refs, etc.
                var error = $"merge-tree failed (exit {mergeTree.ExitCode}): {mergeTree.Error}";
                op.Fail(error);
                return Result<MergeConflictReport>.Failure(error);
        }
    }

    /// <summary>
    /// Parses `git merge-tree --write-tree --name-only` conflict output:
    /// tree OID, conflicted paths, blank line, then informational CONFLICT messages.
    /// </summary>
    internal static MergeConflictReport ParseMergeTreeConflicts(string output)
    {
        var lines = output.Replace("\r\n", "\n").Split('\n');
        var paths = new List<string>();
        var messages = new List<string>();
        var section = 0; // 0 = tree OID line, 1 = conflicted paths, 2 = messages

        foreach (var line in lines)
        {
            if (section == 0) { section = 1; continue; }
            if (section == 1)
            {
                if (line.Trim().Length == 0) { section = 2; continue; }
                paths.Add(line.Trim());
                continue;
            }
            if (line.Trim().Length > 0)
                messages.Add(line.Trim());
        }

        var files = paths.Distinct(StringComparer.Ordinal)
            .Select(p => new MergeConflictFile(
                p,
                string.Join(" ", messages.Where(m => m.Contains(p, StringComparison.Ordinal)))))
            .ToList();

        return new MergeConflictReport(true, files, string.Join("\n", messages));
    }

    public async Task<Result<MergeStrategyOutcome>> ApplyMergeStrategyAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string baseBranch, string strategy,
        IReadOnlyDictionary<string, string>? fileChoices, CancellationToken ct = default)
    {
        using var op = GitOperationScope.Begin(ledger, GitOperationKind.ApplyStrategy,
            orgId.ToString(), repoId.ToString(), taskId.ToString());
        var result = await ApplyMergeStrategyCoreAsync(orgId, repoId, taskId, baseBranch, strategy, fileChoices, ct);
        if (result.IsSuccess) op.Complete(); else op.Fail(result.Error);
        return result;
    }

    private async Task<Result<MergeStrategyOutcome>> ApplyMergeStrategyCoreAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string baseBranch, string strategy,
        IReadOnlyDictionary<string, string>? fileChoices, CancellationToken ct)
    {
        var worktreePath = GetWorktreePath(orgId, repoId, taskId);
        if (!Directory.Exists(worktreePath))
            return Result<MergeStrategyOutcome>.Failure("Worktree does not exist.");

        var barePath = GetBareRepoPath(orgId, repoId);
        var fetch = await FetchOriginAsync(worktreePath, barePath, ct);
        if (fetch.IsFailure)
            return Result<MergeStrategyOutcome>.Failure(fetch.Error!);

        // A previous attempt may have left a merge half-finished — start clean.
        await git.RunAsync(new GitCommand(worktreePath, "merge", "--abort") { Probe = true }, ct);

        // In the task worktree, "ours" is the task branch and "theirs" is the incoming base.
        List<string> mergeArgs = ["merge"];
        switch (strategy)
        {
            case "task": mergeArgs.AddRange(["-X", "ours"]); break;
            case "current": mergeArgs.AddRange(["-X", "theirs"]); break;
        }
        mergeArgs.AddRange(["--no-edit", $"origin/{baseBranch}"]);

        // A conflicting merge exits 1 by design; the unmerged-path read below decides.
        var merge = await git.RunAsync(new GitCommand(worktreePath, mergeArgs, Identity: _identity) { Probe = true }, ct);
        if (merge.Succeeded)
            return Result<MergeStrategyOutcome>.Success(new MergeStrategyOutcome(true, []));

        // -X ours/theirs resolves content conflicts but not modify/delete or add/add-binary —
        // finish those (and per-file picks) by taking the chosen side explicitly.
        var unmergedResult = await git.RunAsync(new GitCommand(worktreePath, "diff", "--name-only", "--diff-filter=U"), ct);
        var unmerged = SplitLines(unmergedResult.Stdout);

        if (!unmergedResult.Succeeded || unmerged.Count == 0)
        {
            await git.RunAsync(new GitCommand(worktreePath, "merge", "--abort") { Probe = true }, ct);
            return Result<MergeStrategyOutcome>.Failure(
                $"Merge failed without resolvable conflicts: {merge.Error}");
        }

        var remaining = new List<string>();
        foreach (var file in unmerged)
        {
            var choice = strategy switch
            {
                "task" => "task",
                "current" => "current",
                _ => fileChoices?.GetValueOrDefault(file) ?? "ai",
            };
            if (choice == "ai")
            {
                remaining.Add(file);
                continue;
            }

            var side = choice == "task" ? "--ours" : "--theirs";
            var checkout = await git.RunAsync(new GitCommand(worktreePath, "checkout", side, "--", file), ct);
            if (!checkout.Succeeded)
            {
                // The chosen side deleted the file (modify/delete conflict) — honor the deletion.
                await git.RunAsync(new GitCommand(worktreePath, "rm", "-f", "--", file), ct);
            }
            else
            {
                await git.RunAsync(new GitCommand(worktreePath, "add", "--", file), ct);
            }
        }

        if (remaining.Count > 0)
        {
            // Merge stays in progress: the resolver agent finishes these files and commits.
            logger.LogInformation(
                "Merge strategy '{Strategy}' applied for task {TaskId}; {Count} file(s) left for AI resolution",
                strategy, taskId, remaining.Count);
            return Result<MergeStrategyOutcome>.Success(new MergeStrategyOutcome(false, remaining));
        }

        var commit = await git.RunAsync(new GitCommand(worktreePath, ["commit", "--no-edit"], Identity: _identity), ct);
        if (!commit.Succeeded)
            return Result<MergeStrategyOutcome>.Failure($"Could not commit the merge: {commit.Describe()}");

        logger.LogInformation("Merge strategy '{Strategy}' fully applied for task {TaskId}", strategy, taskId);
        return Result<MergeStrategyOutcome>.Success(new MergeStrategyOutcome(true, []));
    }

    /// <summary>
    /// Writes this process's commit identity into a mirror's config. Worktrees share the
    /// mirror's config, so this is what makes an agent's `git commit` inside a worktree carry it.
    /// With no identity (a developer's machine left to its own git config) any identity an
    /// older runner wrote is removed, so the developer's own config shows through.
    /// </summary>
    private async Task<Result<string>> ApplyCommitIdentityAsync(string barePath, CancellationToken ct)
    {
        if (_identity is null)
        {
            await git.RunAsync(new GitCommand(barePath, "config", "--unset-all", "user.name") { Probe = true }, ct);
            await git.RunAsync(new GitCommand(barePath, "config", "--unset-all", "user.email") { Probe = true }, ct);
            return Result<string>.Success(barePath);
        }

        var name = await git.RunAsync(new GitCommand(barePath, "config", "user.name", _identity.Name), ct);
        var email = await git.RunAsync(new GitCommand(barePath, "config", "user.email", _identity.Email), ct);
        if (name.Succeeded && email.Succeeded)
            return Result<string>.Success(barePath);

        var failed = name.Succeeded ? email : name;
        logger.LogWarning("Could not set the commit identity on {BarePath}: {Error}", barePath, failed.Error);
        return Result<string>.Failure(
            $"Could not write the platform commit identity into the repository cache: {failed.Describe()}");
    }

    public async Task<Result<BaseSyncOutcome>> SyncBaseBranchAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string baseBranch,
        bool leaveConflictsInProgress, bool commitLeftovers = false,
        CancellationToken ct = default)
    {
        using var op = GitOperationScope.Begin(ledger, GitOperationKind.SyncBase,
            orgId.ToString(), repoId.ToString(), taskId.ToString());
        var result = await SyncBaseBranchCoreAsync(orgId, repoId, taskId, baseBranch,
            leaveConflictsInProgress, commitLeftovers, ct);
        if (result.IsSuccess) op.Complete(); else op.Fail(result.Error);
        return result;
    }

    private async Task<Result<BaseSyncOutcome>> SyncBaseBranchCoreAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string baseBranch,
        bool leaveConflictsInProgress, bool commitLeftovers, CancellationToken ct)
    {
        var worktreePath = GetWorktreePath(orgId, repoId, taskId);
        if (!Directory.Exists(worktreePath))
            return Result<BaseSyncOutcome>.Failure("Worktree does not exist.");

        // A half-finished merge from an earlier attempt would make every check below lie about
        // what is in the tree. Checked before aborting rather than aborted blindly: no stale
        // merge is the overwhelmingly common case.
        var staleMerge = await git.RunAsync(new GitCommand(worktreePath, "rev-parse", "-q", "--verify", "MERGE_HEAD") { Probe = true }, ct);
        if (staleMerge.Succeeded)
            await git.RunAsync(new GitCommand(worktreePath, "merge", "--abort") { Probe = true }, ct);

        var status = await git.RunAsync(new GitCommand(worktreePath, "status", "--porcelain"), ct);
        if (!status.Succeeded)
            return Result<BaseSyncOutcome>.Failure(status.Describe());
        if (!string.IsNullOrWhiteSpace(status.Stdout))
        {
            // At a phase boundary, uncommitted work is what a run that ended without its
            // publish step (a crash, a killed pod) left behind — commit it as the platform
            // rather than refuse the sync over it. Mid-run (the agent's own check_base_updates
            // tool) the agent still owns its working tree and the sync must not touch it.
            if (!commitLeftovers)
            {
                return Result<BaseSyncOutcome>.Failure(
                    $"{GitErrorCode.DirtyWorkingTree}: The worktree has uncommitted changes. Commit them before merging the base branch.");
            }

            var leftovers = await CommitLeftoversAsync(worktreePath,
                "Commit changes left uncommitted by the previous run", ct);
            if (leftovers.IsFailure)
                return Result<BaseSyncOutcome>.Failure(leftovers.Error!);
        }

        var barePath = GetBareRepoPath(orgId, repoId);
        var fetch = await FetchOriginAsync(worktreePath, barePath, ct);
        if (fetch.IsFailure)
            return Result<BaseSyncOutcome>.Failure(fetch.Error!);

        // Nothing to do is the common case — most phase transitions happen while the base is
        // still where the branch left it, and merging anyway would add an empty merge commit.
        var behindResult = await git.RunAsync(new GitCommand(worktreePath,
            "rev-list", "--count", $"HEAD..origin/{baseBranch}"), ct);
        if (!behindResult.Succeeded)
            return Result<BaseSyncOutcome>.Failure(behindResult.Describe());
        if (!int.TryParse(behindResult.Output, out var behind))
            return Result<BaseSyncOutcome>.Failure($"Could not read how far behind origin/{baseBranch} the branch is.");
        if (behind == 0)
            return Result<BaseSyncOutcome>.Success(new BaseSyncOutcome(BaseSyncStatus.AlreadyCurrent, [], 0));

        // A conflicting merge exits 1 by design and is reported as Conflicted below.
        var merge = await git.RunAsync(new GitCommand(worktreePath,
            ["merge", "--no-edit", $"origin/{baseBranch}"], _identity) { Probe = true }, ct);
        if (merge.Succeeded)
        {
            logger.LogInformation(
                "Task {TaskId}: merged {Behind} commit(s) from origin/{BaseBranch} into the worktree",
                taskId, behind, baseBranch);
            return Result<BaseSyncOutcome>.Success(new BaseSyncOutcome(BaseSyncStatus.Merged, [], behind));
        }

        var unmerged = await git.RunAsync(new GitCommand(worktreePath, "diff", "--name-only", "--diff-filter=U"), ct);
        var conflicted = SplitLines(unmerged.Stdout);

        if (conflicted.Count == 0)
        {
            // The merge refused to start rather than colliding (detached HEAD, missing ref, a
            // hook). Nothing is half-applied, but abort so a partial index cannot survive.
            await git.RunAsync(new GitCommand(worktreePath, "merge", "--abort") { Probe = true }, ct);
            return Result<BaseSyncOutcome>.Failure($"Merging origin/{baseBranch} failed: {merge.Describe()}");
        }

        if (!leaveConflictsInProgress)
        {
            await git.RunAsync(new GitCommand(worktreePath, "merge", "--abort") { Probe = true }, ct);
            logger.LogInformation(
                "Task {TaskId}: origin/{BaseBranch} conflicts with the branch in {Count} file(s) — merge rolled back",
                taskId, baseBranch, conflicted.Count);
            return Result<BaseSyncOutcome>.Success(
                new BaseSyncOutcome(BaseSyncStatus.Aborted, conflicted, behind));
        }

        logger.LogInformation(
            "Task {TaskId}: origin/{BaseBranch} conflicts in {Count} file(s) — merge left in progress for the agent",
            taskId, baseBranch, conflicted.Count);
        return Result<BaseSyncOutcome>.Success(
            new BaseSyncOutcome(BaseSyncStatus.Conflicted, conflicted, behind));
    }

    /// <summary>Stages everything and commits it as the platform. Refuses when the tree still
    /// has unresolved conflicts — markers must never become a commit.</summary>
    private async Task<Result> CommitLeftoversAsync(string worktreePath, string message, CancellationToken ct)
    {
        var unmerged = await git.RunAsync(new GitCommand(worktreePath, "diff", "--name-only", "--diff-filter=U"), ct);
        var conflicted = SplitLines(unmerged.Stdout);
        if (conflicted.Count > 0)
        {
            return Result.Failure(
                $"{GitErrorCode.MergeConflict}: The worktree has {conflicted.Count} file(s) with unresolved conflicts: "
                + string.Join(", ", conflicted.Take(10)));
        }

        var add = await git.RunAsync(new GitCommand(worktreePath, "add", "-A"), ct);
        if (!add.Succeeded)
            return Result.Failure(add.Describe());

        var commit = await git.RunAsync(new GitCommand(worktreePath, ["commit", "-m", message], Identity: _identity), ct);
        return commit.Succeeded ? Result.Success() : Result.Failure(commit.Describe());
    }

    /// <summary>Conflict-marker prefixes, checked at line starts only. `=======` is deliberately
    /// excluded: it is a legitimate line in markdown and in plenty of ASCII banners, and the two
    /// side markers are enough to identify a real unresolved hunk.</summary>
    private static readonly string[] ConflictMarkers = ["<<<<<<< ", ">>>>>>> "];

    public async Task<Result<MergeVerification>> VerifyMergeAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string baseBranch, CancellationToken ct = default)
    {
        using var op = GitOperationScope.Begin(ledger, GitOperationKind.VerifyMerge,
            orgId.ToString(), repoId.ToString(), taskId.ToString());

        var worktreePath = GetWorktreePath(orgId, repoId, taskId);
        if (!Directory.Exists(worktreePath))
        {
            op.Fail("Worktree does not exist.", GitErrorCode.Precondition);
            return Result<MergeVerification>.Failure("Worktree does not exist.");
        }

        var unmergedResult = await git.RunAsync(new GitCommand(worktreePath, "diff", "--name-only", "--diff-filter=U"), ct);
        if (!unmergedResult.Succeeded)
        {
            op.Fail(unmergedResult.Describe());
            return Result<MergeVerification>.Failure(unmergedResult.Describe());
        }

        var unmerged = SplitLines(unmergedResult.Stdout);

        // Does the branch actually contain the base now? A resolver that ran `git merge --abort`
        // leaves a spotlessly clean worktree that has resolved nothing. A fetch that fails leaves
        // the question answered against the last fetched base — verification then reads "not
        // merged" when the base moved, which errs on the safe side.
        var barePath = GetBareRepoPath(orgId, repoId);
        await FetchOriginAsync(worktreePath, barePath, ct);
        var behindResult = await git.RunAsync(new GitCommand(worktreePath,
            "rev-list", "--count", $"HEAD..origin/{baseBranch}"), ct);
        var baseMerged = behindResult.Succeeded
            && int.TryParse(behindResult.Output, out var behind)
            && behind == 0;

        // Markers in COMMITTED content are the case worth paying for: the index is clean, git
        // calls the merge finished, and nothing else downstream will look before it lands.
        var withMarkers = await FindConflictMarkersAsync(worktreePath, ct);

        var completed = unmerged.Count == 0 && withMarkers.Count == 0 && baseMerged;
        if (!completed)
        {
            logger.LogWarning(
                "Task {TaskId} merge verification failed — {Unmerged} unmerged, {Markers} file(s) with markers, baseMerged={BaseMerged}",
                taskId, unmerged.Count, withMarkers.Count, baseMerged);
        }

        op.Complete();
        return Result<MergeVerification>.Success(
            new MergeVerification(completed, unmerged, withMarkers, baseMerged));
    }

    /// <summary>
    /// Tracked files whose content still carries conflict markers. Uses `git grep` so the scan
    /// covers exactly the tracked tree — no node_modules, no build output, no .git — and costs
    /// one process instead of a recursive walk.
    /// </summary>
    private async Task<List<string>> FindConflictMarkersAsync(string worktreePath, CancellationToken ct)
    {
        var found = new List<string>();
        foreach (var marker in ConflictMarkers)
        {
            // Exit 1 = no matches, which is the good case and not an error.
            var grep = await git.RunAsync(new GitCommand(worktreePath,
                "grep", "--name-only", "--fixed-strings", "-e", marker) { Probe = true }, ct);
            if (grep.ExitCode == 0)
                found.AddRange(SplitLines(grep.Stdout));
        }

        return [.. found.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    private static List<string> SplitLines(string? value) =>
        [.. (value ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)];

    // ── Publishing ───────────────────────────────────────────────────────────

    public async Task<Result> CommitAndPushAsync(
        string worktreePath, string message, string branchName, CancellationToken ct = default)
    {
        using var op = GitOperationScope.Begin(ledger, GitOperationKind.Publish);
        var result = await CommitAndPushCoreAsync(worktreePath, message, branchName, ct);
        if (result.IsSuccess) op.Complete(); else op.Fail(result.Error);
        return result;
    }

    private async Task<Result> CommitAndPushCoreAsync(
        string worktreePath, string message, string branchName, CancellationToken ct)
    {
        try
        {
            var add = await git.RunAsync(new GitCommand(worktreePath, "add", "-A"), ct);
            if (!add.Succeeded)
                return Result.Failure(add.Describe());

            var status = await git.RunAsync(new GitCommand(worktreePath, "status", "--porcelain"), ct);
            if (status.Succeeded && string.IsNullOrWhiteSpace(status.Stdout))
            {
                logger.LogInformation("No changes to commit in {Path}", worktreePath);
                return Result.Success();
            }

            var commit = await git.RunAsync(new GitCommand(worktreePath, ["commit", "-m", message], Identity: _identity), ct);
            if (!commit.Succeeded)
                return Result.Failure(commit.Describe());

            var push = await git.RunAsync(new GitCommand(worktreePath, "push", "origin", branchName), ct);
            if (!push.Succeeded)
                return Result.Failure(push.Describe());

            logger.LogInformation("Committed and pushed branch {Branch}", branchName);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to commit and push in {Path}", worktreePath);
            return Result.Failure($"Commit/push failed: {ex.Message}");
        }
    }

    public async Task<Result> EnsurePushedAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string branchName, CancellationToken ct = default)
    {
        using var op = GitOperationScope.Begin(ledger, GitOperationKind.Push,
            orgId.ToString(), repoId.ToString(), taskId.ToString());

        var worktreePath = GetWorktreePath(orgId, repoId, taskId);
        if (!Directory.Exists(worktreePath))
        {
            op.Fail("Worktree does not exist", GitErrorCode.Precondition);
            return Result.Failure("Worktree does not exist");
        }

        try
        {
            var push = await git.RunAsync(new GitCommand(worktreePath, "push", "origin", branchName), ct);
            if (!push.Succeeded)
            {
                op.Fail(push.Describe(), push.Code);
                return Result.Failure(push.Describe());
            }

            op.Complete();
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to ensure pushed for task {TaskId}", taskId);
            op.Fail(ex.Message);
            return Result.Failure($"Push failed: {ex.Message}");
        }
    }

    // ── Cleanup ──────────────────────────────────────────────────────────────

    public async Task CleanupWorktreeAsync(Ulid orgId, Ulid repoId, Ulid taskId)
    {
        using var op = GitOperationScope.Begin(ledger, GitOperationKind.Cleanup,
            orgId.ToString(), repoId.ToString(), taskId.ToString());

        var barePath = GetBareRepoPath(orgId, repoId);
        var worktreePath = GetWorktreePath(orgId, repoId, taskId);

        try
        {
            var freed = true;
            if (Directory.Exists(worktreePath))
            {
                freed = await RemoveWorktreeDirectoryAsync(barePath, worktreePath, CancellationToken.None);
                if (freed)
                    logger.LogInformation("Cleaned up worktree for task {TaskId}", taskId);
                else
                    logger.LogWarning(
                        "Worktree for task {TaskId} is locked — leaving it for CreateWorktree recovery or stale-worktree GC",
                        taskId);
            }

            // Drop the name marker only once the folder is actually gone. That marker is the
            // ONLY thing telling the GC a friendly-named folder ("WAV-25") belongs to Orkka,
            // so deleting it while a locked folder survives strands that folder permanently.
            if (freed)
            {
                TryDeleteWorktreeName(orgId, repoId, taskId);
                op.Complete();
            }
            else
            {
                op.Fail("The worktree is locked by another process.", GitErrorCode.Locked);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to cleanup worktree for task {TaskId}", taskId);
            op.Fail(ex.Message);
        }
    }

    // Delete retries: the previous run's agent process can outlive its orchestration by a
    // few seconds on Windows and hold a cwd handle inside the worktree, failing deletes
    // with a sharing violation. Observed locks clear within seconds — ~10s of backoff.
    private static readonly TimeSpan[] DeleteRetryDelays =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4)];

    /// <summary>
    /// Unregisters and deletes a worktree directory, tolerating transient file locks.
    /// Retries the delete with backoff; if the tree stays locked, renames it aside to a
    /// tombstone folder (atomic — frees the original path for recreation) and deletes the
    /// tombstone best-effort. Returns true when the original path is free.
    /// </summary>
    private async Task<bool> RemoveWorktreeDirectoryAsync(
        string barePath, string worktreePath, CancellationToken ct)
    {
        // A legacy linked worktree must be unregistered from the mirror; a standalone clone
        // has no registration and the command fails harmlessly.
        // A `.git` FILE is what a linked worktree has. Not "anything that is not a standalone
        // clone": a clone whose first delete got as far as its `.git` folder before a locked
        // file stopped it has no `.git` at all, and asking git to unregister it only logged
        // "is not a working tree" on every retry (2026-09-17).
        if (Directory.Exists(barePath) && File.Exists(Path.Combine(worktreePath, ".git")))
            await git.RunAsync(new GitCommand(barePath, "worktree", "remove", worktreePath, "--force"), ct);

        var freed = !Directory.Exists(worktreePath);

        if (!freed)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    DeleteDirectoryTree(worktreePath);
                    freed = true;
                    break;
                }
                catch (DirectoryNotFoundException)
                {
                    freed = true;
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt >= DeleteRetryDelays.Length)
                        break;
                    // The task is finished and its folder is going; whatever still runs from
                    // inside it is an orphan of a run that ended — a dev server or bundler
                    // service an agent started in the background. Retrying the delete never
                    // outlasts those: every merged storefront-ui task left its worktree behind
                    // (2026-09-17). End them once, then retry.
                    if (attempt == 0 && OperatingSystem.IsWindows())
                        await WorktreeProcessSweeper.EndHoldersAsync(worktreePath, logger, ct);
                    logger.LogInformation(
                        "Worktree {Path} is locked ({Reason}) — retrying delete in {Delay}s (attempt {Attempt}/{Max})",
                        worktreePath, ex.Message.Trim(), DeleteRetryDelays[attempt].TotalSeconds,
                        attempt + 1, DeleteRetryDelays.Length + 1);
                    await Task.Delay(DeleteRetryDelays[attempt], ct);
                }
            }
        }

        if (!freed)
        {
            // Last resort: rename aside. Same parent folder keeps it a same-volume atomic
            // rename; the "task-" prefix makes stale-worktree GC treat leftovers as ours.
            var tombstone = Path.Combine(
                Path.GetDirectoryName(worktreePath)!, $"task-tombstone-{Ulid.NewUlid()}");
            try
            {
                Directory.Move(worktreePath, tombstone);
                logger.LogWarning("Worktree {Path} stayed locked — moved aside to {Tombstone}",
                    worktreePath, tombstone);
                freed = true;
                try { DeleteDirectoryTree(tombstone); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Stale-worktree GC sweeps task-* folders — the tombstone goes eventually.
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Worktree {Path} is locked and could not be moved aside", worktreePath);
            }
        }

        // Registration for a missing directory would make `worktree add` at the same
        // path fail; prune drops it (also covers the moved-aside tombstone).
        if (Directory.Exists(barePath))
            await git.RunAsync(new GitCommand(barePath, "worktree", "prune"), ct);

        return freed;
    }

    public async Task<int> CleanupStaleWorktreesAsync(TimeSpan maxAge, CancellationToken ct = default)
    {
        if (!Directory.Exists(_basePath))
            return 0;

        var cutoff = DateTime.UtcNow - maxAge;
        var removed = 0;

        // Layout: {basePath}/{orgId}/{repoId}/worktrees/task-{taskId}, bare at {..}/{repoId}/bare.
        // Local-source repos additionally place worktrees in a {repo}-worktrees sibling folder
        // next to the source repo (see GetWorktreePath) — both roots are swept.
        foreach (var orgDir in SafeEnumerateDirectories(_basePath))
        {
            foreach (var repoDir in SafeEnumerateDirectories(orgDir))
            {
                var barePath = Path.Combine(repoDir, "bare");
                string?[] roots = [Path.Combine(repoDir, "worktrees"), ExternalWorktreesRoot(repoDir)];
                var namedWorktrees = LoadNamedWorktrees(repoDir);

                foreach (var worktreesRoot in roots)
                {
                    if (worktreesRoot is null || !Directory.Exists(worktreesRoot))
                        continue;

                    foreach (var worktreePath in SafeEnumerateDirectories(worktreesRoot))
                    {
                        ct.ThrowIfCancellationRequested();

                        // Only touch Orkka-created task folders — external roots sit next to
                        // the user's repo where anything else must be left alone.
                        if (!IsOrkkaWorktreeFolder(worktreePath, namedWorktrees))
                            continue;

                        DateTime lastWrite;
                        try { lastWrite = Directory.GetLastWriteTimeUtc(worktreePath); }
                        catch { continue; }

                        if (lastWrite > cutoff)
                            continue;

                        try
                        {
                            await RemoveWorktreeDirectoryAsync(barePath, worktreePath, ct);
                            removed++;
                            logger.LogInformation("GC removed stale worktree {Path} (last write {LastWrite:u})",
                                worktreePath, lastWrite);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            logger.LogWarning(ex, "GC failed to remove stale worktree {Path}", worktreePath);
                        }
                    }

                    TryDeleteIfEmpty(worktreesRoot);
                }
            }
        }

        if (removed > 0)
            logger.LogInformation("Worktree GC removed {Count} stale worktree(s)", removed);

        return removed;
    }

    public IReadOnlyList<(Ulid OrgId, Ulid RepoId)> EnumerateRepoWorkspaces()
    {
        var result = new List<(Ulid, Ulid)>();
        if (!Directory.Exists(_basePath))
            return result;

        foreach (var orgDir in SafeEnumerateDirectories(_basePath))
        {
            if (!Ulid.TryParse(Path.GetFileName(orgDir), out var orgId))
                continue;

            foreach (var repoDir in SafeEnumerateDirectories(orgDir))
            {
                if (Ulid.TryParse(Path.GetFileName(repoDir), out var repoId))
                    result.Add((orgId, repoId));
            }
        }

        return result;
    }

    public async Task RemoveRepoWorkspaceAsync(Ulid orgId, Ulid repoId, CancellationToken ct = default)
    {
        var repoDir = Path.Combine(_basePath, orgId.ToString(), repoId.ToString());
        if (!Directory.Exists(repoDir))
            return;

        using var op = GitOperationScope.Begin(ledger, GitOperationKind.Cleanup,
            orgId.ToString(), repoId.ToString());
        try
        {
            // Detach any registered worktrees first so git doesn't hold locks, then remove the tree.
            var barePath = GetBareRepoPath(orgId, repoId);
            var externalRoot = ExternalWorktreesRoot(repoDir);
            string?[] roots = [Path.Combine(repoDir, "worktrees"), externalRoot];
            var namedWorktrees = LoadNamedWorktrees(repoDir);
            foreach (var worktreesRoot in roots)
            {
                if (worktreesRoot is null || !Directory.Exists(barePath) || !Directory.Exists(worktreesRoot))
                    continue;

                foreach (var worktreePath in SafeEnumerateDirectories(worktreesRoot))
                {
                    if (!IsOrkkaWorktreeFolder(worktreePath, namedWorktrees))
                        continue;
                    await RemoveWorktreeDirectoryAsync(barePath, worktreePath, ct);
                }
            }

            if (externalRoot is not null)
                TryDeleteIfEmpty(externalRoot);

            DeleteDirectoryTree(repoDir);
            logger.LogInformation("Removed repo workspace for repo {RepoId} (org {OrgId})", repoId, orgId);
            op.Complete();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to remove repo workspace for repo {RepoId} (org {OrgId})", repoId, orgId);
            op.Fail(ex.Message);
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        try { return Directory.EnumerateDirectories(path); }
        catch { return []; }
    }

    private static void TryDeleteIfEmpty(string path)
    {
        try
        {
            if (!Directory.Exists(path))
                return;

            // A worktrees root holding ONLY its dotenv guard counts as empty — the
            // guard is Orkka's own file (marker-checked before deletion). While any
            // worktree remains the guard must stay: it is what keeps the gemini CLI's
            // dotenv walk from escaping the root.
            var entries = Directory.EnumerateFileSystemEntries(path).Take(2).ToList();
            if (entries.Count == 1 && Path.GetFileName(entries[0]) == ".env")
                Agents.GeminiDotenvGuard.TryDeleteGuard(path);

            if (!Directory.EnumerateFileSystemEntries(path).Any())
                Directory.Delete(path);
        }
        catch
        {
            // Leftover empty folder is harmless.
        }
    }

    private static long DirectorySizeBytes(string path)
    {
        try
        {
            return new DirectoryInfo(path)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);
        }
        catch
        {
            return 0;
        }
    }

    // ── Fetching ─────────────────────────────────────────────────────────────

    /// <summary>
    /// One gate per bare repo, so only one fetch at a time touches a given repo's refs.
    ///
    /// Every worktree of a repo SHARES that repo's <c>refs/remotes/*</c>. Two tasks fetching at
    /// once are therefore two processes racing to compare-and-swap the same ref, and the loser
    /// gets "cannot lock ref … is at X but expected Y". Keyed by bare path, so unrelated repos
    /// still fetch in parallel — the contention this removes is per repo, not global.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> FetchGates =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan[] FetchRetryDelays =
        [TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(750), TimeSpan.FromMilliseconds(2000)];

    /// <summary>Ref names out of git's "cannot lock ref 'refs/remotes/origin/main'" text.</summary>
    private static readonly System.Text.RegularExpressions.Regex LockedRefPattern =
        new(@"cannot lock ref '([^']+)'", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// The only way this class fetches. Serializes per repo, retries the ref-lock collision that
    /// concurrency still produces across processes (the local host and the runner both drive the
    /// same mirror, and a lock in one of them cannot bind the other), and repairs a ref store
    /// that has genuinely diverged rather than leaving every future fetch to fail the same way.
    /// </summary>
    /// <param name="workingDirectory">Where git runs — the bare repo or one of its worktrees.</param>
    /// <param name="barePath">The shared ref store both of those resolve to. The gate key.</param>
    private async Task<Result<string>> FetchOriginAsync(
        string workingDirectory, string barePath, CancellationToken ct,
        IReadOnlyList<string>? arguments = null)
    {
        arguments ??= ["fetch", "origin"];

        // A standalone clone has its own ref store: nothing else fetches into it, so it takes
        // its own gate and never queues behind a sibling task. Legacy linked worktrees and the
        // mirror itself still share the mirror's refs and serialize on it.
        var gateKey = workingDirectory != barePath && IsStandaloneClone(workingDirectory) ? workingDirectory : barePath;
        var gate = FetchGates.GetOrAdd(gateKey, _ => new SemaphoreSlim(1, 1));

        // Bounded on both sides of the gate. Waiting: a queue of tasks on a busy repo must not
        // stall a phase forever. Running: a dead connection can block git indefinitely, and
        // while the gate is held that would stall every OTHER task on this repo too.
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        waitCts.CancelAfter(FetchGateWaitTimeout);
        try
        {
            await gate.WaitAsync(waitCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Result<string>.Failure(
                $"{GitErrorCode.Timeout}: Timed out waiting to fetch {barePath} — another task is still fetching this repository.");
        }

        try
        {
            var result = await RunFetchAsync(workingDirectory, arguments, ct);

            for (var attempt = 0; attempt < FetchRetryDelays.Length; attempt++)
            {
                if (result.Succeeded || result.Code != GitErrorCode.RefLock)
                    return ToResult(result);

                logger.LogInformation(
                    "Fetch for {BarePath} lost a ref race (attempt {Attempt}/{Max}) — retrying",
                    barePath, attempt + 1, FetchRetryDelays.Length);
                await Task.Delay(FetchRetryDelays[attempt], ct);
                result = await RunFetchAsync(workingDirectory, arguments, ct);
            }

            if (result.Succeeded || result.Code != GitErrorCode.RefLock)
                return ToResult(result);

            // Every retry lost, so this is no longer a race: the ref store itself disagrees with
            // what fetch expects (a loose ref and packed-refs holding different values, typically
            // after a fetch was killed mid-write). Left alone it fails identically forever.
            var repairedRefs = await RepairRemoteRefsAsync(workingDirectory, result.Error, ct);
            if (repairedRefs == 0)
                return ToResult(result);

            logger.LogWarning(
                "Fetch for {BarePath} still failed after retries — dropped {Count} diverged remote ref(s) and refetching",
                barePath, repairedRefs);
            return ToResult(await RunFetchAsync(workingDirectory, [.. arguments, "--force"], ct));
        }
        finally
        {
            gate.Release();
        }

        static Result<string> ToResult(GitCommandResult r)
            => r.Succeeded ? Result<string>.Success(r.Output) : Result<string>.Failure(r.Describe());
    }

    /// <summary>How long a task waits for its turn to fetch a repo before giving up.</summary>
    private static readonly TimeSpan FetchGateWaitTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Ceiling on a single fetch. Credentials never prompt, so a fetch that runs this
    /// long is not going to finish.</summary>
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromMinutes(5);

    private Task<GitCommandResult> RunFetchAsync(
        string workingDirectory, IReadOnlyList<string> arguments, CancellationToken ct)
        => git.RunAsync(new GitCommand(workingDirectory, arguments, Timeout: FetchTimeout), ct);

    /// <summary>
    /// Deletes exactly the remote-tracking refs git named in its failure, so the next fetch
    /// rebuilds them from the remote. Surgical on purpose: these are remote-tracking refs, always
    /// re-derivable from origin, but anything broader would be deleting refs on a hunch.
    /// </summary>
    private async Task<int> RepairRemoteRefsAsync(string workingDirectory, string? error, CancellationToken ct)
    {
        var dropped = 0;
        foreach (System.Text.RegularExpressions.Match match in LockedRefPattern.Matches(error ?? ""))
        {
            var refName = match.Groups[1].Value;

            // Never touch a local branch on the strength of a parsed error string — a task's own
            // commits live there, and they are not recoverable from the remote.
            if (!refName.StartsWith("refs/remotes/", StringComparison.Ordinal))
            {
                logger.LogWarning("Refusing to delete non-remote ref {Ref} during fetch repair", refName);
                continue;
            }

            var delete = await git.RunAsync(new GitCommand(workingDirectory, "update-ref", "-d", refName), ct);
            if (delete.Succeeded) dropped++;
            else logger.LogWarning("Could not drop diverged ref {Ref}: {Error}", refName, delete.Error);
        }

        return dropped;
    }

    // ── Diffs ────────────────────────────────────────────────────────────────

    public async Task<Result<string>> GetDiffAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string baseBranch,
        int maxChars = 60_000, string? headBranch = null, CancellationToken ct = default)
    {
        var worktreePath = GetWorktreePath(orgId, repoId, taskId);
        if (Directory.Exists(worktreePath))
        {
            var result = await git.RunAsync(new GitCommand(worktreePath,
                ["diff", $"origin/{baseBranch}...HEAD", .. DiffExcludePathspec]), ct);
            if (result.Succeeded)
                return Result<string>.Success(Truncate(
                    await WithExcludedFilesNoteAsync(worktreePath, $"origin/{baseBranch}...HEAD", result.Stdout, ct),
                    maxChars));
            // Corrupt worktree (deleted bare, broken gitdir link) — try the bare fallback.
        }

        // Worktree gone or unusable: PR diffs must still render, so diff the pushed
        // branch against the base in the bare mirror instead.
        if (string.IsNullOrEmpty(headBranch))
            return Result<string>.Failure("Worktree does not exist");

        var barePath = GetBareRepoPath(orgId, repoId);
        if (!Directory.Exists(barePath))
            return Result<string>.Failure("Worktree and bare mirror do not exist");

        // Refresh refs so a branch pushed from elsewhere resolves.
        await FetchOriginAsync(barePath, barePath, ct);

        var remoteHead = await git.RunAsync(new GitCommand(barePath,
            "rev-parse", "--verify", "--quiet", $"refs/remotes/origin/{headBranch}") { Probe = true }, ct);
        var headRef = remoteHead.Succeeded ? $"origin/{headBranch}" : headBranch;

        var bareDiff = await git.RunAsync(new GitCommand(barePath,
            ["diff", $"origin/{baseBranch}...{headRef}", .. DiffExcludePathspec]), ct);
        return !bareDiff.Succeeded
            ? Result<string>.Failure(bareDiff.Describe())
            : Result<string>.Success(Truncate(
                await WithExcludedFilesNoteAsync(barePath, $"origin/{baseBranch}...{headRef}", bareDiff.Stdout, ct),
                maxChars));

        static string Truncate(string diff, int max) =>
            diff.Length > max ? diff[..max] + "\n... (diff truncated)" : diff;
    }

    /// <summary>
    /// Files that are never worth an agent's diff budget: lockfiles and other generated
    /// artifacts. A fresh Next.js app's package-lock.json alone is ~8,600 lines — it used
    /// to land second in alphabetical order and eat the whole review budget, so the
    /// reviewer saw the lockfile and not one source file. They are excluded from the diff
    /// itself and listed by name at the top instead, so the agent still knows they changed.
    /// </summary>
    internal static readonly string[] DiffExcludedGlobs =
    [
        "package-lock.json", "pnpm-lock.yaml", "yarn.lock", "bun.lockb", "bun.lock",
        "Cargo.lock", "Gemfile.lock", "poetry.lock", "Pipfile.lock", "composer.lock",
        "packages.lock.json", "go.sum", "*.min.js", "*.min.css", "*.map",
    ];

    // `:(exclude,glob)` pathspecs — one anchored at the root, one for any depth.
    private static readonly string[] DiffExcludePathspec =
        ["--", ".", .. DiffExcludedGlobs.SelectMany(g => new[] { $":(exclude,glob){g}", $":(exclude,glob)**/{g}" })];

    /// <summary>Prepends a one-line note naming any excluded files that did change, so the
    /// exclusion never reads as "nothing else changed".</summary>
    private async Task<string> WithExcludedFilesNoteAsync(
        string repoPath, string range, string diff, CancellationToken ct)
    {
        var names = await git.RunAsync(new GitCommand(repoPath, "diff", "--name-only", range), ct);
        if (!names.Succeeded || string.IsNullOrWhiteSpace(names.Stdout))
            return diff;

        var excluded = SplitLines(names.Stdout).Where(IsDiffExcluded).ToList();

        return excluded.Count == 0
            ? diff
            : $"# Not shown (generated/lock files, {excluded.Count}): {string.Join(", ", excluded)}\n\n{diff}";
    }

    internal static bool IsDiffExcluded(string path)
    {
        var fileName = Path.GetFileName(path);
        foreach (var glob in DiffExcludedGlobs)
        {
            if (glob.StartsWith("*."))
            {
                if (fileName.EndsWith(glob[1..], StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (string.Equals(fileName, glob, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    public async Task<Result<string>> GetWorkingTreeDiffAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string baseBranch,
        int maxChars = 60_000, CancellationToken ct = default, string? baseSha = null)
    {
        var worktreePath = GetWorktreePath(orgId, repoId, taskId);
        if (!Directory.Exists(worktreePath))
            return Result<string>.Failure("Worktree does not exist");

        string against;
        if (Orkka.Modules.SourceControl.Contracts.GitArgs.IsSafeSha(baseSha))
        {
            // A Story member: its own change only, from where it started on the shared branch
            // (moved past a default-branch sync, as the finished review diff is).
            against = await ReviewDiffBaseAsync(worktreePath, baseSha!, "HEAD", ct);
        }
        else
        {
            // The branch point, so a base that moved on since the task started doesn't show up
            // as the task's own changes. Falls back to the remote tip if it can't be resolved.
            var mergeBase = await git.RunAsync(new GitCommand(worktreePath, "merge-base", $"origin/{baseBranch}", "HEAD"), ct);
            against = mergeBase.Succeeded && !string.IsNullOrWhiteSpace(mergeBase.Output)
                ? mergeBase.Output
                : $"origin/{baseBranch}";
        }

        // Stage the whole working tree — including untracked files, honouring .gitignore and
        // info/exclude — into a throwaway index, then diff that index against the branch
        // point. One diff, proper "new file" hunks for untracked files, and the worktree's
        // real index is never touched (the agent's own git commands may be mid-flight in it).
        var tempIndex = Path.Combine(Path.GetTempPath(), $"orkka-wt-{taskId}-{Guid.NewGuid():N}.index");
        var env = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = tempIndex };
        try
        {
            var seeded = false;
            var indexPath = await git.RunAsync(new GitCommand(worktreePath, "rev-parse", "--git-path", "index"), ct);
            if (indexPath.Succeeded && !string.IsNullOrWhiteSpace(indexPath.Output))
            {
                var trimmed = indexPath.Output;
                var realIndex = Path.IsPathRooted(trimmed) ? trimmed : Path.Combine(worktreePath, trimmed);
                try
                {
                    if (File.Exists(realIndex))
                    {
                        File.Copy(realIndex, tempIndex, overwrite: true);
                        seeded = true;
                    }
                }
                catch (IOException) { /* index mid-write — rebuild instead */ }
                catch (UnauthorizedAccessException) { /* same */ }
            }
            if (!seeded)
            {
                var read = await git.RunAsync(new GitCommand(worktreePath, ["read-tree", "HEAD"], Environment: env), ct);
                if (!read.Succeeded)
                    return Result<string>.Failure(read.Describe());
            }

            var add = await git.RunAsync(new GitCommand(worktreePath, ["add", "-A", "--ignore-errors", "--", "."], Environment: env), ct);
            if (!add.Succeeded)
                return Result<string>.Failure(add.Describe());

            var diff = await git.RunAsync(new GitCommand(worktreePath,
                ["diff", "--cached", "--no-color", "--no-ext-diff", "--no-textconv", against, "--"], Environment: env), ct);
            if (!diff.Succeeded)
                return Result<string>.Failure(diff.Describe());

            var text = diff.Stdout;
            return Result<string>.Success(
                text.Length > maxChars ? text[..maxChars] + "\n... (diff truncated)" : text);
        }
        finally
        {
            try { File.Delete(tempIndex); } catch { /* temp file — best effort */ }
        }
    }

    public async Task<bool> RemoteBranchExistsAsync(
        Ulid orgId, Ulid repoId, string branchName,
        CancellationToken ct = default)
    {
        var barePath = GetBareRepoPath(orgId, repoId);
        if (!Directory.Exists(barePath))
            return false;

        var result = await git.RunAsync(new GitCommand(barePath,
            ["ls-remote", "--heads", "origin", branchName], Timeout: FetchTimeout), ct);
        if (result.Succeeded)
            return !string.IsNullOrWhiteSpace(result.Stdout);

        // A failure to ASK is not "the branch does not exist": answer from the mirror's
        // remote-tracking refs instead, which the last refresh keeps current. Still false for
        // a branch the mirror has never fetched, which is the answer that makes the caller
        // create or fetch it.
        logger.LogDebug(
            "ls-remote for {Branch} in {BarePath} failed ({Error}) — answering from the mirror's refs",
            branchName, barePath, result.Error);
        var local = await git.RunAsync(new GitCommand(barePath,
            "rev-parse", "--verify", "--quiet", $"refs/remotes/origin/{branchName}") { Probe = true }, ct);
        return local.Succeeded && !string.IsNullOrWhiteSpace(local.Stdout);
    }
    // ── Stories ──────────────────────────────────────────────────────────────

    public async Task<Result<string>> GetDiffSinceAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string baseBranch, string baseSha,
        int maxChars = 60_000, string? headBranch = null, CancellationToken ct = default)
    {
        if (!Orkka.Modules.SourceControl.Contracts.GitArgs.IsSafeSha(baseSha))
            return await GetDiffAsync(orgId, repoId, taskId, baseBranch, maxChars, headBranch, ct);

        var worktreePath = GetWorktreePath(orgId, repoId, taskId);
        if (Directory.Exists(worktreePath))
        {
            var from = await ReviewDiffBaseAsync(worktreePath, baseSha, "HEAD", ct);
            var result = await git.RunAsync(new GitCommand(worktreePath,
                ["diff", from, "HEAD", .. DiffExcludePathspec]), ct);
            if (result.Succeeded)
                return Result<string>.Success(Truncate(
                    await WithExcludedFilesNoteAsync(worktreePath, $"{from}..HEAD", result.Stdout, ct), maxChars));
        }

        if (string.IsNullOrEmpty(headBranch))
            return Result<string>.Failure("Worktree does not exist");
        var barePath = GetBareRepoPath(orgId, repoId);
        if (!Directory.Exists(barePath))
            return Result<string>.Failure("Worktree and bare mirror do not exist");

        await FetchOriginAsync(barePath, barePath, ct);
        var remoteHead = await git.RunAsync(new GitCommand(barePath,
            "rev-parse", "--verify", "--quiet", $"refs/remotes/origin/{headBranch}") { Probe = true }, ct);
        var headRef = remoteHead.Succeeded ? $"origin/{headBranch}" : headBranch;
        var bareFrom = await ReviewDiffBaseAsync(barePath, baseSha, headRef, ct);
        var bareDiff = await git.RunAsync(new GitCommand(barePath,
            ["diff", bareFrom, headRef, .. DiffExcludePathspec]), ct);
        return !bareDiff.Succeeded
            // A base the mirror cannot resolve: the whole branch is still a truthful diff.
            ? await GetDiffAsync(orgId, repoId, taskId, baseBranch, maxChars, headBranch, ct)
            : Result<string>.Success(Truncate(
                await WithExcludedFilesNoteAsync(barePath, $"{bareFrom}..{headRef}", bareDiff.Stdout, ct), maxChars));

        static string Truncate(string diff, int max) =>
            diff.Length > max ? diff[..max] + "\n... (diff truncated)" : diff;
    }

    /// <summary>
    /// What a Story member's review diff starts from: its <c>ReviewBaseSha</c>, moved past any
    /// default-branch sync merged into the shared branch since.
    ///
    /// The base sync merges <c>origin/{default}</c> INTO the shared branch (first parent = the
    /// branch), so a plain <c>ReviewBaseSha..HEAD</c> also carries everything that landed on the
    /// default branch meanwhile. Moving the stored base to an existing commit is only right when
    /// the member had no commits of its own before the sync (then the merge commit itself is the
    /// base); once it has, no commit fits — the merge's first parent lacks the default branch's
    /// changes and its second parent lacks the earlier members' work. The right base is the
    /// TREE of <c>ReviewBaseSha</c> merged with the newest synced default-branch commit (which
    /// contains the older ones), built by <c>git merge-tree --write-tree</c> without touching any
    /// ref or working copy. Diffed against it, HEAD shows exactly the member's change — plus how
    /// it resolved a conflict, if the sync had one (a conflicted merge-tree keeps the markers).
    /// No sync merge since the base, or a git too old for <c>merge-tree --write-tree</c> (2.38+):
    /// the base itself, as before.
    /// </summary>
    internal async Task<string> ReviewDiffBaseAsync(string repoPath, string baseSha, string headRef, CancellationToken ct)
    {
        // Merges on the branch's own (first-parent) line since the base, newest first:
        // "<merge> <first parent> <second parent>…".
        var merges = await git.RunAsync(new GitCommand(repoPath,
            "rev-list", "--first-parent", "--merges", "--parents", $"{baseSha}..{headRef}") { Probe = true }, ct);
        if (!merges.Succeeded || string.IsNullOrWhiteSpace(merges.Stdout))
            return baseSha;

        var newest = merges.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (newest.Length < 3 || !Orkka.Modules.SourceControl.Contracts.GitArgs.IsSafeSha(newest[2]))
            return baseSha;
        var synced = newest[2];

        // Nothing new came in with it: nothing to move past.
        var contained = await git.RunAsync(new GitCommand(repoPath,
            "merge-base", "--is-ancestor", synced, baseSha) { Probe = true }, ct);
        if (contained.Succeeded)
            return baseSha;

        // Exit 0 = clean, 1 = conflicted; both print the merged tree's id on the first line.
        var tree = await git.RunAsync(new GitCommand(repoPath,
            "merge-tree", "--write-tree", "--no-messages", baseSha, synced) { Probe = true }, ct);
        var treeId = tree.Stdout?.Split('\n', 2)[0].Trim();
        return Orkka.Modules.SourceControl.Contracts.GitArgs.IsSafeSha(treeId) ? treeId! : baseSha;
    }

    public async Task<string?> ResolveReviewBaseAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string branchName, string baseBranch, CancellationToken ct = default)
    {
        var worktreePath = GetWorktreePath(orgId, repoId, taskId);
        if (!Directory.Exists(worktreePath))
            return null;

        // The previous member pushed the shared branch: this member starts at its head. The
        // clone was just made from it (CreateWorktreeAsync continues an existing remote branch,
        // and for a linked folder the folder IS origin), so the ref is there.
        var remote = await git.RunAsync(new GitCommand(worktreePath,
            "rev-parse", "--verify", "--quiet", $"refs/remotes/origin/{branchName}^{{commit}}") { Probe = true }, ct);
        if (remote.Succeeded && Sha(remote.Stdout) is { } head)
            return head;

        // First member: the branch is new, cut from the default branch.
        var mergeBase = await git.RunAsync(new GitCommand(worktreePath,
            "merge-base", "HEAD", $"origin/{baseBranch}") { Probe = true }, ct);
        return mergeBase.Succeeded ? Sha(mergeBase.Stdout) : null;

        static string? Sha(string? output)
        {
            var sha = output?.Trim();
            return Orkka.Modules.SourceControl.Contracts.GitArgs.IsSafeSha(sha) ? sha : null;
        }
    }
}
