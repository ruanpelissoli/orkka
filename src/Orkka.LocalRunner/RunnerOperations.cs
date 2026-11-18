namespace Orkka.LocalRunner;

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Execution.Agents;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.SharedKernel.Utilities;
using Orkka.SharedKernel.Ai;

/// <summary>
/// Executes host requests on the user's machine: repo validation, git workspace ops (via
/// the shared GitWorkspaceManager over the local repo cache), and claude CLI runs with
/// subscription auth. Worktree/workspace tokens from the wire are resolved to real paths
/// here and only here.
/// </summary>
public sealed class RunnerOperations(
    IGitWorkspaceManager git,
    IAiAgentInvoker invoker,
    ProviderDetection detection,
    IConfiguration configuration,
    RunnerHostLink hostLink,
    ILogger<RunnerOperations> logger,
    Orkka.Git.GitProcess? gitProcess = null,
    ForgeCli? forge = null,
    Trackers.TrackerAttachmentCache? ticketCache = null,
    MachineRunSlots? runSlots = null,
    RunnerHostRouter? router = null,
    // Decides whether a repository command the host asks for may run here (orkka.json on the
    // default branch, or the developer's approval). Null — a test that never runs one — means
    // every such command is refused: there is no path where the host's word alone suffices.
    Commands.CommandTrust? commandTrust = null,
    string workspaceName = "default")
{
    private readonly string _basePath = configuration["Worker:RepoBasePath"]!;
    private readonly ForgeCli forgeCli =
        forge ?? new ForgeCli(Microsoft.Extensions.Logging.Abstractions.NullLogger<ForgeCli>.Instance);

    /// <summary>One squash merge at a time per user folder. Two scratch clones cut from the
    /// same repository and pushed back a moment apart leave the second push NonFastForward:
    /// main moved under it when the first landed (prod, 2026-09-16, two StoreFront tasks
    /// landing in one folder). Serialized, the second clone is cut after the first push.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> MergeGates = new();

    private static string MergeGateKey(string repoPath)
    {
        var full = Path.GetFullPath(repoPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
    }

    /// <summary>The engine every git command in this class runs through. Static because the
    /// helpers that need it are static; the runner host hands in its own at construction.</summary>
    private static Orkka.Git.GitProcess Git { get; set; } =
        new(Microsoft.Extensions.Logging.Abstractions.NullLogger<Orkka.Git.GitProcess>.Instance);

    private readonly bool _gitProcessInstalled = InstallGitProcess(gitProcess);

    private static bool InstallGitProcess(Orkka.Git.GitProcess? process)
    {
        if (process is not null)
            Git = process;
        return true;
    }

    // ── Filesystem browsing (folder picker) ─────────────────────────────────

    public Task<DirectoryListing> ListDirectoryAsync(ListDirectoryRequest req)
    {
        try
        {
            // Roots: drives on Windows, home + / elsewhere.
            if (string.IsNullOrWhiteSpace(req.Path))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                DirectoryEntry[] roots = OperatingSystem.IsWindows()
                    ? [
                        new DirectoryEntry("Home", home, false),
                        .. DriveInfo.GetDrives()
                            .Where(d => d.IsReady)
                            .Select(d => new DirectoryEntry(d.Name, d.RootDirectory.FullName, false)),
                      ]
                    : [new DirectoryEntry("Home", home, false), new DirectoryEntry("/", "/", false)];

                return Task.FromResult(new DirectoryListing(null, null, roots, null));
            }

            var path = Path.GetFullPath(req.Path);
            if (!Directory.Exists(path))
                return Task.FromResult(new DirectoryListing(req.Path, null, [], "Directory does not exist."));

            var entries = new DirectoryInfo(path)
                .EnumerateDirectories()
                .Where(d => !d.Attributes.HasFlag(FileAttributes.Hidden)
                         && !d.Attributes.HasFlag(FileAttributes.System)
                         && !d.Name.StartsWith('.'))
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .Select(d => new DirectoryEntry(
                    d.Name, d.FullName, Directory.Exists(Path.Combine(d.FullName, ".git"))))
                .ToArray();

            return Task.FromResult(new DirectoryListing(
                path, Directory.GetParent(path)?.FullName, entries, null));
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(new DirectoryListing(req.Path, null, [], "Access denied."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new DirectoryListing(req.Path, null, [], ex.Message));
        }
    }

    // ── Repo validation ─────────────────────────────────────────────────────

    public async Task<RepoPathInfo> ValidateRepoPathAsync(ValidateRepoPathRequest req)
    {
        try
        {
            var path = Path.GetFullPath(req.Path);
            if (!Directory.Exists(path))
                return new RepoPathInfo(false, false, null, false, null, "Directory does not exist.");

            var inside = await RunGitAsync(path, "rev-parse --is-inside-work-tree");
            if (!inside.Success || inside.Value?.Trim() != "true")
                return new RepoPathInfo(true, false, null, false, null,
                    "Not a git repository. Run `git init` and create at least one commit first.");

            var head = await RunGitAsync(path, "symbolic-ref --short HEAD");
            var hasCommits = await RunGitAsync(path, "rev-parse HEAD");
            if (!hasCommits.Success)
                return new RepoPathInfo(true, false, null, false, null,
                    "Repository has no commits yet — create an initial commit first.");

            var status = await RunGitAsync(path, "status --porcelain");
            var isDirty = status.Success && !string.IsNullOrWhiteSpace(status.Value);

            return new RepoPathInfo(
                true, true,
                head.Success ? head.Value?.Trim() : "main",
                isDirty,
                Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                null);
        }
        catch (Exception ex)
        {
            return new RepoPathInfo(false, false, null, false, null, ex.Message);
        }
    }

    public async Task<InitLocalRepoResult> InitLocalRepoAsync(InitLocalRepoRequest req)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(req.Name) ||
                !System.Text.RegularExpressions.Regex.IsMatch(req.Name, "^[A-Za-z0-9][A-Za-z0-9._-]*$"))
            {
                return new InitLocalRepoResult(false, null, null,
                    "Repository names can use letters, digits, dots, dashes and underscores, starting with a letter or digit.");
            }

            var parent = Path.GetFullPath(req.ParentPath);
            if (!Directory.Exists(parent))
                return new InitLocalRepoResult(false, null, null, "Parent directory does not exist.");

            var target = Path.Combine(parent, req.Name);
            if (Directory.Exists(target) || File.Exists(target))
                return new InitLocalRepoResult(false, null, null, $"'{req.Name}' already exists here.");

            Directory.CreateDirectory(target);

            // `-b main` needs git >= 2.28; fall back to init + symbolic-ref for older gits.
            var init = await RunGitAsync(target, "init -b main");
            if (!init.Success)
            {
                init = await RunGitAsync(target, "init");
                if (init.Success)
                    await RunGitAsync(target, "symbolic-ref HEAD refs/heads/main");
            }
            if (!init.Success)
            {
                TryDeleteDirectory(target);
                return new InitLocalRepoResult(false, null, null, $"git init failed: {init.Error}");
            }

            // The bare mirror and agent worktrees need at least one commit on the default
            // branch. The engine supplies the platform identity, so a machine without git user
            // config commits all the same.
            await File.WriteAllTextAsync(Path.Combine(target, "README.md"), $"# {req.Name}\n");
            var add = await RunGitAsync(target, "add README.md");
            var commit = add.Success
                ? await RunGitAsync(target, "commit -m \"Initial commit\"")
                : add;
            if (!commit.Success)
            {
                TryDeleteDirectory(target);
                return new InitLocalRepoResult(false, null, null, $"Initial commit failed: {commit.Error}");
            }

            return new InitLocalRepoResult(true, target, "main", null);
        }
        catch (Exception ex)
        {
            return new InitLocalRepoResult(false, null, null, ex.Message);
        }
    }

    public async Task<InitLocalRepoResult> InitRepoInPlaceAsync(InitRepoInPlaceRequest req)
    {
        try
        {
            var path = Path.GetFullPath(req.Path);
            if (!Directory.Exists(path))
                return new InitLocalRepoResult(false, null, null, "Directory does not exist.");

            if (RejectUnsafeInitPath(path) is { } refusal)
                return new InitLocalRepoResult(false, null, null, refusal);

            // Cloud-synced folders are where a non-developer keeps everything, and the sync
            // client races git for .git/index. Plenty of people work this way without ever
            // noticing, so this is a log line rather than a refusal.
            if (LooksCloudSynced(path))
            {
                logger.LogWarning(
                    "[Runner] {Path} looks like a cloud-synced folder (OneDrive/iCloud/Dropbox) — "
                    + "the sync client and git can race each other over .git", path);
            }

            // --show-toplevel, not --is-inside-work-tree: the latter answers TRUE for a folder
            // NESTED inside someone else's repository (a home directory or Documents under git
            // is common enough), and adopting that parent would aim every worktree, commit and
            // merge at a repository the user never picked.
            var toplevel = await RunGitAsync(path, "rev-parse --show-toplevel");
            var enclosing = toplevel.Success && !string.IsNullOrWhiteSpace(toplevel.Value)
                ? Path.GetFullPath(toplevel.Value.Trim())
                : null;
            var isRepo = enclosing is not null && SamePath(enclosing, path);

            if (enclosing is not null && !isRepo)
            {
                // Initialising here would nest a repository inside one that already TRACKS
                // these files, and the parent would then see every one of them deleted and
                // replaced by a gitlink. Refuse rather than rewrite a repository we were
                // never offered. Untracked (or ignored) contents nest harmlessly, so those
                // fall through and get their own repository as asked.
                var tracked = await RunGitAsync(path, "ls-files -- .");
                if (tracked.Success && !string.IsNullOrWhiteSpace(tracked.Value))
                {
                    return new InitLocalRepoResult(false, null, null,
                        $"This folder is already part of the git repository at {enclosing}. "
                        + "Link that folder instead, or pick one that isn't inside it.");
                }
            }

            // Already a repository with history: the user's own git is the safety net, and
            // there is nothing here to import. Nothing below runs.
            if (isRepo)
            {
                var hasCommits = await RunGitAsync(path, "rev-parse HEAD");
                if (hasCommits.Success)
                {
                    var branch = await RunGitAsync(path, "symbolic-ref --short HEAD");
                    return new InitLocalRepoResult(
                        true, path, branch.Success ? branch.Value?.Trim() : "main", null);
                }
            }

            // Refused BEFORE anything is written, so an oversized folder is left untouched
            // rather than unwound.
            if (TooLargeToImport(path) is { } tooLarge)
                return new InitLocalRepoResult(false, null, null, tooLarge);

            // A copy of the folder exactly as it arrived, taken before the first byte changes.
            // Everything else in this method is careful reasoning about failure modes we
            // thought of; this covers the ones we did not — a bad ignore rule, a sync client
            // eating .git, a user who picked the wrong folder — and "your original is here"
            // needs no git to act on. Best effort: failing to copy is not a reason to refuse
            // an import that cannot delete anything anyway.
            var snapshot = TrySnapshot(path);

            var createdRepo = false;
            if (!isRepo)
            {
                // `-b main` needs git >= 2.28; fall back to init + symbolic-ref for older gits.
                var init = await RunGitAsync(path, "init -b main");
                if (!init.Success)
                {
                    init = await RunGitAsync(path, "init");
                    if (init.Success)
                        await RunGitAsync(path, "symbolic-ref HEAD refs/heads/main");
                }
                if (!init.Success)
                    return new InitLocalRepoResult(false, null, null, $"git init failed: {init.Error}");
                createdRepo = true;
            }

            // The bare mirror and agent worktrees need at least one commit. Commit the
            // folder's existing contents so agents actually see the user's code — but settle
            // what is ignored first, so the initial commit doesn't swallow dependency
            // folders or .env secrets.
            var (excluded, wroteGitignore) = await EnsureIgnoredAsync(path);

            // Everything below unwinds the .git THIS call created, so a folder that could not
            // be imported is left exactly as the user handed it over. Only the directory (and
            // the .gitignore) we just made are removed — never a working file.
            InitLocalRepoResult Undo(string error)
            {
                if (createdRepo)
                {
                    TryDeleteDirectory(Path.Combine(path, ".git"));
                    if (wroteGitignore)
                        TryDeleteFile(Path.Combine(path, ".gitignore"));
                }
                return new InitLocalRepoResult(false, null, null, error);
            }

            // A first `add -A` over a real project — thousands of files, a cold file cache and
            // a virus scanner reading every one of them — routinely outlives two minutes.
            var add = await RunGitAsync(path, "add -A", TimeSpan.FromMinutes(10));
            if (!add.Success)
                return Undo($"git add failed: {add.Error}");

            // Authored by the platform identity, like every commit Orkka makes: the baseline
            // of an imported folder is Orkka's doing, and a machine with no git identity at all
            // imports exactly as well as one with several.
            var commit = await RunGitAsync(path,
                "commit --allow-empty -m \"Initial commit\"", TimeSpan.FromMinutes(10));
            if (!commit.Success)
                return Undo($"Initial commit failed: {commit.Error}");

            // A named point to come back to. Every agent commit lands on top of this one, so
            // "put it back the way it was" stays a single git command for the whole lifetime
            // of the project.
            await RunGitAsync(path, "tag -f orkka-baseline");

            var head = await RunGitAsync(path, "symbolic-ref --short HEAD");
            logger.LogInformation(
                "[Runner] Initialized git repository in place at {Path} (snapshot: {Snapshot})",
                path, snapshot ?? "none");
            return new InitLocalRepoResult(
                true, path, head.Success ? head.Value?.Trim() : "main", null,
                ExcludedPaths: excluded, SnapshotPath: snapshot);
        }
        catch (Exception ex)
        {
            return new InitLocalRepoResult(false, null, null, ex.Message);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best effort — a half-created folder is annoying but harmless.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best effort, same reasoning as TryDeleteDirectory.
        }
    }

    // ── Guards for turning a picked folder into a repository ────────────────

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool SamePath(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            PathComparison);

    /// <summary>Shell folders that hold a whole life rather than one project. A folder picker
    /// reaches them in two clicks, and `git add -A` over one is minutes of work, gigabytes of
    /// objects, and every secret on the machine committed by accident.</summary>
    private static readonly string[] ProtectedHomeFolders =
        ["Desktop", "Documents", "Downloads", "Pictures", "Music", "Videos", "Library"];

    /// <summary>
    /// Null when the folder is a reasonable thing to make a repository out of, else the
    /// sentence to show the user. `git init` is not destructive anywhere, but these are the
    /// paths where the import that follows it would be.
    /// </summary>
    internal static string? RejectUnsafeInitPath(string path)
    {
        // Normalised ONCE, then compared as plain strings. Path.GetFullPath must not be
        // re-applied to a trimmed root: on Windows "C:" means "the current directory of
        // drive C:", not the drive itself, and the root check would silently never fire.
        var expanded = Path.GetFullPath(path);
        var full = Path.TrimEndingDirectorySeparator(expanded);

        if (Path.GetPathRoot(expanded) is { Length: > 0 } root
            && string.Equals(Path.TrimEndingDirectorySeparator(root), full, PathComparison))
        {
            return "That is the root of a drive, not a project. Pick the folder that holds your app.";
        }

        var home = Path.TrimEndingDirectorySeparator(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (string.IsNullOrEmpty(home))
            return null;

        if (string.Equals(home, full, PathComparison))
            return "That is your home folder, not a project. Pick the folder that holds your app.";

        if (Path.GetDirectoryName(home) is { Length: > 0 } usersRoot
            && string.Equals(usersRoot, full, PathComparison))
        {
            return "That folder holds every account on this machine. Pick the folder that holds your app.";
        }

        var name = Path.GetFileName(full);
        var parent = Path.GetDirectoryName(full);
        if (parent is null || !string.Equals(parent, home, PathComparison))
            return null;

        // Directly under home: the shell folders, plus any cloud-sync root (OneDrive,
        // "OneDrive - Contoso", iCloud Drive) whose whole point is to hold everything.
        if (ProtectedHomeFolders.Any(f => string.Equals(f, name, PathComparison))
            || name.StartsWith("OneDrive", PathComparison)
            || name.StartsWith("iCloud", PathComparison)
            || string.Equals(name, "Dropbox", PathComparison))
        {
            return $"'{name}' holds everything on this machine, not one app. "
                 + "Open it and pick the folder that holds your app.";
        }

        return null;
    }

    /// <summary>Whether the path sits inside a folder a sync client owns. Advisory only —
    /// see the call site.</summary>
    private static bool LooksCloudSynced(string path)
    {
        var full = Path.GetFullPath(path);
        string[] markers = ["OneDrive", "iCloud", "Dropbox", "Google Drive", "Creative Cloud Files"];
        return full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => markers.Any(m => segment.StartsWith(m, PathComparison)));
    }

    /// <summary>Directories skipped by the size pre-scan: everything git will be told to
    /// ignore, plus .git itself. Keeping the two lists in step is what makes the scan a
    /// forecast of the commit rather than a measure of the disk.</summary>
    private static readonly HashSet<string> UnscannedDirectories =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".git", "node_modules", ".venv", "venv", "__pycache__", ".next",
            "dist", "build", "obj", "bin", "target", ".gradle", "vendor",
        };

    private const int MaxImportFiles = 20_000;
    private const long MaxImportBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// Null when the folder is small enough to commit, else the sentence to show. Walks with
    /// an early exit, so the cost is bounded by the threshold rather than by the tree: the
    /// folder this catches is one nobody wants us to spend ten minutes staging.
    /// </summary>
    private static string? TooLargeToImport(string path)
    {
        var files = 0;
        long bytes = 0;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(path));

        while (pending.Count > 0)
        {
            IEnumerable<FileSystemInfo> children;
            try
            {
                children = pending.Pop().EnumerateFileSystemInfos();
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                // Unreadable subtree — git will skip it too. Not a reason to refuse.
                continue;
            }

            foreach (var child in children)
            {
                if (child is DirectoryInfo subdirectory)
                {
                    // Reparse points: a junction or symlink back up the tree turns this walk
                    // into an infinite one, and git does not follow them either.
                    if (!UnscannedDirectories.Contains(subdirectory.Name)
                        && !subdirectory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        pending.Push(subdirectory);
                    }
                    continue;
                }

                files++;
                try { bytes += ((FileInfo)child).Length; } catch { /* vanished mid-walk */ }

                if (files > MaxImportFiles || bytes > MaxImportBytes)
                {
                    return "This folder is too big to bring in as one project — it holds more than "
                         + $"{MaxImportFiles:N0} files or {MaxImportBytes / (1024 * 1024 * 1024)} GB "
                         + "outside the usual dependency folders. Pick the folder that holds just your app.";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// What an import keeps out of the repository, and nothing more.
    ///
    /// Every entry here is regenerable from the code itself or is a secret. Notably absent:
    /// dist/ and build/, which are output only when something builds into them — see
    /// <see cref="BuildOutputFolders"/>. The asymmetry is deliberate: committing something
    /// regenerable is untidy, while ignoring something hand-written hides part of the app
    /// from the agents AND leaves it with no point in history to restore from. When the
    /// import is unsure, it commits.
    /// </summary>
    private static readonly string[] AlwaysIgnored =
        ["node_modules/", ".venv/", "venv/", "__pycache__/", ".next/", "target/", ".gradle/",
         "bin/", "obj/", ".env", ".env.local", ".env.*.local", ".DS_Store", "Thumbs.db"];

    /// <summary>Ignored only on the evidence of <see cref="BuildSystemMarkers"/>: without a
    /// build system to regenerate them, a folder called "build" is just a folder, and it is
    /// as likely to hold the app as to hold its output.</summary>
    private static readonly string[] BuildOutputFolders = ["dist", "build", "out"];

    private static readonly string[] BuildSystemMarkers =
        ["package.json", "pyproject.toml", "Cargo.toml", "go.mod", "pom.xml", "build.gradle"];

    /// <summary>Heavy or secret-bearing paths worth a second look when a .gitignore already
    /// exists. Checked against git itself, so an entry covered by any pattern anywhere in the
    /// file (or by a nested .gitignore) counts as handled.</summary>
    private static readonly string[] MustNotCommit =
        ["node_modules", ".venv", "venv", "__pycache__", ".next", "target", ".env", ".env.local"];

    private static bool HasBuildSystem(string path) =>
        BuildSystemMarkers.Any(marker => File.Exists(Path.Combine(path, marker)));

    /// <summary>
    /// Makes sure the initial commit skips what no repository should carry, and returns
    /// whether the .gitignore was created from scratch (the only case the rollback may undo).
    ///
    /// An existing .gitignore is never rewritten or reordered — a block is APPENDED, and only
    /// for paths that exist on disk and that git confirms are still unignored. The file a
    /// no-repository folder arrives with is usually the one an AI chat wrote for it, which is
    /// exactly as likely to omit node_modules as to include it.
    /// </summary>
    private async Task<(string[] Excluded, bool WroteFile)> EnsureIgnoredAsync(string path)
    {
        var buildOutput = HasBuildSystem(path)
            ? BuildOutputFolders.Where(f => Directory.Exists(Path.Combine(path, f))).ToArray()
            : [];

        var gitignore = Path.Combine(path, ".gitignore");
        if (!File.Exists(gitignore))
        {
            var entries = AlwaysIgnored.Concat(buildOutput.Select(f => $"{f}/")).ToArray();
            await File.WriteAllTextAsync(gitignore, string.Join('\n', entries) + "\n");
            return (ExistingOf(path, entries), true);
        }

        var missing = new List<string>();
        foreach (var entry in MustNotCommit.Concat(buildOutput))
        {
            var candidate = Path.Combine(path, entry);
            if (!Directory.Exists(candidate) && !File.Exists(candidate))
                continue;

            // check-ignore exits 1 for "not ignored", which is exactly the case worth fixing.
            var ignored = await RunGitAsync(path, $"check-ignore -q -- \"{entry}\"");
            if (!ignored.Success)
                missing.Add(Directory.Exists(candidate) ? $"{entry}/" : entry);
        }

        if (missing.Count == 0)
            return ([], false);

        var existing = await File.ReadAllTextAsync(gitignore);
        var separator = existing.EndsWith('\n') ? "" : "\n";
        await File.AppendAllTextAsync(gitignore,
            $"{separator}\n# Added by Orkka — kept out of the repository, left on disk.\n"
            + string.Join('\n', missing) + "\n");

        logger.LogInformation(
            "[Runner] Appended {Count} entries to the .gitignore in {Path}", missing.Count, path);
        return ([.. missing], false);
    }

    /// <summary>The subset of ignore entries that match something actually present, so the
    /// user is told what was really left out rather than read a template back.</summary>
    private static string[] ExistingOf(string path, IEnumerable<string> entries) =>
        [.. entries
            .Where(e => !e.Contains('*'))
            .Where(e =>
            {
                var candidate = Path.Combine(path, e.TrimEnd('/'));
                return Directory.Exists(candidate) || File.Exists(candidate);
            })];

    /// <summary>
    /// Copies the folder as it arrived to ~/.orkka/imports/{name}-{timestamp}, skipping the
    /// directories nobody needs a backup of, and prunes copies older than a month. Returns
    /// the copy's path, or null when it could not be made — the caller carries on either way,
    /// because the import itself never removes a file.
    /// </summary>
    private string? TrySnapshot(string path)
    {
        try
        {
            var root = configuration["Runner:ImportsPath"] is { Length: > 0 } configured
                ? configured
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".orkka", "imports");
            Directory.CreateDirectory(root);
            PruneOldSnapshots(root);

            var target = Path.Combine(root,
                $"{Path.GetFileName(Path.TrimEndingDirectorySeparator(path))}-{DateTime.Now:yyyyMMdd-HHmmss}");
            CopyTree(path, target);
            return target;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Runner] Could not copy {Path} before importing it", path);
            return null;
        }
    }

    private static void PruneOldSnapshots(string root)
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromDays(30);
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            try
            {
                if (Directory.GetCreationTimeUtc(directory) < cutoff)
                    Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // A copy we cannot prune is disk we cannot reclaim — never a failed import.
            }
        }
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var info = new DirectoryInfo(directory);

            // Regenerable content and symlink loops: the same exclusions the size scan makes,
            // for the same reason. .git is skipped too — a repository with history never gets
            // here, and a half-made one is not worth preserving.
            if (UnscannedDirectories.Contains(info.Name)
                || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            CopyTree(directory, Path.Combine(destination, info.Name));
        }
    }

    // ── Git workspace ops (delegate to the shared GitWorkspaceManager) ─────

    /// <summary>Whether this machine can reach the given remotes with the developer's own git
    /// credentials — the preflight for exactly the clone a task or a chat turn would
    /// do. See <see cref="RepoAccessProbing"/>, which owns the probe.</summary>
    public Task<CheckRepoAccessResponse> CheckRepoAccessAsync(CheckRepoAccessRequest req)
        => RepoAccessProbing.ProbeAsync(req.Repos);

    /// <summary>
    /// Clones or refreshes the mirror with the developer's own git credentials. The engine writes this machine's identity — the one
    /// configured in runner.env, else none, leaving the developer's git config in charge.
    /// </summary>
    public async Task<RunnerOpResult> EnsureBareCloneAsync(EnsureBareCloneRequest req)
    {
        var result = await git.EnsureBareCloneAsync(
            Ulid.Parse(req.OrgId), Ulid.Parse(req.RepoId), req.CloneUrlOrPath, req.LocalCheckoutPath);
        return result.IsSuccess ? RunnerOpResult.Ok(result.Value) : RunnerOpResult.Fail(result.Error!);
    }

    /// <summary>The repositories in a project's base folder (read-only — see
    /// <see cref="RepoDiscovery"/>).</summary>
    public Task<DiscoverReposResult> DiscoverReposAsync(DiscoverReposRequest req)
        => RepoDiscovery.DiscoverAsync(Git, req.Path);

    public async Task<RunnerOpResult> CreateWorktreeAsync(CreateWorktreeRequest req)
    {
        var result = await git.CreateWorktreeAsync(
            Ulid.Parse(req.OrgId), Ulid.Parse(req.RepoId), Ulid.Parse(req.TaskId),
            req.BranchName, req.BaseBranch, req.WorktreeName);
        return result.IsSuccess ? RunnerOpResult.Ok(result.Value) : RunnerOpResult.Fail(result.Error!);
    }

    public async Task<RunnerOpResult> GetMergeConflictsAsync(GetMergeConflictsRequest req)
    {
        var result = await git.GetMergeConflictsAsync(
            Ulid.Parse(req.OrgId), Ulid.Parse(req.RepoId), req.HeadBranch, req.BaseBranch);
        return result.IsSuccess
            ? RunnerOpResult.Ok(System.Text.Json.JsonSerializer.Serialize(result.Value))
            : RunnerOpResult.Fail(result.Error!);
    }

    public async Task<RunnerOpResult> ApplyMergeStrategyAsync(ApplyMergeStrategyRequest req)
    {
        Dictionary<string, string>? choices = null;
        if (!string.IsNullOrEmpty(req.ChoicesJson))
        {
            try
            {
                choices = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(req.ChoicesJson);
            }
            catch (System.Text.Json.JsonException)
            {
                return RunnerOpResult.Fail("Invalid per-file choices payload.");
            }
        }

        var result = await git.ApplyMergeStrategyAsync(
            Ulid.Parse(req.OrgId), Ulid.Parse(req.RepoId), Ulid.Parse(req.TaskId),
            req.BaseBranch, req.Strategy, choices);
        return result.IsSuccess
            ? RunnerOpResult.Ok(System.Text.Json.JsonSerializer.Serialize(result.Value))
            : RunnerOpResult.Fail(result.Error!);
    }

    public async Task<RunnerOpResult> SyncBaseBranchAsync(SyncBaseBranchRequest req)
    {
        var orgId = Ulid.Parse(req.OrgId);
        var repoId = Ulid.Parse(req.RepoId);
        var taskId = Ulid.Parse(req.TaskId);

        var result = await git.SyncBaseBranchAsync(
            orgId, repoId, taskId, req.BaseBranch, req.LeaveConflictsInProgress,
            req.CommitLeftovers);

        return result.IsSuccess
            ? RunnerOpResult.Ok(System.Text.Json.JsonSerializer.Serialize(result.Value))
            : RunnerOpResult.Fail(result.Error!);
    }

    public async Task<RunnerOpResult> VerifyMergeAsync(VerifyMergeRequest req)
    {
        var result = await git.VerifyMergeAsync(
            Ulid.Parse(req.OrgId), Ulid.Parse(req.RepoId), Ulid.Parse(req.TaskId), req.BaseBranch);
        return result.IsSuccess
            ? RunnerOpResult.Ok(System.Text.Json.JsonSerializer.Serialize(result.Value))
            : RunnerOpResult.Fail(result.Error!);
    }

    public async Task<RunnerOpResult> RunWorktreeCommandAsync(RunWorktreeCommandRequest req)
    {
        var (orgId, repoId, taskId) = (Ulid.Parse(req.OrgId), Ulid.Parse(req.RepoId), Ulid.Parse(req.TaskId));
        if (await VetCommandAsync(git.GetWorktreePath(orgId, repoId, taskId), req.Command, req.Kind) is { } approval)
        {
            return RunnerOpResult.Ok(System.Text.Json.JsonSerializer.Serialize(
                new RunWorktreeCommandResult(-1, null, 0, approval.Message, approval)));
        }

        var result = await git.RunWorktreeCommandAsync(
            orgId, repoId, taskId, req.Command,
            req.Kind == CommandKind.Build ? RepoCommandKind.Build : RepoCommandKind.Check, req.TimeoutSeconds);

        var payload = result.IsSuccess
            ? new RunWorktreeCommandResult(
                result.Value!.ExitCode, result.Value.Output, result.Value.DurationSeconds, null)
            : new RunWorktreeCommandResult(-1, null, 0, result.Error);

        return RunnerOpResult.Ok(System.Text.Json.JsonSerializer.Serialize(payload));
    }

    /// <summary>
    /// Splits an app-run request into what may start and what may not: a target starts only
    /// when its start, install and stop commands are each vouched for (<see cref="Commands.CommandTrust"/>),
    /// checked against the developer's own working copy of the repository. A target whose
    /// dependency was refused is refused too.
    /// </summary>
    internal async Task<(StartAppRunRequest Allowed, List<AppRunTargetStart> Refused)> VetAppRunAsync(
        StartAppRunRequest request)
    {
        var refused = new List<AppRunTargetStart>();
        var refusedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in request.Targets)
        {
            if (!RunnerTokens.TryParseUserRepo(target.RepoToken, out var orgId, out var repoId))
                continue; // the manager reports a malformed token itself
            var (repoDir, _) = await ResolveUserRepoPathAsync(orgId, repoId);
            if (repoDir is null)
                continue; // likewise: "repository not found" is the manager's answer

            foreach (var command in new[] { target.Command, target.InstallCommand, target.StopCommand })
            {
                if (string.IsNullOrWhiteSpace(command))
                    continue;
                if (await VetCommandAsync(repoDir, command, CommandKind.RunTarget) is { } approval)
                {
                    refused.Add(new AppRunTargetStart(
                        target.RunId, target.Key, false, null, null, null, null, approval.Message, approval));
                    refusedKeys.Add(target.Key);
                    break;
                }
            }
        }

        // Dependents of a refused target cannot come up either; say why instead of letting
        // them start against a dependency that will never answer.
        bool changed;
        do
        {
            changed = false;
            foreach (var target in request.Targets)
            {
                if (refusedKeys.Contains(target.Key) || !target.DependsOn.Any(refusedKeys.Contains))
                    continue;
                refused.Add(new AppRunTargetStart(
                    target.RunId, target.Key, false, null, null, null, null,
                    $"Not started: it depends on {string.Join(", ", target.DependsOn.Where(refusedKeys.Contains))}, "
                    + "which is waiting for your approval."));
                refusedKeys.Add(target.Key);
                changed = true;
            }
        }
        while (changed);

        return (request with { Targets = [.. request.Targets.Where(t => !refusedKeys.Contains(t.Key))] }, refused);
    }

    /// <summary>
    /// Null when <paramref name="command"/> may run in <paramref name="repoDir"/>; otherwise the
    /// approval the developer has to give on this machine. See <see cref="Commands.CommandTrust"/>.
    /// </summary>
    internal async Task<CommandApproval?> VetCommandAsync(string repoDir, string command, CommandKind kind)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;
        if (commandTrust is null)
        {
            return new CommandApproval("-", repoDir, Commands.CommandTrust.Normalize(command), kind,
                Environment.MachineName);
        }

        var verdict = await commandTrust.CheckAsync(repoDir, command, kind, workspaceName);
        return verdict.Allowed ? null : verdict.Pending;
    }

    public async Task<RunnerOpResult> CommitAndPushAsync(CommitAndPushRequest req)
    {
        if (!TryResolveWorktree(req.WorktreeToken, out var worktreePath, out var error))
            return RunnerOpResult.Fail(error);

        // The developer's own git credentials push.
        var result = await git.CommitAndPushAsync(worktreePath, req.Message, req.BranchName);
        return result.IsSuccess ? RunnerOpResult.Ok() : RunnerOpResult.Fail(result.Error!);
    }

    public async Task<RunnerOpResult> EnsurePushedAsync(EnsurePushedRequest req)
    {
        var result = await git.EnsurePushedAsync(
            Ulid.Parse(req.OrgId), Ulid.Parse(req.RepoId), Ulid.Parse(req.TaskId),
            req.BranchName);
        return result.IsSuccess ? RunnerOpResult.Ok() : RunnerOpResult.Fail(result.Error!);
    }

    public async Task<RunnerOpResult> RemoteBranchExistsAsync(RemoteBranchExistsRequest req)
    {
        var exists = await git.RemoteBranchExistsAsync(
            Ulid.Parse(req.OrgId), Ulid.Parse(req.RepoId), req.BranchName);
        return RunnerOpResult.Ok(exists ? "true" : "false");
    }

    public async Task<RunnerOpResult> GetDiffAsync(GetDiffRequest req)
    {
        var result = string.IsNullOrWhiteSpace(req.BaseSha)
            ? await git.GetDiffAsync(
                Ulid.Parse(req.OrgId), Ulid.Parse(req.RepoId), Ulid.Parse(req.TaskId),
                req.BaseBranch, req.MaxChars, req.HeadBranch)
            : await git.GetDiffSinceAsync(
                Ulid.Parse(req.OrgId), Ulid.Parse(req.RepoId), Ulid.Parse(req.TaskId),
                req.BaseBranch, req.BaseSha, req.MaxChars, req.HeadBranch);
        return result.IsSuccess ? RunnerOpResult.Ok(result.Value) : RunnerOpResult.Fail(result.Error!);
    }

    public async Task<RunnerOpResult> ResolveReviewBaseAsync(ResolveReviewBaseRequest req)
    {
        if (!GitArgs.IsSafeRef(req.BranchName) || !GitArgs.IsSafeRef(req.BaseBranch))
            return RunnerOpResult.Fail("Invalid branch name.");
        var sha = await git.ResolveReviewBaseAsync(
            Ulid.Parse(req.OrgId), Ulid.Parse(req.RepoId), Ulid.Parse(req.TaskId), req.BranchName, req.BaseBranch);
        return sha is null ? RunnerOpResult.Fail("Could not read the branch's starting point.") : RunnerOpResult.Ok(sha);
    }

    public async Task<RunnerOpResult> GetWorkingTreeDiffAsync(GetWorkingTreeDiffRequest req)
    {
        var result = await git.GetWorkingTreeDiffAsync(
            Ulid.Parse(req.OrgId), Ulid.Parse(req.RepoId), Ulid.Parse(req.TaskId),
            req.BaseBranch, req.MaxChars, baseSha: req.BaseSha);
        return result.IsSuccess ? RunnerOpResult.Ok(result.Value) : RunnerOpResult.Fail(result.Error!);
    }

    public async Task<RunnerOpResult> WriteWorktreeFilesAsync(WriteWorktreeFilesRequest req)
    {
        var result = await git.WriteWorktreeContextAsync(
            Ulid.Parse(req.OrgId), Ulid.Parse(req.RepoId), Ulid.Parse(req.TaskId),
            req.Files.Select(f => new WorktreeContextFile(f.RelativePath, f.Content)).ToList());
        return result.IsSuccess ? RunnerOpResult.Ok() : RunnerOpResult.Fail(result.Error!);
    }

    /// <summary>Returns the outcome as a JSON payload rather than a bare Ok/Fail: a setup
    /// command that exits non-zero is not a transport failure — the host needs the exit code
    /// and output to decide whether the QA lane can proceed.</summary>
    public async Task<RunnerOpResult> RunSetupCommandAsync(RunSetupCommandRequest req)
    {
        var (orgId, repoId, taskId) = (Ulid.Parse(req.OrgId), Ulid.Parse(req.RepoId), Ulid.Parse(req.TaskId));
        if (await VetCommandAsync(git.GetWorktreePath(orgId, repoId, taskId), req.Command, CommandKind.Setup) is { } approval)
        {
            return RunnerOpResult.Ok(System.Text.Json.JsonSerializer.Serialize(
                new RunSetupCommandResult(false, -1, null, 0, approval.Message, approval)));
        }

        var result = await git.RunSetupCommandAsync(orgId, repoId, taskId, req.Command);

        var payload = result.IsSuccess
            ? new RunSetupCommandResult(
                result.Value!.Skipped, result.Value.ExitCode, result.Value.Output,
                result.Value.DurationSeconds, null)
            : new RunSetupCommandResult(false, -1, null, 0, result.Error);

        return RunnerOpResult.Ok(System.Text.Json.JsonSerializer.Serialize(payload));
    }

    public async Task<RunnerOpResult> CleanupWorktreeAsync(CleanupWorktreeRequest req)
    {
        await git.CleanupWorktreeAsync(Ulid.Parse(req.OrgId), Ulid.Parse(req.RepoId), Ulid.Parse(req.TaskId));
        await RemoveTaskChatWorkspaceAsync(req);
        return RunnerOpResult.Ok();
    }

    /// <summary>The task's Discuss workspace goes with its worktree: its copy of this repo is a
    /// worktree of the mirror, so it is removed through the mirror (which also forgets it), and
    /// the folder itself once nothing is left in it. Best effort — a leftover folder costs disk,
    /// never correctness, and is rebuilt on the next Discuss turn.</summary>
    internal async Task RemoveTaskChatWorkspaceAsync(CleanupWorktreeRequest req)
    {
        try
        {
            var workspace = Path.Combine(_basePath, req.OrgId, "task-chat-workspace", req.TaskId);
            if (!Directory.Exists(workspace))
                return;

            var bare = git.GetBareRepoPath(Ulid.Parse(req.OrgId), Ulid.Parse(req.RepoId));
            if (Directory.Exists(bare))
            {
                var bareKey = Path.GetFullPath(bare).Replace('\\', '/').TrimEnd('/');
                foreach (var folder in Directory.EnumerateDirectories(workspace))
                {
                    // Only this repo's copy: its .git file points into this mirror's worktrees.
                    var dotGit = Path.Combine(folder, ".git");
                    if (!File.Exists(dotGit))
                        continue;
                    var pointer = (await File.ReadAllTextAsync(dotGit)).Replace('\\', '/');
                    if (!pointer.Contains(bareKey, StringComparison.OrdinalIgnoreCase))
                        continue;
                    await RunGitAsync(bare, $"worktree remove --force \"{folder}\"");
                }
                await RunGitAsync(bare, "worktree prune");
            }

            if (!Directory.EnumerateFileSystemEntries(workspace).Any())
                Directory.Delete(workspace);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not remove the Discuss workspace of task {TaskId}", req.TaskId);
        }
    }

    public async Task<RunnerOpResult> RemoveRepoWorkspaceAsync(RemoveRepoWorkspaceRequest req)
    {
        await git.RemoveRepoWorkspaceAsync(Ulid.Parse(req.OrgId), Ulid.Parse(req.RepoId));
        return RunnerOpResult.Ok();
    }

    // ── Repo browsing (bare cache) ──────────────────────────────────────────

    // Browse reads use origin/{branch}: fetches only update refs/remotes/origin/*, so the
    // plain refs/heads/* copies are frozen at clone time and would serve stale content.

    public async Task<RunnerFileTreeResult> GetFileTreeAsync(GetFileTreeRequest req)
    {
        var bare = git.GetBareRepoPath(Ulid.Parse(req.OrgId), Ulid.Parse(req.RepoId));
        if (!Directory.Exists(bare))
            return new RunnerFileTreeResult(false, null, "Repository cache not prepared yet.");

        var result = await RunGitAsync(bare, $"ls-tree -r --name-only \"origin/{req.Branch}\"");
        return result.Success
            ? new RunnerFileTreeResult(true,
                result.Value!.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), null)
            : new RunnerFileTreeResult(false, null, result.Error);
    }

    public async Task<RunnerOpResult> GetFileContentAsync(GetFileContentRequest req)
    {
        var bare = git.GetBareRepoPath(Ulid.Parse(req.OrgId), Ulid.Parse(req.RepoId));
        if (!Directory.Exists(bare))
            return RunnerOpResult.Fail("Repository cache not prepared yet.");

        // Escape the ref:path spec minimally — repo paths never contain quotes.
        return await RunGitAsync(bare, $"show \"origin/{req.Branch}:{req.FilePath}\"");
    }

    public async Task<RunnerCommitLogResult> GetCommitLogAsync(GetCommitLogRequest req)
    {
        var bare = git.GetBareRepoPath(Ulid.Parse(req.OrgId), Ulid.Parse(req.RepoId));
        if (!Directory.Exists(bare))
            return new RunnerCommitLogResult(false, null, "Repository cache not prepared yet.");
        if (!GitArgs.IsSafeRef(req.Branch) || (req.FilePath is not null && !GitArgs.IsSafePath(req.FilePath)))
            return new RunnerCommitLogResult(false, null, "Invalid branch or path.");

        var result = await RunGitAsync(bare, GitArgs.CommitLog($"origin/{req.Branch}", req.Skip, req.Take, req.FilePath));
        if (!result.Success)
            return new RunnerCommitLogResult(false, null, result.Error);

        var commits = GitArgs.ParseCommitLog(result.Value ?? "")
            .Select(c => new RunnerCommitInfo(c.Sha, c.Author, c.Date, c.Subject))
            .ToArray();
        return new RunnerCommitLogResult(true, commits, null);
    }

    public async Task<RunnerBranchListResult> ListBranchesAsync(ListBranchesRequest req)
    {
        var bare = git.GetBareRepoPath(Ulid.Parse(req.OrgId), Ulid.Parse(req.RepoId));
        if (!Directory.Exists(bare))
            return new RunnerBranchListResult(false, null, "Repository cache not prepared yet.");

        // Refresh refs first so manual pushes to the user's repo show up immediately.
        await RunGitAsync(bare, "fetch origin");

        var result = await RunGitAsync(bare, GitArgs.BranchList("refs/remotes/origin"));
        if (!result.Success)
            return new RunnerBranchListResult(false, null, result.Error);

        var branches = GitArgs.ParseBranchList(result.Value ?? "", stripPrefix: "origin/")
            .Select(b => new RunnerBranchInfo(b.Name, b.Sha, b.LastCommitSubject, b.LastCommitDate))
            .ToList();

        // The mirror's origin IS the user's working repo, so the refs above only cover
        // branches checked out locally. Branches living solely on the user repo's own
        // remote (e.g. a GitHub origin/feature-x never checked out) exist only as the
        // user repo's remote-tracking refs — surface those too, flagged IsRemote.
        if (await GetUserRepoPathAsync(bare) is { } userRepo)
        {
            var remoteRefs = await RunGitAsync(userRepo, GitArgs.BranchList("refs/remotes/origin"));
            if (remoteRefs.Success)
            {
                var known = branches.Select(b => b.Name).ToHashSet(StringComparer.Ordinal);
                branches.AddRange(GitArgs.ParseBranchList(remoteRefs.Value ?? "", stripPrefix: "origin/")
                    .Where(b => !known.Contains(b.Name))
                    .Select(b => new RunnerBranchInfo(
                        b.Name, b.Sha, b.LastCommitSubject, b.LastCommitDate, IsRemote: true)));
            }
        }

        return new RunnerBranchListResult(true, [.. branches], null);
    }

    public async Task<RunnerOpResult> EnsureLocalBranchAsync(EnsureLocalBranchRequest req)
    {
        if (!GitArgs.IsSafeRef(req.Branch))
            return RunnerOpResult.Fail("Invalid branch name.");

        var bare = git.GetBareRepoPath(Ulid.Parse(req.OrgId), Ulid.Parse(req.RepoId));
        if (!Directory.Exists(bare))
            return RunnerOpResult.Fail("Repository cache not prepared yet.");

        // Hybrid (origin is a remote URL, not a folder here): there is no working copy to
        // materialize a local ref in, and none is needed — the cache already mirrors the forge,
        // so a fetch is the whole job. Failing instead would break the repo page's base-branch
        // picker for every hybrid project, with a message about a working repository the user
        // does not have.
        var userRepo = await GetUserRepoPathAsync(bare);
        if (userRepo is null)
        {
            var refresh = await RunGitAsync(bare, "fetch origin");
            if (!refresh.Success)
                return RunnerOpResult.Fail(refresh.Error!);

            return (await RunGitAsync(
                    bare, $"rev-parse --verify --quiet \"refs/remotes/origin/{req.Branch}\"")).Success
                ? RunnerOpResult.Ok()
                : RunnerOpResult.Fail($"Branch '{req.Branch}' was not found on its remote.");
        }

        var localExists = await RunGitAsync(userRepo, $"rev-parse --verify --quiet \"refs/heads/{req.Branch}\"");
        if (!localExists.Success)
        {
            var tracking = await RunGitAsync(
                userRepo, $"rev-parse --verify --quiet \"refs/remotes/origin/{req.Branch}\"");
            if (!tracking.Success)
                return RunnerOpResult.Fail(
                    $"Branch '{req.Branch}' was not found in your repository or on its remote.");

            // `git branch` only writes the ref — the user's working tree stays untouched.
            var create = await RunGitAsync(
                userRepo, $"branch \"{req.Branch}\" \"refs/remotes/origin/{req.Branch}\"");
            if (!create.Success)
                return RunnerOpResult.Fail($"Could not create local branch '{req.Branch}': {create.Error}");
            logger.LogInformation("Created local branch {Branch} in {Repo} from its remote-tracking ref",
                req.Branch, userRepo);
        }

        // Refresh the mirror so refs/remotes/origin/{branch} is immediately usable as a base.
        var fetch = await RunGitAsync(bare, "fetch origin");
        return fetch.Success ? RunnerOpResult.Ok() : RunnerOpResult.Fail(fetch.Error!);
    }

    public async Task<RunnerOpResult> CreateRepoBranchAsync(CreateRepoBranchRequest req)
    {
        if (!GitArgs.IsSafeRef(req.NewBranch) || !GitArgs.IsSafeRef(req.BaseBranch))
            return RunnerOpResult.Fail("Invalid branch name.");

        var bare = git.GetBareRepoPath(Ulid.Parse(req.OrgId), Ulid.Parse(req.RepoId));
        if (!Directory.Exists(bare))
            return RunnerOpResult.Fail("Repository cache not prepared yet.");

        // No working repository behind the cache means origin is a real remote, not a folder on
        // this machine — the hybrid shape: the user's runner executing against a forge repo.
        // Creating the ref in a working copy is then both impossible and beside the point; the
        // branch has to exist on the FORGE, because that is what the worktree forks from and
        // what a pull request can be based on.
        //
        // This is why milestone branches silently never appeared in hybrid: every attempt failed
        // here, the orchestrator degraded to the default branch, and each task merged straight to
        // main — exactly the per-task deploy the milestone branch exists to avoid.
        var userRepo = await GetUserRepoPathAsync(bare);
        if (userRepo is null)
        {
            // Hybrid: the cache's origin is a forge, and branches on a forge are created
            // through the forge API by the host — never by a ref push from this machine.
            return RunnerOpResult.Fail(
                "Branches on a forge repository are created through the forge API by Orkka, not from this machine.");
        }

        var exists = await RunGitAsync(userRepo, $"rev-parse --verify --quiet \"refs/heads/{req.NewBranch}\"");
        if (exists.Success)
            return RunnerOpResult.Fail($"Branch '{req.NewBranch}' already exists.");

        // Base resolves local-first, then the user repo's own remote-tracking ref — so a
        // branch that only lives on GitHub can be a base without a prior checkout.
        var baseRef = $"refs/heads/{req.BaseBranch}";
        if (!(await RunGitAsync(userRepo, $"rev-parse --verify --quiet \"{baseRef}\"")).Success)
        {
            baseRef = $"refs/remotes/origin/{req.BaseBranch}";
            if (!(await RunGitAsync(userRepo, $"rev-parse --verify --quiet \"{baseRef}\"")).Success)
                return RunnerOpResult.Fail(
                    $"Base branch '{req.BaseBranch}' was not found in your repository or on its remote.");
        }

        var create = await RunGitAsync(userRepo, $"branch \"{req.NewBranch}\" \"{baseRef}\"");
        if (!create.Success)
            return RunnerOpResult.Fail($"Could not create branch '{req.NewBranch}': {create.Error}");
        logger.LogInformation("Created branch {Branch} from {Base} in {Repo}",
            req.NewBranch, req.BaseBranch, userRepo);

        var fetch = await RunGitAsync(bare, "fetch origin");
        return fetch.Success ? RunnerOpResult.Ok() : RunnerOpResult.Fail(fetch.Error!);
    }

    /// <summary>Resolves the user's real working repository for a terminal session —
    /// where they actually work, never the bare cache or an agent worktree. Returns an
    /// actionable error instead of a path when the repo can't be located.</summary>
    public async Task<(string? Path, string? Error)> ResolveUserRepoPathAsync(string orgId, string repoId)
    {
        var bare = git.GetBareRepoPath(Ulid.Parse(orgId), Ulid.Parse(repoId));
        if (!Directory.Exists(bare))
            return (null, "Repository cache not prepared yet — open the Repos page to sync this repository.");

        var origin = await GetOriginUrlAsync(bare);
        if (string.IsNullOrEmpty(origin))
            return (null, "The repository cache on this machine has no origin — re-sync it from the Repos page.");

        // Orkka Local: the mirror's origin IS the user's working folder on this disk.
        if (!IsRemoteUrl(origin))
        {
            return Directory.Exists(origin)
                ? (origin, null)
                : (null, "The repository folder no longer exists at its linked path.");
        }

        // Hosted (hybrid) runner: the origin is a forge URL, so there is no user folder to
        // point at — the machine holds only the bare mirror plus per-task worktrees. Give
        // Run and the terminal a persistent checkout of the default branch instead.
        return await EnsureDefaultBranchCheckoutAsync(bare);
    }

    /// <summary>
    /// A long-lived working copy of the default branch, kept next to the task worktrees
    /// (<c>worktrees/main</c>) as a git worktree of the bare mirror. Fetched and fast-
    /// forwarded on every resolve, so "Run" starts what is actually on main today. Never
    /// reset or cleaned: the user may have run `npm install` there, and their
    /// <c>.env</c> lives there too — exactly the state a run needs and a fresh checkout would lose.
    /// </summary>
    private async Task<(string? Path, string? Error)> EnsureDefaultBranchCheckoutAsync(string bare)
    {
        var repoDir = Path.GetDirectoryName(bare)!;
        var checkout = Path.Combine(repoDir, "worktrees", "main");

        // Best effort — offline is not fatal, we just run what we have.
        var fetch = await RunGitAsync(bare, "fetch origin --prune");
        if (!fetch.Success)
            logger.LogWarning("Could not fetch {Bare} before resolving its checkout: {Error}", bare, fetch.Error);

        var branch = await DetectDefaultBranchAsync(bare);
        if (branch is null)
            return (null, "Could not determine the repository's default branch — re-sync it from the Repos page.");

        if (Directory.Exists(Path.Combine(checkout, ".git")) || File.Exists(Path.Combine(checkout, ".git")))
        {
            // Advance to origin's tip when it is a clean fast-forward. Diverged or dirty?
            // Leave the user's state alone and run it as is — silently discarding local
            // edits from a play button is not acceptable.
            var pull = await RunGitAsync(checkout, $"merge --ff-only origin/{branch}");
            if (!pull.Success)
                logger.LogInformation("Default-branch checkout at {Path} not fast-forwarded: {Error}", checkout, pull.Error);
            return (checkout, null);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(checkout)!);
        // A detached-HEAD worktree tracking origin/{branch}: no local branch to fight with
        // the task worktrees (which check out real branches), and --ff-only above keeps it
        // moving with the remote.
        var add = await RunGitAsync(bare, $"worktree add --detach \"{checkout}\" origin/{branch}");
        if (!add.Success)
        {
            // A stale registration (folder deleted by hand) blocks re-adding — prune and retry once.
            await RunGitAsync(bare, "worktree prune");
            add = await RunGitAsync(bare, $"worktree add --detach \"{checkout}\" origin/{branch}");
            if (!add.Success)
                return (null, $"Could not create a working copy of {branch}: {add.Error}");
        }

        logger.LogInformation("Created default-branch checkout for hosted repo at {Path}", checkout);
        return (checkout, null);
    }

    private static async Task<string?> DetectDefaultBranchAsync(string bare)
    {
        // origin/HEAD is set by clone; fall back to the usual suspects when it is missing.
        var head = await RunGitAsync(bare, "symbolic-ref --short refs/remotes/origin/HEAD");
        if (head.Success && head.Value?.Trim() is { Length: > 0 } symbolic)
            return symbolic.Trim().StartsWith("origin/") ? symbolic.Trim()["origin/".Length..] : symbolic.Trim();

        foreach (var candidate in new[] { "main", "master", "develop" })
        {
            var exists = await RunGitAsync(bare, $"rev-parse --verify --quiet refs/remotes/origin/{candidate}");
            if (exists.Success)
                return candidate;
        }
        return null;
    }

    private static bool IsRemoteUrl(string origin) =>
        origin.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || origin.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || origin.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)
        || origin.StartsWith("git@", StringComparison.OrdinalIgnoreCase);

    /// <summary>The mirror's origin as configured. Not <c>remote get-url</c>: that applies the
    /// developer's <c>url.*.insteadOf</c> rewrites, so a GitHub repository reached through a
    /// mirror (or a local path) read as "no remote" — publishing failed with "This repository
    /// has no remote to open a pull request on" — or named the mirror's host to the forge CLI.
    /// Git still applies the rewrite to the fetches and pushes themselves.</summary>
    internal static async Task<string?> GetOriginUrlAsync(string bare)
    {
        var origin = await RunGitAsync(bare, "config --get remote.origin.url");
        return origin.Success ? origin.Value?.Trim() : null;
    }

    // ── Editor escape hatch ─────────────────────────────────────────────────

    /// <summary>Editors installed on this machine, so the UI only offers what will work.</summary>
    public Task<ListEditorsResult> ListEditorsAsync()
        => Task.FromResult(new ListEditorsResult(
            [.. EditorResolver.ListInstalled().Select(e => new RunnerEditor(e.Key, e.DisplayName))]));

    /// <summary>
    /// Opens a task's worktree in a local editor. This is the "let me finish it myself"
    /// escape hatch: the agent's branch is already checked out there, so edits made here are
    /// picked up by the next phase (or by the commit the dev agent makes on resume).
    /// </summary>
    public async Task<RunnerOpResult> OpenInEditorAsync(OpenInEditorRequest req)
    {
        string path;
        if (!string.IsNullOrEmpty(req.RepoToken))
        {
            // The repository itself, on its default branch: the user's working folder on
            // Orkka Local, the persistent main checkout for hosted repos (created on first
            // use, same folder Run starts the app in).
            var (repoPath, repoError) = await ResolveRepoTokenAsync(req.RepoToken);
            if (repoPath is null)
                return RunnerOpResult.Fail(repoError ?? "Could not find that repository on this machine.");
            path = repoPath;
        }
        else
        {
            if (!TryResolveWorktree(req.WorktreeToken, out path, out var tokenError))
                return RunnerOpResult.Fail(tokenError);

            if (!Directory.Exists(path))
                return RunnerOpResult.Fail(
                    "This task has no worktree on this machine yet — it appears once an agent starts working.");
        }

        var editor = EditorResolver.Resolve(req.Editor);
        if (editor is null)
        {
            return RunnerOpResult.Fail(req.Editor is { Length: > 0 } requested
                ? $"'{requested}' isn't installed on this machine, or its command-line launcher isn't on PATH."
                : "No supported editor found on PATH. Install VS Code, Cursor, Windsurf, Zed, Sublime Text, or a JetBrains IDE.");
        }

        try
        {
            // UseShellExecute=false keeps the editor from inheriting a console window; the
            // resolved path is an absolute launcher from the allowlist, never wire input.
            //
            // Batch launchers need cmd.exe: on Windows every editor that adds itself to PATH
            // does so via a .cmd shim (VS Code's bin\code.cmd, Cursor, WebStorm…), and
            // CreateProcess — which UseShellExecute=false uses — refuses to exec a batch
            // file ("not a valid Win32 application"). This single line is why the Open in
            // VS Code button failed for every Windows user. Same workaround the Playwright
            // MCP spawn uses (ClaudeAgentInvoker).
            var isBatchLauncher = editor.Path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                || editor.Path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

            var psi = new ProcessStartInfo
            {
                FileName = isBatchLauncher ? "cmd.exe" : editor.Path,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (isBatchLauncher)
            {
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add(editor.Path);
            }
            psi.ArgumentList.Add(path);

            Process.Start(psi);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to launch {Editor} for {Path}", editor.Key, path);
            return RunnerOpResult.Fail($"Could not launch {editor.DisplayName}: {ex.Message}");
        }

        logger.LogInformation("Opened {Path} in {Editor}", path, editor.DisplayName);
        return RunnerOpResult.Ok(editor.DisplayName);
    }

    /// <summary>Resolves a <see cref="RunnerTokens.UserRepo"/> wire token to the user's
    /// working repository path — the v3 ops (sync status, push, publish, squash-merge)
    /// never receive filesystem paths from the host.</summary>
    private async Task<(string? Path, string? Error)> ResolveRepoTokenAsync(string repoToken)
    {
        if (!RunnerTokens.TryParseUserRepo(repoToken, out var orgId, out var repoId))
            return (null, "Invalid repository token.");
        return await ResolveUserRepoPathAsync(orgId, repoId);
    }

    /// <summary>The bare mirror's origin URL is the user's working repository path (that is
    /// how local repos are wired) — null when it isn't a directory on this machine.</summary>
    private static async Task<string?> GetUserRepoPathAsync(string bare)
    {
        var origin = await RunGitAsync(bare, "remote get-url origin");
        if (!origin.Success)
            return null;
        var path = origin.Value?.Trim();
        return !string.IsNullOrEmpty(path) && Directory.Exists(path) ? path : null;
    }

    public async Task<RunnerTreeResult> ListTreeAsync(ListTreeRequest req)
    {
        var bare = git.GetBareRepoPath(Ulid.Parse(req.OrgId), Ulid.Parse(req.RepoId));
        if (!Directory.Exists(bare))
            return new RunnerTreeResult(false, null, "Repository cache not prepared yet.");
        if (!GitArgs.IsSafeRef(req.Branch) || (!string.IsNullOrEmpty(req.DirPath) && !GitArgs.IsSafePath(req.DirPath)))
            return new RunnerTreeResult(false, null, "Invalid branch or path.");

        var result = await RunGitAsync(bare, GitArgs.TreeLevel($"origin/{req.Branch}", req.DirPath));
        if (!result.Success)
            return new RunnerTreeResult(false, null, result.Error);

        var entries = GitArgs.ParseTreeLevel(result.Value ?? "")
            .Select(e => new RunnerTreeEntry(e.Name, e.Path, e.IsDirectory))
            .ToArray();
        return new RunnerTreeResult(true, entries, null);
    }

    public async Task<RunnerOpResult> GetCommitDiffAsync(GetCommitDiffRequest req)
    {
        var bare = git.GetBareRepoPath(Ulid.Parse(req.OrgId), Ulid.Parse(req.RepoId));
        if (!Directory.Exists(bare))
            return RunnerOpResult.Fail("Repository cache not prepared yet.");
        if (!GitArgs.IsSafeSha(req.Sha))
            return RunnerOpResult.Fail("Invalid commit sha.");

        var result = await RunGitAsync(bare, GitArgs.CommitDiff(req.Sha));
        if (!result.Success)
            return result;

        var diff = result.Value ?? "";
        if (diff.Length > req.MaxChars)
            diff = diff[..req.MaxChars] + "\n... (diff truncated)";
        return RunnerOpResult.Ok(diff);
    }

    // ── Remote sync of the user's working repository ────────────────────────

    public async Task<RunnerRemoteSyncStatus> GetRemoteSyncStatusAsync(RemoteSyncStatusRequest req)
    {
        var (repo, resolveError) = await ResolveRepoTokenAsync(req.RepoToken);
        if (repo is null)
            return new RunnerRemoteSyncStatus(false, false, false, null, 0, 0, resolveError);
        if (!Directory.Exists(Path.Combine(repo, ".git")))
            return new RunnerRemoteSyncStatus(false, false, false, null, 0, 0, "Not a git repository.");

        var branchResult = await RunGitAsync(repo, "rev-parse --abbrev-ref HEAD");
        if (!branchResult.Success)
            return new RunnerRemoteSyncStatus(true, false, false, null, 0, 0, branchResult.Error);
        var branch = branchResult.Value!.Trim();

        var remoteResult = await RunGitAsync(repo, "remote get-url origin");
        if (!remoteResult.Success)
            return new RunnerRemoteSyncStatus(true, false, false, branch, 0, 0, null);

        // Counts against the locally known origin refs (same as `git status`) — no fetch,
        // so status stays instant and offline-safe.
        var aheadResult = await RunGitAsync(repo, $"rev-list --count \"origin/{branch}..{branch}\"");
        if (!aheadResult.Success)
        {
            // origin/{branch} unknown locally — branch was never pushed.
            return new RunnerRemoteSyncStatus(true, true, false, branch, 0, 0, null, remoteResult.Value?.Trim());
        }
        var behindResult = await RunGitAsync(repo, $"rev-list --count \"{branch}..origin/{branch}\"");

        _ = int.TryParse(aheadResult.Value?.Trim(), out var ahead);
        _ = int.TryParse(behindResult.Value?.Trim(), out var behind);
        return new RunnerRemoteSyncStatus(true, true, true, branch, ahead, behind, null, remoteResult.Value?.Trim());
    }

    public async Task<RunnerOpResult> PushToRemoteAsync(PushToRemoteRequest req)
    {
        if (!GitArgs.IsSafeRef(req.Branch))
            return RunnerOpResult.Fail("Invalid branch name.");
        var (repoPath, resolveError) = await ResolveRepoTokenAsync(req.RepoToken);
        if (repoPath is null)
            return RunnerOpResult.Fail(resolveError!);
        if (!Directory.Exists(Path.Combine(repoPath, ".git")))
            return RunnerOpResult.Fail("Not a git repository.");

        logger.LogInformation("Pushing {Branch} of {Repo} to origin", req.Branch, repoPath);
        // The developer's own working copy, pushed to the remote THEY configured with their
        // own git credentials. -u so a first push creates the upstream; prompts are disabled,
        // so a missing credential fails fast.
        var result = await RunGitAsync(repoPath, $"push -u origin \"{req.Branch}\"");
        return result.Success ? RunnerOpResult.Ok() : RunnerOpResult.Fail(result.Error!);
    }

    /// <summary>
    /// Publishes a reviewed task: the branch is already on the remote (the host pushes it after
    /// every run), so this proves that and opens the pull request with the developer's CLI.
    /// </summary>
    public async Task<RunnerOpResult> OpenPullRequestAsync(OpenPullRequestRequest req)
    {
        if (!GitArgs.IsSafeRef(req.HeadBranch) || !GitArgs.IsSafeRef(req.BaseBranch))
            return RunnerOpResult.Fail("Invalid branch name.");
        if (!Enum.TryParse<Orkka.SharedKernel.Utilities.ForgeKind>(req.Forge, ignoreCase: true, out var forge))
            forge = Orkka.SharedKernel.Utilities.ForgeKind.Other;

        var bare = git.GetBareRepoPath(Ulid.Parse(req.OrgId), Ulid.Parse(req.RepoId));
        var origin = Directory.Exists(bare) ? await GetOriginUrlAsync(bare) : null;
        if (string.IsNullOrEmpty(origin) || !IsRemoteUrl(origin))
            return RunnerOpResult.Fail("This repository has no remote to open a pull request on.");

        var onRemote = await RunGitAsync(bare, $"ls-remote --exit-code --heads origin \"{req.HeadBranch}\"");
        if (!onRemote.Success)
            return RunnerOpResult.Fail(
                $"Branch '{req.HeadBranch}' is not on the remote yet — it is pushed after every run; retry once the task's last run has finished.");

        return await forgeCli.OpenAsync(origin, forge, req.CliAccount,
            req.HeadBranch, req.BaseBranch, req.Title, req.Body, CancellationToken.None, req.Draft);
    }

    public async Task<RunnerOpResult> MarkPullRequestReadyAsync(MarkPullRequestReadyRequest req)
    {
        var (origin, forge, error) = await ForgeOriginAsync(req.OrgId, req.RepoId, req.Forge);
        return origin is null
            ? RunnerOpResult.Fail(error!)
            : await forgeCli.MarkReadyAsync(origin, forge, req.CliAccount, req.PullRequestUrl, req.Undo, CancellationToken.None);
    }

    public async Task<RunnerOpResult> UpdatePullRequestBodyAsync(UpdatePullRequestBodyRequest req)
    {
        var (origin, forge, error) = await ForgeOriginAsync(req.OrgId, req.RepoId, req.Forge);
        return origin is null
            ? RunnerOpResult.Fail(error!)
            : await forgeCli.UpdateBodyAsync(origin, forge, req.CliAccount, req.PullRequestUrl, req.Body, CancellationToken.None);
    }

    private async Task<(string? Origin, Orkka.SharedKernel.Utilities.ForgeKind Forge, string? Error)> ForgeOriginAsync(
        string orgId, string repoId, string forgeName)
    {
        if (!Enum.TryParse<Orkka.SharedKernel.Utilities.ForgeKind>(forgeName, ignoreCase: true, out var forge))
            forge = Orkka.SharedKernel.Utilities.ForgeKind.Other;
        var bare = git.GetBareRepoPath(Ulid.Parse(orgId), Ulid.Parse(repoId));
        var origin = Directory.Exists(bare) ? await GetOriginUrlAsync(bare) : null;
        return string.IsNullOrEmpty(origin) || !IsRemoteUrl(origin)
            ? (null, forge, "This repository has no remote pull request to change.")
            : (origin, forge, null);
    }

    public async Task<RunnerOpResult> GetPullRequestStateAsync(GetPullRequestStateRequest req)
    {
        if (!Enum.TryParse<Orkka.SharedKernel.Utilities.ForgeKind>(req.Forge, ignoreCase: true, out var forge))
            forge = Orkka.SharedKernel.Utilities.ForgeKind.Other;

        var bare = git.GetBareRepoPath(Ulid.Parse(req.OrgId), Ulid.Parse(req.RepoId));
        var origin = Directory.Exists(bare) ? await GetOriginUrlAsync(bare) : null;
        if (string.IsNullOrEmpty(origin))
            return RunnerOpResult.Fail("Repository cache not prepared yet.");

        return await forgeCli.GetStateAsync(origin, forge, req.CliAccount, req.PullRequestUrl, CancellationToken.None);
    }

    public async Task<RunnerOpResult> PublishRepoToGitHubAsync(PublishRepoToGitHubRequest req)
    {
        var (repoPath, resolveError) = await ResolveRepoTokenAsync(req.RepoToken);
        if (repoPath is null)
            return RunnerOpResult.Fail(resolveError!);
        if (!Directory.Exists(Path.Combine(repoPath, ".git")))
            return RunnerOpResult.Fail("Not a git repository.");

        var name = string.IsNullOrWhiteSpace(req.Name)
            ? Path.GetFileName(repoPath.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : req.Name.Trim();
        if (string.IsNullOrWhiteSpace(name) ||
            !System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9][A-Za-z0-9._-]*$"))
        {
            return RunnerOpResult.Fail(
                "Repository names can use letters, digits, dots, dashes and underscores, starting with a letter or digit.");
        }

        var origin = await RunGitAsync(repoPath, "remote get-url origin");
        if (origin.Success)
            return RunnerOpResult.Fail(
                $"This repository already has an origin remote ({origin.Value?.Trim()}).");

        var branch = await RunGitAsync(repoPath, "rev-parse --abbrev-ref HEAD");
        if (!branch.Success)
            return RunnerOpResult.Fail(
                "Repository has no commits yet — create an initial commit first.");

        // The user's own gh login IS the GitHub credential — the runner never holds
        // tokens. Probe auth first so a logged-out gh fails with a remedy instead of
        // gh's own multi-line status dump.
        var auth = await RunProcessAsync("gh", repoPath, "auth status");
        if (auth is null)
            return RunnerOpResult.Fail(
                "The GitHub CLI (gh) is not installed on this machine — install it from https://cli.github.com, run `gh auth login`, then try again.");
        if (!auth.Success)
            return RunnerOpResult.Fail(
                "The GitHub CLI is not logged in on this machine — run `gh auth login` in a terminal, then try again.");

        var visibility = req.Private ? "--private" : "--public";
        var create = await RunProcessAsync("gh", repoPath,
            $"repo create \"{name}\" {visibility} --source \"{repoPath}\" --remote origin --push");
        if (create is null || !create.Success)
            return RunnerOpResult.Fail(
                $"Could not create the GitHub repository: {create?.Error ?? "gh could not be started."}");

        logger.LogInformation("Published {Repo} to GitHub as {Name}", repoPath, name);

        // gh prints the new repo's URL; fall back to a plain OK if the format changes.
        var url = create.Value?
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => l.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        return RunnerOpResult.Ok(url);
    }

    /// <summary>
    /// Points a local repo that has no origin at a remote the user already created, and
    /// pushes to it with the machine's own git credentials. Split from
    /// <see cref="PublishRepoToGitHubAsync"/>, which creates the repository with the user's
    /// `gh` CLI; this is everything after creation, on any forge.
    /// </summary>
    public async Task<RunnerOpResult> AttachRemoteAsync(AttachRemoteRequest req)
    {
        var (repoPath, resolveError) = await ResolveRepoTokenAsync(req.RepoToken);
        if (repoPath is null)
            return RunnerOpResult.Fail(resolveError!);
        if (!Directory.Exists(Path.Combine(repoPath, ".git")))
            return RunnerOpResult.Fail("Not a git repository.");

        // https only, and never with credentials baked in: a file:// one would quietly point
        // the repo at another folder on the same disk.
        if (!Uri.TryCreate(req.RemoteUrl, UriKind.Absolute, out var remote)
            || !remote.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(remote.UserInfo))
        {
            return RunnerOpResult.Fail(
                "The remote must be an https:// repository URL with no credentials in it.");
        }

        var existing = await RunGitAsync(repoPath, "remote get-url origin");
        if (existing.Success)
        {
            return RunnerOpResult.Fail(
                $"This repository already has an origin remote ({existing.Value?.Trim()}).");
        }

        var branchResult = await RunGitAsync(repoPath, "rev-parse --abbrev-ref HEAD");
        if (!branchResult.Success)
            return RunnerOpResult.Fail("Repository has no commits yet — create an initial commit first.");

        var branch = branchResult.Value!.Trim();
        if (!GitArgs.IsSafeRef(branch))
            return RunnerOpResult.Fail($"Cannot push branch '{branch}'.");

        // origin gets the CLEAN url.
        var cleanUrl = $"https://{remote.Host}{remote.AbsolutePath}";
        var add = await RunGitAsync(repoPath, $"remote add origin \"{cleanUrl}\"");
        if (!add.Success)
            return RunnerOpResult.Fail($"Could not set the origin remote: {add.Error}");

        if (!req.Push)
            return RunnerOpResult.Ok(cleanUrl);

        // The developer's own git credentials push. No `-u`: upstream is wired by hand
        // further down.
        var push = await RunGitAsync(repoPath,
            $"push origin \"refs/heads/{branch}:refs/heads/{branch}\"");

        if (!push.Success)
        {
            // Leave nothing half-wired: with origin removed the UI still offers "attach a
            // remote", which is a retry the user recognizes.
            await RunGitAsync(repoPath, "remote remove origin");

            var detail = push.Error;
            if (detail?.Contains("fetch first", StringComparison.OrdinalIgnoreCase) == true
                || detail?.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase) == true)
            {
                return RunnerOpResult.Fail(
                    $"{cleanUrl} already has commits in it. Create the repository on GitHub " +
                    "WITHOUT a README, .gitignore or licence — Orkka pushes your folder's own " +
                    "history, which cannot merge into a repo that already has its own.");
            }
            return RunnerOpResult.Fail($"Could not push to {cleanUrl}: {detail}");
        }

        // Materialize origin/{branch} and the tracking config ourselves. A plain push
        // writes neither, and without them GetRemoteSyncStatus reports the branch as never
        // pushed — the one thing we just proved false.
        var head = await RunGitAsync(repoPath, "rev-parse HEAD");
        if (head.Success && head.Value?.Trim() is { Length: > 0 } sha && GitArgs.IsSafeSha(sha))
            await RunGitAsync(repoPath, $"update-ref \"refs/remotes/origin/{branch}\" {sha}");

        await RunGitAsync(repoPath, $"config \"branch.{branch}.remote\" origin");
        await RunGitAsync(repoPath, $"config \"branch.{branch}.merge\" \"refs/heads/{branch}\"");

        logger.LogInformation("Attached {Repo} to {Url} and pushed {Branch}",
            repoPath, cleanUrl, branch);
        return RunnerOpResult.Ok(cleanUrl);
    }

    // ── Merge-back into the user's working repository ───────────────────────

    public async Task<RunnerOpResult> SquashMergeIntoUserRepoAsync(SquashMergeRequest req)
    {
        var (repoPath, resolveError) = await ResolveRepoTokenAsync(req.RepoToken);
        if (repoPath is null)
            return RunnerOpResult.Fail(resolveError!);
        if (!Directory.Exists(repoPath))
            return RunnerOpResult.Fail($"Repository path no longer exists: {repoPath}");

        // One merge into this folder at a time, from the first thing it touches. The gate used to
        // be taken after the receive policy was written, so two merges landing together both
        // wrote the folder's .git/config at once and one failed "could not lock config file"
        // (CI, 2026-09-17 — the same race as a prod pair landing in one folder).
        var gate = MergeGates.GetOrAdd(MergeGateKey(repoPath), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            return await SquashMergeGatedAsync(req, repoPath);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<RunnerOpResult> SquashMergeGatedAsync(SquashMergeRequest req, string repoPath)
    {
        // The local forge rule: Orkka never merges INSIDE the user's working copy. The merge
        // happens in a scratch clone Orkka owns and lands in the user's repository as a push
        // — a plain ref update when the base branch is not checked out there, and, through
        // `receive.denyCurrentBranch=updateInstead`, a fast-forward of the working tree when
        // it is checked out AND clean. A dirty tree refuses the push with git's own reason and
        // the user's files are never touched; no branch is ever switched under them.
        var branchExists = await RunGitAsync(repoPath, $"rev-parse --verify --quiet \"refs/heads/{req.HeadBranch}\"");
        if (!branchExists.Success)
            return RunnerOpResult.Fail(
                $"Branch '{req.HeadBranch}' is not in your repository — the agent may not have pushed its work.");

        var baseExists = await RunGitAsync(repoPath, $"rev-parse --verify --quiet \"refs/heads/{req.BaseBranch}\"");
        if (!baseExists.Success)
            return RunnerOpResult.Fail($"Branch '{req.BaseBranch}' is not in your repository.");

        // The receive policy is passed to the receiving git for this one push (--receive-pack
        // below), never written to the user's .git/config: Orkka changes no config it does not own.
        var scratch = Path.Combine(_basePath, "merge-scratch", $"{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.GetDirectoryName(scratch)!);
        try
        {
            using var op = Orkka.Git.GitOperationScope.Begin(
                Orkka.Git.NullGitOperationLedger.Instance, Orkka.Git.GitOperationKind.SquashMerge);

            var clone = await RunGitAsync(Path.GetDirectoryName(scratch)!,
                $"clone --no-checkout --branch \"{req.BaseBranch}\" \"{repoPath}\" \"{scratch}\"", TimeSpan.FromMinutes(10));
            if (!clone.Success)
                return RunnerOpResult.Fail($"Could not prepare a merge workspace: {clone.Error}");

            var checkout = await RunGitAsync(scratch, $"checkout \"{req.BaseBranch}\"", TimeSpan.FromMinutes(10));
            if (!checkout.Success)
                return RunnerOpResult.Fail($"Could not check out '{req.BaseBranch}' for the merge: {checkout.Error}");

            var merge = await RunGitAsync(scratch, $"merge --squash \"origin/{req.HeadBranch}\"", TimeSpan.FromMinutes(10));
            if (!merge.Success)
            {
                return RunnerOpResult.Fail(
                    $"Merge conflict between '{req.HeadBranch}' and '{req.BaseBranch}': {merge.Error} " +
                    "Resolve by merging manually, or move the task back to Developing so the agent rebases.");
            }

            // The message carries the whole PR body, which routinely runs to tens of KB.
            // Passing it as -m puts it on the command line, and Windows' CreateProcess caps
            // that at 32,767 chars. -F takes the text from a file verbatim.
            var messageFile = Path.Combine(Path.GetTempPath(), $"orkka-merge-{Guid.NewGuid():N}.txt");
            RunnerOpResult commit;
            try
            {
                await File.WriteAllTextAsync(messageFile, req.CommitMessage);
                commit = await RunGitAsync(scratch, $"commit -F \"{messageFile}\"");
            }
            finally
            {
                try { File.Delete(messageFile); } catch { /* temp file, best effort */ }
            }

            if (!commit.Success)
                return RunnerOpResult.Fail($"Commit failed after merge: {commit.Error}");

            // Local transport runs the receiving git through a shell with this exact command, so
            // `updateInstead` applies to this push only (GIT_CONFIG_* would be stripped from it).
            var push = await RunGitAsync(scratch,
                $"push \"--receive-pack=git -c receive.denyCurrentBranch=updateInstead receive-pack\" origin \"{req.BaseBranch}:{req.BaseBranch}\"",
                TimeSpan.FromMinutes(10));
            if (!push.Success)
            {
                var dirty = push.Error?.Contains("unstaged changes", StringComparison.OrdinalIgnoreCase) == true
                    || push.Error?.Contains("staged changes", StringComparison.OrdinalIgnoreCase) == true
                    || push.Error?.Contains("Working directory has", StringComparison.OrdinalIgnoreCase) == true;
                return RunnerOpResult.Fail(dirty
                    ? $"Your repository has uncommitted changes on '{req.BaseBranch}'. Commit or stash them, then merge again — the merge is ready and will land the moment the tree is clean."
                    : $"The merge could not be landed in your repository: {push.Error}");
            }

            op.Complete();
            logger.LogInformation("Squash-merged {Head} into {Base} of {Path} (via scratch clone)",
                req.HeadBranch, req.BaseBranch, repoPath);
            return RunnerOpResult.Ok();
        }
        finally
        {
            try { Orkka.Modules.Execution.Git.GitWorkspaceManager.DeleteDirectoryTree(scratch); }
            catch (Exception ex) { logger.LogDebug(ex, "Could not remove the merge scratch clone at {Path}", scratch); }
        }
    }

    // ── Chat workspace (read-only worktrees per repo) ───────────────────────

    public async Task<RunnerOpResult> EnsureChatWorkspaceAsync(EnsureChatWorkspaceRequest req)
    {
        try
        {
            // Scoped per project when the host sends a ProjectId (worktrees persist between
            // chats — an org-shared folder leaks other projects' code into the agent's Cwd);
            // a pre-project-scoping host omits it and keeps the legacy org-level layout.
            // A TaskId (with a project) is a task's Discuss chat: its own folder, so it never
            // moves the project chat's checkout and never touches the task's running worktree.
            var taskId = string.IsNullOrEmpty(req.ProjectId) ? null : req.TaskId;
            var workspacePath = ChatWorkspacePath(req.OrgId, req.ProjectId, taskId);
            Directory.CreateDirectory(workspacePath);

            foreach (var repo in req.Repos)
            {
                var bare = git.GetBareRepoPath(Ulid.Parse(req.OrgId), Ulid.Parse(repo.RepoId));
                if (!Directory.Exists(bare))
                    continue;

                var repoWorkspace = Path.Combine(workspacePath, repo.Name);
                var branch = await ChatBranchAsync(bare, repo, taskId is not null);

                if (Directory.Exists(repoWorkspace))
                {
                    await RunGitAsync(repoWorkspace, $"reset --hard origin/{branch}");
                }
                else
                {
                    var result = await RunGitAsync(bare,
                        $"worktree add \"{repoWorkspace}\" origin/{branch} --detach");
                    if (!result.Success)
                        logger.LogWarning("Chat worktree failed for {Repo}: {Error}", repo.Name, result.Error);
                }
            }

            return RunnerOpResult.Ok(RunnerTokens.ChatWorkspace(req.OrgId, req.ProjectId, taskId));
        }
        catch (Exception ex)
        {
            return RunnerOpResult.Fail($"Chat workspace preparation failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The branch a chat worktree is detached at: the requested branch (a task's Discuss chat
    /// reads the task branch) when it is a well-formed name that exists on the remote — the
    /// mirror's <c>refs/remotes/origin/*</c>, refreshed before every turn — else the default
    /// branch. A task branch that was never pushed reads as the default branch: the chat then
    /// discusses the base the work will start from, which is still the right code to read.
    /// </summary>
    internal static async Task<string> ChatBranchAsync(string bare, ChatWorkspaceRepoDto repo, bool honorBranch)
    {
        var fallback = repo.DefaultBranch ?? "main";
        if (!honorBranch || string.IsNullOrWhiteSpace(repo.Branch) || !IsSafeBranchName(repo.Branch))
            return fallback;

        var exists = await RunGitAsync(bare, $"rev-parse --verify --quiet \"refs/remotes/origin/{repo.Branch}\"");
        return exists.Success ? repo.Branch : fallback;
    }

    /// <summary>A branch name that can be put on a git command line as-is: the characters git
    /// branch names are made of in practice, no ref-format traps (<c>..</c>, a leading dash or
    /// slash, <c>.lock</c>). Anything else falls back to the default branch.</summary>
    internal static bool IsSafeBranchName(string branch)
        => branch.Length is > 0 and <= 250
            && branch.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/')
            && !branch.StartsWith('-') && !branch.StartsWith('/') && !branch.EndsWith('/')
            && !branch.Contains("..", StringComparison.Ordinal)
            && !branch.Contains("//", StringComparison.Ordinal)
            && !branch.EndsWith(".lock", StringComparison.Ordinal);

    // ── Agent runs (the developer's own vendor CLI sign-in; no credential from the host) ─

    public IAsyncEnumerable<RunnerChunk> ExecuteAgentRun(RunnerAgentRunRequest req, CancellationToken ct)
    {
        if (!TryResolveWorktree(req.WorktreeToken, out var worktreePath, out var error))
            return ErrorStream(error);

        // Re-check credentials at run time (the host pre-flight works off the last Hello,
        // which can be up to a minute stale — tokens may have expired since).
        if (ProviderBlocker(req.Provider) is { } blocked)
            return ErrorStream(blocked);

        CopyTicketAttachments(req.TicketAttachments, worktreePath);

        // Project-context tools: the host sent the definitions; calls forward back to it
        // over CallProjectTool. Requires a task id — that is the key the host's registry
        // answers under.
        var toolServer = req.ProjectTools is { Length: > 0 } && req.TaskId is not null
            ? new HostProjectToolServer(req.TaskId, req.ProjectTools, hostLink, logger)
            : null;

        var request = new AgentRequest(
            SystemPrompt: req.SystemPrompt,
            UserMessage: req.UserMessage,
            WorktreePath: worktreePath,
            Provider: ParseProvider(req.Provider),
            Model: req.Model,
            MaxTokens: req.MaxTokens,
            MaxTurns: req.MaxTurns,
            Phase: Enum.TryParse<AgentPhase>(req.Phase, out var phase) ? phase : AgentPhase.Planning,
            AgentName: req.AgentName,
            AgentRole: req.AgentRole,
            OrgId: Ulid.Parse(req.OrgId),
            ProjectId: Ulid.Parse(req.ProjectId),
            TaskId: req.TaskId is null ? null : Ulid.Parse(req.TaskId),
            AgentId: req.AgentId is null ? null : Ulid.Parse(req.AgentId),
            ExecutionId: req.ExecutionId is null ? null : Ulid.Parse(req.ExecutionId),
            ResumeSessionId: req.ResumeSessionId,
            MaxToolCalls: req.MaxToolCalls,
            MaxWallClockSeconds: req.MaxWallClockSeconds,
            Interactive: req.Interactive,
            ApiTesting: req.ApiTesting,
            ReadOnlyDirs: ExistingDirs(req.ReadOnlyDirs),
            Workspace: workspaceName)
        { ToolServer = toolServer };

        return RunGated(req.RunId, req.TaskId, token => MapChunks(invoker.InvokeAsync(request, token), token), ct);
    }

    /// <summary>
    /// An agent run on this machine: waits for one of the machine-wide run slots (shared by
    /// every workspace), and routes its approval questions to this session's host while it
    /// runs. The invoker is only called once the slot is held, so a queued run spawns nothing.
    /// </summary>
    internal async IAsyncEnumerable<RunnerChunk> RunGated(
        string runId, string? taskId, Func<CancellationToken, IAsyncEnumerable<RunnerChunk>> run,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (runSlots is { Available: 0 })
            logger.LogInformation("Run {RunId} waits for a free agent slot ({Capacity} running on this machine)",
                runId, runSlots.Capacity);
        using var slot = runSlots is null ? null : await runSlots.AcquireAsync(ct);
        using var route = router?.Route(taskId, hostLink);
        await foreach (var chunk in run(ct).WithCancellation(ct))
            yield return chunk;
    }

    public IAsyncEnumerable<RunnerChunk> ExecuteChatRun(RunnerChatRunRequest req, CancellationToken ct)
    {
        if (!RunnerTokens.TryParseChatWorkspace(req.WorkspaceToken, out var orgId, out var projectId, out var taskId))
            return ErrorStream($"Unrecognized chat workspace token: {req.WorkspaceToken}");

        if (ProviderBlocker(req.Provider) is { } blocked)
            return ErrorStream(blocked);

        // Platform tools (create_document, create_task, remember…): the host sent the
        // definitions and answers each call over CallProjectTool, keyed by this run's id.
        // Same mechanism the pipeline uses; the key is the run rather than a task because a
        // chat turn has no task. Null when the host sent none (older host, cloud-routed
        // turn, or a provider without tool support) — the turn then behaves as before.
        var chatToolServer = req.ProjectTools is { Length: > 0 }
            ? new HostProjectToolServer(req.RunId, req.ProjectTools, hostLink, logger)
            : null;

        // Chat = read-only tools over the shared workspace plus the web: the Research phase
        // profile of the shared invoker, matching AgentChatService.ReadOnlyTools on the
        // hosted side so the same question gets the same tools either way.
        // ResumeSessionId reuses the conversation's stored CLI session on this machine
        // (claude sessions; codex threads via exec resume).
        var request = new AgentRequest(
            SystemPrompt: req.SystemPrompt,
            UserMessage: req.UserMessage,
            // A task's Discuss chat runs in the task's own chat workspace (the task branch), with
            // the same read-only Research profile — never in the task's worktree.
            WorktreePath: ChatWorkspacePath(orgId, projectId, taskId),
            Provider: ParseProvider(req.Provider),
            Model: req.Model,
            // Fable-class models batch parallel tool calls and explore far deeper than the
            // Sonnet-era 30-turn budget — ecosystem-wide diagram turns died on error_max_turns.
            MaxTurns: 100,
            Phase: AgentPhase.Research,
            ResumeSessionId: req.ResumeSessionId,
            ReadOnlyDirs: ExistingDirs(req.ReadOnlyDirs),
            Workspace: workspaceName)
        { ToolServer = chatToolServer };

        return MapChunks(invoker.InvokeAsync(request, ct), ct);
    }

    /// <summary>The read-only folders the host named that are real, absolute directories on
    /// this machine and not inside the Orkka home — a host can widen what an agent reads to the
    /// developer's own base folder, never to Orkka's clones of other workspaces.</summary>
    internal static string[]? ExistingDirs(string[]? dirs)
    {
        if (dirs is not { Length: > 0 })
            return null;

        var orkkaHome = DeveloperSecrets.OrkkaHome();
        var kept = dirs
            .Where(d => !string.IsNullOrWhiteSpace(d) && Path.IsPathFullyQualified(d))
            .Select(d => Path.TrimEndingDirectorySeparator(Path.GetFullPath(d)))
            .Where(Directory.Exists)
            .Where(d => RepoDiscovery.RejectPath(d) is null)
            .Where(d => !DeveloperSecrets.IsSensitivePath(
                AgentWorktreeBoundaryGuard.Normalize(d), worktree: null, orkkaHome))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return kept.Length == 0 ? null : kept;
    }

    public async Task<RunnerUtilityResult> UtilityCompleteAsync(RunnerUtilityRequest req)
    {
        // Receipt log: the host times these ops out ("Invocation canceled by the server")
        // and its only failure evidence is that timeout — without this line there is no
        // way to tell "op never reached the runner" from "generation outran the timeout".
        var sw = System.Diagnostics.Stopwatch.StartNew();
        logger.LogInformation(
            "Utility op received — model {Model}, provider {Provider}, maxTokens {MaxTokens}, prompt {PromptChars} chars",
            req.Model, req.Provider ?? "claude", req.MaxTokens,
            (req.System?.Length ?? 0) + req.UserMessage.Length);

        // Same fresh credential re-check the agent/chat runs do — a stale host pre-flight
        // should fail with an actionable message, not a CLI 401.
        if (ProviderBlocker(req.Provider) is { } blocked)
            return new RunnerUtilityResult(null, 0, 0, 0, 0, blocked);

        // One-shot headless run in a scratch directory: no repo context. Phase Utility
        // strips the tool schema, so the run cannot wander — MaxTokens and the host's
        // invoke timeout are the real cost ceilings. The turn budget is deliberately
        // generous runaway insurance: a run killed on max_turns bills its full spend and
        // delivers nothing, so a cap that can fire on legitimate work (MaxTurns: 1 killed
        // multi-turn thinking generations) is strictly worse than finishing the task.
        var scratch = Path.Combine(_basePath, "utility-scratch");
        Directory.CreateDirectory(scratch);

        var request = new AgentRequest(
            SystemPrompt: req.System ?? "You are a helpful assistant.",
            UserMessage: req.UserMessage,
            WorktreePath: scratch,
            Provider: ParseProvider(req.Provider),
            Model: req.Model,
            MaxTokens: req.MaxTokens,
            MaxTurns: 16,
            Phase: AgentPhase.Utility,
            Workspace: workspaceName);

        // One retry on failure: transient drops ("Connection closed mid-response", CLI
        // exit 1) otherwise kill a fully-billed generation and surface as a 500. Provider
        // blockers already returned above, so a retry here never masks a misconfiguration.
        //
        // Not on the output-token cap, though: the same prompt under the same cap dies the
        // same way, and a second dead generation only pushes the host's 15-minute op
        // ceiling further out — the host falls back to its own key the moment we answer.
        var result = await RunUtilityAsync(request);
        if (result.Error is not null && !ShouldRetryUtility(result.Error))
        {
            logger.LogWarning(
                "Utility run hit the CLI output-token cap ({Error}) — not retrying, the host falls back",
                result.Error);
            return result;
        }

        if (result.Error is not null)
        {
            logger.LogWarning("Utility run failed ({Error}) — retrying once", result.Error);
            result = await RunUtilityAsync(request);
        }

        return result;
    }

    /// <summary>The one-retry policy for a failed utility generation: everything but the
    /// CLI's output-token cap, which fails identically on every attempt.</summary>
    internal static bool ShouldRetryUtility(string? error)
        => error is not null && !ClaudeCliErrors.IsOutputTokenCap(error);

    private Task<RunnerUtilityResult> RunUtilityAsync(AgentRequest request)
        => CollectUtilityResultAsync(invoker.InvokeAsync(request));

    /// <summary>Folds an invoker's chunk stream into the utility op's answer.</summary>
    internal static async Task<RunnerUtilityResult> CollectUtilityResultAsync(
        IAsyncEnumerable<AgentChunk> chunks)
    {
        var text = new System.Text.StringBuilder();
        int inputTokens = 0, outputTokens = 0, cacheRead = 0, cacheWrite = 0;
        string? error = null;

        try
        {
            await foreach (var chunk in chunks)
            {
                switch (chunk.Type)
                {
                    case AgentChunkType.Text:
                        text.Append(chunk.Content);
                        break;
                    case AgentChunkType.Error:
                        // First error wins. The CLI reports the real cause in its result
                        // message ("API Error: … exceeded the N output token maximum") and
                        // THEN exits 1, which the invoker reports as a second, generic
                        // "Agent process failed" error — keeping the last one would hide
                        // the cause from the retry decision above and from the host's log.
                        error ??= chunk.Content;
                        break;
                    case AgentChunkType.Done:
                        inputTokens = chunk.PromptTokens;
                        outputTokens = chunk.CompletionTokens;
                        cacheRead = chunk.CacheReadTokens;
                        cacheWrite = chunk.CacheWriteTokens;
                        // The Done chunk's content is the final result text when present.
                        if (text.Length == 0 && !string.IsNullOrEmpty(chunk.Content))
                            text.Append(chunk.Content);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        return new RunnerUtilityResult(
            text.Length > 0 ? text.ToString() : null,
            inputTokens, outputTokens, cacheRead, cacheWrite, error);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>Provider travels as the AiProvider enum name; unknown/empty (including a
    /// pre-provider host) falls back to Anthropic — today's behavior.</summary>
    private static AiProvider ParseProvider(string? provider)
        => Enum.TryParse<AiProvider>(provider, ignoreCase: true, out var parsed)
            ? parsed
            : AiProvider.Anthropic;

    /// <summary>Fresh credential check for the request's provider; null when runnable.
    /// <see cref="ProviderDetection.Detect"/> already layers the run-time evidence over the
    /// probe, so a provider whose last run failed to authenticate arrives here as
    /// Authenticated=false with that failure (time + remedy) in Detail — the message below
    /// quotes it, and no further run is spent until the user signs back in.
    /// Also where the guard policy holds on this machine: a CLI Orkka cannot guard arrives as
    /// not ready unless the developer opted in here (ProviderDetection.ApplyGuardPolicy), so
    /// nothing the host sends can start one.</summary>
    private string? ProviderBlocker(string? provider)
    {
        var cliKey = RunnerProviders.FromAiProviderName(provider);
        var status = detection.Detect(cliKey, workspaceName);
        if (status.Authenticated)
            return null;

        // Detection may carry a provider-specific remedy (Ollama: server down, codex CLI
        // missing, no models pulled) that the generic installed/logged-in wording can't say.
        var problem = !string.IsNullOrWhiteSpace(status.Detail)
            ? status.Detail
            : status.CliFound
                ? $"the {cliKey} CLI is not logged in on this machine — run `{ProviderDetection.LoginCommand(cliKey)}` (or use the Connect button in the runner status panel)."
                : $"the {cliKey} CLI is not installed on this machine.";
        logger.LogWarning("Refusing {Provider} run: {Problem}", cliKey, problem);
        return $"Cannot run this agent: {problem}";
    }

    /// <summary>The chat workspace folder. A task's Discuss workspace lives OUTSIDE the project
    /// chat workspace (whose root is the project agent's Cwd — a nested folder would put another
    /// branch's copy of every repo in front of it).</summary>
    internal string ChatWorkspacePath(string orgId, string? projectId, string? taskId = null)
        => string.IsNullOrEmpty(projectId)
            ? Path.Combine(_basePath, orgId, "chat-workspace")
            : string.IsNullOrEmpty(taskId)
                ? Path.Combine(_basePath, orgId, "chat-workspace", projectId)
                : Path.Combine(_basePath, orgId, "task-chat-workspace", taskId);

    /// <summary>A ticket imported from a tracker: its attachments, cached on this machine by
    /// the poller, go into <c>.orkka/context/attachments/</c> before the agent starts (the
    /// clone's info/exclude keeps <c>.orkka/</c> out of every commit). Never fails the run —
    /// an agent without the screenshots is still an agent with the ticket.</summary>
    internal void CopyTicketAttachments(TrackerTicketRef? ticket, string worktreePath)
    {
        if (ticket is null || ticketCache is null || !Directory.Exists(worktreePath))
            return;
        try
        {
            if (ticketCache.CopyToWorktree(ticket.ConnectionId, ticket.TicketKey, worktreePath) is { } copied)
                logger.LogInformation("Copied {Count} attachment(s) of {TicketKey} into the worktree", copied, ticket.TicketKey);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Could not copy the attachments of {TicketKey} into the worktree: {Error}",
                ticket.TicketKey, ex.Message);
        }
    }

    private bool TryResolveWorktree(string token, out string path, out string error)
    {
        if (RunnerTokens.TryParseWorktree(token, out var orgId, out var repoId, out var taskId))
        {
            path = git.GetWorktreePath(Ulid.Parse(orgId), Ulid.Parse(repoId), Ulid.Parse(taskId));
            error = "";
            return true;
        }

        path = "";
        error = $"Unrecognized worktree token: {token}";
        return false;
    }

    private static async IAsyncEnumerable<RunnerChunk> MapChunks(
        IAsyncEnumerable<AgentChunk> source, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var raw in source.WithCancellation(ct))
        {
            // Tool results never leave the machine; tool calls leave without file bodies.
            if (RunnerChunkPrivacy.ForHost(raw) is not { } chunk)
                continue;

            yield return new RunnerChunk(
                chunk.Type.ToString(),
                chunk.Content,
                chunk.PromptTokens,
                chunk.CompletionTokens,
                chunk.CacheReadTokens,
                chunk.CacheWriteTokens,
                chunk.SessionId,
                chunk.VendorCostUsd);
        }
    }

    private static async IAsyncEnumerable<RunnerChunk> ErrorStream(
        string message, [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new RunnerChunk("Error", message);
        yield return new RunnerChunk("Done", "");
        await Task.CompletedTask;
    }

    /// <summary>Every git command in this class: the engine, with the runner's two-minute
    /// default budget and the developer's own credentials.</summary>
    private static async Task<RunnerOpResult> RunGitAsync(
        string workingDirectory, string arguments, TimeSpan? timeout = null)
    {
        var result = await Git.RunAsync(new Orkka.Git.GitCommand(
            workingDirectory, Orkka.Git.GitArgumentLine.Split(arguments),
            Timeout: timeout ?? TimeSpan.FromMinutes(2)),
            CancellationToken.None);
        return result.Succeeded ? RunnerOpResult.Ok(result.Stdout) : RunnerOpResult.Fail(result.Describe());
    }

    /// <summary>Runs a CLI non-interactively with the same stdin-closed + timed-kill
    /// discipline as git (two minutes unless the caller knows better). Returns null when the
    /// executable itself cannot be started (not installed / not on PATH) so callers can give
    /// a tool-specific remedy.</summary>
    private static async Task<RunnerOpResult?> RunProcessAsync(
        string fileName, string workingDirectory, string arguments, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
        if (process is null)
            return null;

        using var _ = process;
        process.StandardInput.Close();

        // Hard cap: a process that outlives this is stuck on something interactive
        // (credential dialog, LFS endpoint) — kill the whole tree and surface a real
        // error instead of hanging an agent run.
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        var limit = timeout ?? TimeSpan.FromMinutes(2);
        using var cts = new CancellationTokenSource(limit);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return RunnerOpResult.Fail(
                $"{fileName} {arguments.Split(' ')[0]} timed out after {limit.TotalMinutes:0.#} minutes — " +
                "the operation may require credentials or network access that is unavailable " +
                "non-interactively.");
        }

        var output = await outputTask;
        var stderr = await stderrTask;

        return process.ExitCode == 0
            ? RunnerOpResult.Ok(output)
            : RunnerOpResult.Fail(string.IsNullOrWhiteSpace(stderr) ? $"{fileName} exited {process.ExitCode}" : stderr.Trim());
    }
}

/// <summary>Runner-side audit sink: agent tool use goes to the console (the user IS the
/// auditor on their own machine); the host keeps its own DB audit via execution events.</summary>
public sealed class ConsoleAuditLog(ILogger<ConsoleAuditLog> logger) : IAgentAuditLog
{
    public Task RecordAsync(AgentAuditRecord record, CancellationToken ct = default)
    {
        logger.LogInformation("[Audit] {EventType}: {Summary}", record.EventType, record.Summary);
        return Task.CompletedTask;
    }
}
