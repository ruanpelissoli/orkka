namespace Orkka.Tests.Worker;

using Orkka.Modules.Execution.Git;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

public sealed class GitWorkspaceManagerTests
{
    private readonly GitWorkspaceManager _manager;

    public GitWorkspaceManagerTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Worker:RepoBasePath"] = Path.Combine(Path.GetTempPath(), "orkka-test-repos")
            })
            .Build();

        _manager = new GitWorkspaceManager(config, Mock.Of<ILogger<GitWorkspaceManager>>());
    }

    [Fact]
    public void GetBareRepoPath_ReturnsCorrectPath()
    {
        var orgId = Ulid.NewUlid();
        var repoId = Ulid.NewUlid();

        var path = _manager.GetBareRepoPath(orgId, repoId);

        path.Should().Contain(orgId.ToString());
        path.Should().Contain(repoId.ToString());
        path.Should().EndWith("bare");
    }

    [Fact]
    public void GetWorktreePath_ReturnsCorrectPath()
    {
        var orgId = Ulid.NewUlid();
        var repoId = Ulid.NewUlid();
        var taskId = Ulid.NewUlid();

        var path = _manager.GetWorktreePath(orgId, repoId, taskId);

        path.Should().Contain(orgId.ToString());
        path.Should().Contain(repoId.ToString());
        path.Should().Contain($"task-{taskId}");
    }

    [Fact]
    public void GetWorktreePath_LocalSourceRepo_ReturnsSiblingWorktreesFolder()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "orkka-sibling-test-" + Ulid.NewUlid());
        var sourceRepo = Path.Combine(basePath, "code", "my-app");
        Directory.CreateDirectory(sourceRepo);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Worker:RepoBasePath"] = Path.Combine(basePath, "cache")
            })
            .Build();
        var manager = new GitWorkspaceManager(config, Mock.Of<ILogger<GitWorkspaceManager>>());

        var orgId = Ulid.NewUlid();
        var repoId = Ulid.NewUlid();
        var taskId = Ulid.NewUlid();
        var repoDir = Path.Combine(basePath, "cache", orgId.ToString(), repoId.ToString());
        Directory.CreateDirectory(repoDir);
        File.WriteAllText(Path.Combine(repoDir, "source-path"), sourceRepo);

        try
        {
            var path = manager.GetWorktreePath(orgId, repoId, taskId);

            path.Should().Be(Path.Combine(basePath, "code", "my-app-worktrees", $"task-{taskId}"));
        }
        finally
        {
            Directory.Delete(basePath, recursive: true);
        }
    }

    [Fact]
    public void GetWorktreePath_SourceRepoMoved_FallsBackToCacheLayout()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "orkka-moved-test-" + Ulid.NewUlid());
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Worker:RepoBasePath"] = basePath
            })
            .Build();
        var manager = new GitWorkspaceManager(config, Mock.Of<ILogger<GitWorkspaceManager>>());

        var orgId = Ulid.NewUlid();
        var repoId = Ulid.NewUlid();
        var taskId = Ulid.NewUlid();
        var repoDir = Path.Combine(basePath, orgId.ToString(), repoId.ToString());
        Directory.CreateDirectory(repoDir);
        File.WriteAllText(Path.Combine(repoDir, "source-path"),
            Path.Combine(basePath, "gone", "repo"));

        try
        {
            var path = manager.GetWorktreePath(orgId, repoId, taskId);

            path.Should().Be(Path.Combine(repoDir, "worktrees", $"task-{taskId}"));
        }
        finally
        {
            Directory.Delete(basePath, recursive: true);
        }
    }

    [Fact]
    public async Task CleanupStaleWorktreesAsync_SweepsExternalSiblingFolder()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "orkka-gc-ext-test-" + Ulid.NewUlid());
        var sourceRepo = Path.Combine(basePath, "code", "my-app");
        Directory.CreateDirectory(sourceRepo);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Worker:RepoBasePath"] = Path.Combine(basePath, "cache")
            })
            .Build();
        var manager = new GitWorkspaceManager(config, Mock.Of<ILogger<GitWorkspaceManager>>());

        var orgId = Ulid.NewUlid();
        var repoId = Ulid.NewUlid();
        var repoDir = Path.Combine(basePath, "cache", orgId.ToString(), repoId.ToString());
        Directory.CreateDirectory(repoDir);
        File.WriteAllText(Path.Combine(repoDir, "source-path"), sourceRepo);

        var stalePath = manager.GetWorktreePath(orgId, repoId, Ulid.NewUlid());
        var externalRoot = Path.GetDirectoryName(stalePath)!;
        externalRoot.Should().Be(Path.Combine(basePath, "code", "my-app-worktrees"));
        Directory.CreateDirectory(stalePath);
        // A non-Orkka folder inside the sibling root must survive the sweep.
        var unrelated = Path.Combine(externalRoot, "keep-me");
        Directory.CreateDirectory(unrelated);
        Directory.SetLastWriteTimeUtc(stalePath, DateTime.UtcNow.AddHours(-100));
        Directory.SetLastWriteTimeUtc(unrelated, DateTime.UtcNow.AddHours(-100));

        try
        {
            var removed = await manager.CleanupStaleWorktreesAsync(TimeSpan.FromHours(72));

            removed.Should().Be(1);
            Directory.Exists(stalePath).Should().BeFalse();
            Directory.Exists(unrelated).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(basePath, recursive: true);
        }
    }

    [Fact]
    public async Task GetWorktreePath_FriendlyNameRecorded_UsesFriendlyFolder()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "orkka-name-test-" + Ulid.NewUlid());
        var manager = ManagerFor(basePath);
        var orgId = Ulid.NewUlid();
        var repoId = Ulid.NewUlid();
        var taskId = Ulid.NewUlid();

        try
        {
            // CreateWorktree records the name before failing on the missing bare repo.
            await manager.CreateWorktreeAsync(orgId, repoId, taskId, "orkka/BRW-7", "main", "BRW-7");

            var path = manager.GetWorktreePath(orgId, repoId, taskId);

            Path.GetFileName(path).Should().Be("BRW-7");
        }
        finally
        {
            if (Directory.Exists(basePath))
                Directory.Delete(basePath, recursive: true);
        }
    }

    [Fact]
    public async Task GetWorktreePath_LegacyFolderExists_KeepsLegacyName()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "orkka-legacy-test-" + Ulid.NewUlid());
        var manager = ManagerFor(basePath);
        var orgId = Ulid.NewUlid();
        var repoId = Ulid.NewUlid();
        var taskId = Ulid.NewUlid();
        var legacyPath = Path.Combine(basePath, orgId.ToString(), repoId.ToString(), "worktrees", $"task-{taskId}");
        Directory.CreateDirectory(legacyPath);

        try
        {
            await manager.CreateWorktreeAsync(orgId, repoId, taskId, "orkka/BRW-7", "main", "BRW-7");

            manager.GetWorktreePath(orgId, repoId, taskId).Should().Be(legacyPath);
        }
        finally
        {
            if (Directory.Exists(basePath))
                Directory.Delete(basePath, recursive: true);
        }
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("task-fake")]
    [InlineData("has space")]
    [InlineData("")]
    public async Task GetWorktreePath_InvalidFriendlyName_FallsBackToUlidFolder(string name)
    {
        var basePath = Path.Combine(Path.GetTempPath(), "orkka-badname-test-" + Ulid.NewUlid());
        var manager = ManagerFor(basePath);
        var orgId = Ulid.NewUlid();
        var repoId = Ulid.NewUlid();
        var taskId = Ulid.NewUlid();

        try
        {
            await manager.CreateWorktreeAsync(orgId, repoId, taskId, "orkka/x", "main", name);

            Path.GetFileName(manager.GetWorktreePath(orgId, repoId, taskId)).Should().Be($"task-{taskId}");
        }
        finally
        {
            if (Directory.Exists(basePath))
                Directory.Delete(basePath, recursive: true);
        }
    }

    [Fact]
    public async Task CleanupStaleWorktreesAsync_SweepsFriendlyNamedWorktrees_KeepsUnrelated()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "orkka-gc-name-test-" + Ulid.NewUlid());
        var manager = ManagerFor(basePath);
        var orgId = Ulid.NewUlid();
        var repoId = Ulid.NewUlid();
        var taskId = Ulid.NewUlid();

        await manager.CreateWorktreeAsync(orgId, repoId, taskId, "orkka/BRW-9", "main", "BRW-9");
        var stalePath = manager.GetWorktreePath(orgId, repoId, taskId);
        Path.GetFileName(stalePath).Should().Be("BRW-9");
        Directory.CreateDirectory(stalePath);
        var unrelated = Path.Combine(Path.GetDirectoryName(stalePath)!, "not-orkkas");
        Directory.CreateDirectory(unrelated);
        Directory.SetLastWriteTimeUtc(stalePath, DateTime.UtcNow.AddHours(-100));
        Directory.SetLastWriteTimeUtc(unrelated, DateTime.UtcNow.AddHours(-100));

        try
        {
            var removed = await manager.CleanupStaleWorktreesAsync(TimeSpan.FromHours(72));

            removed.Should().Be(1);
            Directory.Exists(stalePath).Should().BeFalse();
            Directory.Exists(unrelated).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(basePath))
                Directory.Delete(basePath, recursive: true);
        }
    }

    [Fact]
    public void ParseMergeTreeConflicts_ParsesFilesAndDetails()
    {
        const string output = """
            d9260cb341293e20982cad2aa44357f3e3862008
            .gitignore
            go.mod

            CONFLICT (modify/delete): .gitignore deleted in main and modified in orkka/BRW-7.
            CONFLICT (add/add): Merge conflict in go.mod
            """;

        var report = GitWorkspaceManager.ParseMergeTreeConflicts(output);

        report.HasConflicts.Should().BeTrue();
        report.Files.Should().HaveCount(2);
        report.Files[0].Path.Should().Be(".gitignore");
        report.Files[0].Detail.Should().Contain("modify/delete");
        report.Files[1].Path.Should().Be("go.mod");
        report.Files[1].Detail.Should().Contain("add/add");
        report.RawDetails.Should().Contain("CONFLICT");
    }

    [Fact]
    public void ParseMergeTreeConflicts_DeduplicatesRepeatedPaths()
    {
        const string output = """
            abc123
            src/app.ts
            src/app.ts

            CONFLICT (content): Merge conflict in src/app.ts
            """;

        var report = GitWorkspaceManager.ParseMergeTreeConflicts(output);

        report.Files.Should().ContainSingle(f => f.Path == "src/app.ts");
    }

    [Fact]
    public async Task GetDiffAsync_WorktreeGone_FallsBackToBareMirror()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "orkka-diff-fallback-" + Ulid.NewUlid());
        var srcRepo = Path.Combine(basePath, "src");
        Directory.CreateDirectory(srcRepo);

        try
        {
            // Real git: a source repo with main + a feature branch carrying one change.
            await GitAsync(srcRepo, "init -b main");
            await File.WriteAllTextAsync(Path.Combine(srcRepo, "a.txt"), "hello\n");
            await GitAsync(srcRepo, "add -A");
            await GitAsync(srcRepo, "-c user.email=t@t -c user.name=t commit -m init");
            await GitAsync(srcRepo, "checkout -b orkka/DIFF-1");
            await File.WriteAllTextAsync(Path.Combine(srcRepo, "a.txt"), "hello world\n");
            await GitAsync(srcRepo, "add -A");
            await GitAsync(srcRepo, "-c user.email=t@t -c user.name=t commit -m change");
            await GitAsync(srcRepo, "checkout main");

            var manager = ManagerFor(Path.Combine(basePath, "cache"));
            var orgId = Ulid.NewUlid();
            var repoId = Ulid.NewUlid();
            var clone = await manager.EnsureBareCloneAsync(orgId, repoId, srcRepo);
            clone.IsSuccess.Should().BeTrue(clone.Error);

            // No worktree was ever created for this task — the fallback must kick in.
            var diff = await manager.GetDiffAsync(
                orgId, repoId, Ulid.NewUlid(), "main", headBranch: "orkka/DIFF-1");

            diff.IsSuccess.Should().BeTrue(diff.Error);
            diff.Value.Should().Contain("hello world");

            // Without a head branch the old failure surfaces.
            var noFallback = await manager.GetDiffAsync(orgId, repoId, Ulid.NewUlid(), "main");
            noFallback.IsFailure.Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(basePath))
                try { Directory.Delete(basePath, recursive: true); } catch { /* git handles */ }
        }
    }

    [Fact]
    public async Task CleanupWorktreeAsync_FolderRemoved_DropsTheNameMarker()
    {
        // The complement of the locked case below. Keeping the marker is only correct while the
        // folder survives — once it is gone the marker must go too, or the names directory grows
        // without bound and the GC keeps recognising a folder that no longer exists.
        var basePath = Path.Combine(Path.GetTempPath(), "orkka-marker-ok-" + Ulid.NewUlid());
        var orgId = Ulid.NewUlid();
        var repoId = Ulid.NewUlid();
        var taskId = Ulid.NewUlid();

        var manager = ManagerFor(basePath);
        var repoDir = Path.Combine(basePath, orgId.ToString(), repoId.ToString());
        var markerDir = Path.Combine(repoDir, "worktree-names");
        Directory.CreateDirectory(markerDir);
        var marker = Path.Combine(markerDir, taskId.ToString());
        await File.WriteAllTextAsync(marker, "WAV-25");
        var worktreePath = Path.Combine(repoDir, "worktrees", "WAV-25");
        Directory.CreateDirectory(worktreePath);
        await File.WriteAllTextAsync(Path.Combine(worktreePath, "file.txt"), "done\n");

        try
        {
            await manager.CleanupWorktreeAsync(orgId, repoId, taskId);

            Directory.Exists(worktreePath).Should().BeFalse();
            File.Exists(marker).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(basePath))
                Directory.Delete(basePath, recursive: true);
        }
    }

    [WindowsOnlyFact("staging a locked worktree needs a file handle that blocks deletion; POSIX unlinks regardless")]
    public async Task CleanupWorktreeAsync_FolderSurvives_KeepsNameMarkerForTheGc()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "orkka-marker-" + Ulid.NewUlid());
        var orgId = Ulid.NewUlid();
        var repoId = Ulid.NewUlid();
        var taskId = Ulid.NewUlid();

        var manager = ManagerFor(basePath);
        var repoDir = Path.Combine(basePath, orgId.ToString(), repoId.ToString());
        var markerDir = Path.Combine(repoDir, "worktree-names");
        Directory.CreateDirectory(markerDir);
        await File.WriteAllTextAsync(Path.Combine(markerDir, taskId.ToString()), "WAV-25");
        var worktreePath = Path.Combine(repoDir, "worktrees", "WAV-25");
        Directory.CreateDirectory(worktreePath);

        // Hold the folder open so removal cannot free it — the locked-worktree case.
        var held = new FileStream(
            Path.Combine(worktreePath, "held.bin"), FileMode.Create, FileAccess.Write, FileShare.None);

        try
        {
            await manager.CleanupWorktreeAsync(orgId, repoId, taskId);

            // The marker is the only thing that lets the GC recognise a friendly-named
            // folder. Dropping it while the folder survives strands the folder forever.
            Directory.Exists(worktreePath).Should().BeTrue();
            File.Exists(Path.Combine(markerDir, taskId.ToString())).Should().BeTrue();
        }
        finally
        {
            await held.DisposeAsync();
            if (Directory.Exists(basePath))
                try { Directory.Delete(basePath, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task CreateWorktreeAsync_BranchNotOnRemote_TracksItsOwnBranchNotTheBase()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "orkka-upstream-" + Ulid.NewUlid());
        var srcRepo = Path.Combine(basePath, "src");
        Directory.CreateDirectory(srcRepo);

        try
        {
            await GitAsync(srcRepo, "init -b main");
            await File.WriteAllTextAsync(Path.Combine(srcRepo, "a.txt"), "hello\n");
            await GitAsync(srcRepo, "add -A");
            await GitAsync(srcRepo, "-c user.email=t@t -c user.name=t commit -m init");

            var manager = ManagerFor(Path.Combine(basePath, "cache"));
            var orgId = Ulid.NewUlid();
            var repoId = Ulid.NewUlid();
            var taskId = Ulid.NewUlid();
            var clone = await manager.EnsureBareCloneAsync(orgId, repoId, srcRepo);
            clone.IsSuccess.Should().BeTrue(clone.Error);

            var worktree = await manager.CreateWorktreeAsync(
                orgId, repoId, taskId, "orkka/UP-1", "main");
            worktree.IsSuccess.Should().BeTrue(worktree.Error);

            // The branch must point at its own (not yet pushed) remote counterpart. Inheriting
            // git's autoSetupMerge upstream — origin/main — is what made every finished task
            // read as "1 ahead, N behind" in editors long after its branch was pushed.
            var remote = await GitOutAsync(worktree.Value!, "config --get branch.orkka/UP-1.remote");
            var merge = await GitOutAsync(worktree.Value!, "config --get branch.orkka/UP-1.merge");

            remote.Should().Be("origin");
            merge.Should().Be("refs/heads/orkka/UP-1");
        }
        finally
        {
            if (Directory.Exists(basePath))
                try { Directory.Delete(basePath, recursive: true); } catch { /* git handles */ }
        }
    }

    /// <summary>A Story member's LIVE diff is its own change: against its review base, the
    /// earlier sub-task's commit on the shared branch is not shown (e2e, 2026-11-17).</summary>
    [Fact]
    public async Task GetWorkingTreeDiffAsync_WithAReviewBase_LeavesOutTheEarlierMembersWork()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "orkka-wt-story-" + Ulid.NewUlid());
        var srcRepo = Path.Combine(basePath, "src");
        Directory.CreateDirectory(srcRepo);

        try
        {
            await GitAsync(srcRepo, "init -b main");
            await File.WriteAllTextAsync(Path.Combine(srcRepo, "a.txt"), "hello\n");
            await GitAsync(srcRepo, "add -A");
            await GitAsync(srcRepo, "-c user.email=t@t -c user.name=t commit -m init");

            var manager = ManagerFor(Path.Combine(basePath, "cache"));
            var orgId = Ulid.NewUlid();
            var repoId = Ulid.NewUlid();
            var taskId = Ulid.NewUlid();
            (await manager.EnsureBareCloneAsync(orgId, repoId, srcRepo)).IsSuccess.Should().BeTrue();
            var created = await manager.CreateWorktreeAsync(orgId, repoId, taskId, "story/SHOP-20", "main", "SHOP-22");
            created.IsSuccess.Should().BeTrue(created.Error);
            var worktree = manager.GetWorktreePath(orgId, repoId, taskId);

            // The first sub-task's work, already on the shared branch.
            await File.WriteAllTextAsync(Path.Combine(worktree, "first.txt"), "first member\n");
            await GitAsync(worktree, "add -A");
            await GitAsync(worktree, "-c user.email=t@t -c user.name=t commit -m first");
            var reviewBase = await GitOutAsync(worktree, "rev-parse HEAD");

            // The second sub-task, mid-run.
            await File.WriteAllTextAsync(Path.Combine(worktree, "second.txt"), "second member\n");

            var own = await manager.GetWorkingTreeDiffAsync(orgId, repoId, taskId, "main", baseSha: reviewBase);
            own.IsSuccess.Should().BeTrue(own.Error);
            own.Value.Should().Contain("second.txt").And.NotContain("first.txt");

            var whole = await manager.GetWorkingTreeDiffAsync(orgId, repoId, taskId, "main");
            whole.Value.Should().Contain("first.txt", "without a review base it is the whole branch");
        }
        finally
        {
            if (Directory.Exists(basePath))
                try { Directory.Delete(basePath, recursive: true); } catch { /* git handles */ }
        }
    }

    [Fact]
    public async Task GetWorkingTreeDiffAsync_SeesUncommittedAndUntracked_LeavesTheIndexAlone()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "orkka-wt-diff-" + Ulid.NewUlid());
        var srcRepo = Path.Combine(basePath, "src");
        Directory.CreateDirectory(srcRepo);

        try
        {
            await GitAsync(srcRepo, "init -b main");
            await File.WriteAllTextAsync(Path.Combine(srcRepo, "a.txt"), "hello\n");
            await File.WriteAllTextAsync(Path.Combine(srcRepo, ".gitignore"), "ignored.log\n");
            await GitAsync(srcRepo, "add -A");
            await GitAsync(srcRepo, "-c user.email=t@t -c user.name=t commit -m init");

            var manager = ManagerFor(Path.Combine(basePath, "cache"));
            var orgId = Ulid.NewUlid();
            var repoId = Ulid.NewUlid();
            var taskId = Ulid.NewUlid();
            (await manager.EnsureBareCloneAsync(orgId, repoId, srcRepo)).IsSuccess.Should().BeTrue();
            var created = await manager.CreateWorktreeAsync(orgId, repoId, taskId, "orkka/WT-1", "main", "WT-1");
            created.IsSuccess.Should().BeTrue(created.Error);
            var worktree = manager.GetWorktreePath(orgId, repoId, taskId);

            // Nothing changed yet → empty diff, and the committed-only diff agrees.
            var empty = await manager.GetWorkingTreeDiffAsync(orgId, repoId, taskId, "main");
            empty.IsSuccess.Should().BeTrue(empty.Error);
            empty.Value.Should().BeEmpty();

            // An uncommitted edit, a brand-new untracked file, and an ignored file.
            await File.WriteAllTextAsync(Path.Combine(worktree, "a.txt"), "hello world\n");
            Directory.CreateDirectory(Path.Combine(worktree, "src"));
            await File.WriteAllTextAsync(Path.Combine(worktree, "src", "new.cs"), "class New {}\n");
            await File.WriteAllTextAsync(Path.Combine(worktree, "ignored.log"), "noise\n");

            var diff = await manager.GetWorkingTreeDiffAsync(orgId, repoId, taskId, "main");
            diff.IsSuccess.Should().BeTrue(diff.Error);
            diff.Value.Should().Contain("+hello world");
            diff.Value.Should().Contain("new file mode");
            diff.Value.Should().Contain("src/new.cs");
            diff.Value.Should().NotContain("ignored.log");

            // The committed-only diff still sees nothing: this is the whole point.
            var committed = await manager.GetDiffAsync(orgId, repoId, taskId, "main");
            committed.IsSuccess.Should().BeTrue(committed.Error);
            committed.Value.Should().BeEmpty();

            // And the worktree's real index was not touched — new.cs is still untracked, a.txt
            // still unstaged — so the agent's next `git status` reads exactly as before.
            var status = await GitOutAsync(worktree, "status --porcelain --untracked-files=all");
            status.Should().Contain("?? src/new.cs");
            status.Should().Contain("M a.txt");
            status.Should().NotContain("A  src/new.cs").And.NotContain("M  a.txt");
        }
        finally
        {
            if (Directory.Exists(basePath))
                try { Directory.Delete(basePath, recursive: true); } catch { /* git handles */ }
        }
    }

    private static async Task<string> GitOutAsync(string cwd, string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", args)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return stdout.Trim();
    }

    private static async Task GitAsync(string cwd, string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", args)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        await p.WaitForExitAsync();
        p.ExitCode.Should().Be(0, $"git {args} failed: {await p.StandardError.ReadToEndAsync()}");
    }

    private static GitWorkspaceManager ManagerFor(string basePath)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Worker:RepoBasePath"] = basePath
            })
            .Build();
        return new GitWorkspaceManager(config, Mock.Of<ILogger<GitWorkspaceManager>>());
    }

    [Fact]
    public async Task CreateWorktreeAsync_NoBareRepo_ReturnsFailure()
    {
        var orgId = Ulid.NewUlid();
        var repoId = Ulid.NewUlid();
        var taskId = Ulid.NewUlid();

        var result = await _manager.CreateWorktreeAsync(
            orgId, repoId, taskId, "feature-branch", "main");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Bare repo does not exist");
    }

    [Fact]
    public async Task CleanupStaleWorktreesAsync_RemovesOldWorktrees_KeepsFresh()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "orkka-gc-test-" + Ulid.NewUlid());
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Worker:RepoBasePath"] = basePath
            })
            .Build();
        var manager = new GitWorkspaceManager(config, Mock.Of<ILogger<GitWorkspaceManager>>());

        var orgId = Ulid.NewUlid();
        var repoId = Ulid.NewUlid();
        var stalePath = manager.GetWorktreePath(orgId, repoId, Ulid.NewUlid());
        var freshPath = manager.GetWorktreePath(orgId, repoId, Ulid.NewUlid());
        Directory.CreateDirectory(stalePath);
        Directory.CreateDirectory(freshPath);
        Directory.SetLastWriteTimeUtc(stalePath, DateTime.UtcNow.AddHours(-100));

        try
        {
            var removed = await manager.CleanupStaleWorktreesAsync(TimeSpan.FromHours(72));

            removed.Should().BeGreaterThanOrEqualTo(1);
            Directory.Exists(stalePath).Should().BeFalse();
            Directory.Exists(freshPath).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(basePath))
                Directory.Delete(basePath, recursive: true);
        }
    }

    [Fact]
    public async Task CreateWorktreeAsync_RepoOverSizeCap_ReturnsFailure()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "orkka-size-test-" + Ulid.NewUlid());
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Worker:RepoBasePath"] = basePath,
                ["Worker:MaxRepoSizeMb"] = "1",
            })
            .Build();
        var manager = new GitWorkspaceManager(config, Mock.Of<ILogger<GitWorkspaceManager>>());

        var orgId = Ulid.NewUlid();
        var repoId = Ulid.NewUlid();
        var barePath = manager.GetBareRepoPath(orgId, repoId);
        Directory.CreateDirectory(barePath);
        // 2 MB file — over the 1 MB cap.
        await File.WriteAllBytesAsync(Path.Combine(barePath, "big.pack"), new byte[2 * 1024 * 1024]);

        try
        {
            var result = await manager.CreateWorktreeAsync(orgId, repoId, Ulid.NewUlid(), "branch", "main");

            result.IsFailure.Should().BeTrue();
            result.Error.Should().Contain("exceeds");
        }
        finally
        {
            if (Directory.Exists(basePath))
                Directory.Delete(basePath, recursive: true);
        }
    }

    [Fact]
    public async Task RemoveRepoWorkspaceAsync_DeletesEntireRepoTree_AndEnumerationReflectsIt()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "orkka-wipe-test-" + Ulid.NewUlid());
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Worker:RepoBasePath"] = basePath
            })
            .Build();
        var manager = new GitWorkspaceManager(config, Mock.Of<ILogger<GitWorkspaceManager>>());

        var orgId = Ulid.NewUlid();
        var repoId = Ulid.NewUlid();
        var repoDir = Path.Combine(basePath, orgId.ToString(), repoId.ToString());
        // No "bare" dir so removal skips git and just deletes the tree.
        Directory.CreateDirectory(Path.Combine(repoDir, "worktrees", "task-" + Ulid.NewUlid()));

        try
        {
            manager.EnumerateRepoWorkspaces().Should().Contain((orgId, repoId));

            await manager.RemoveRepoWorkspaceAsync(orgId, repoId);

            Directory.Exists(repoDir).Should().BeFalse();
            manager.EnumerateRepoWorkspaces().Should().NotContain((orgId, repoId));
        }
        finally
        {
            if (Directory.Exists(basePath))
                Directory.Delete(basePath, recursive: true);
        }
    }
}
