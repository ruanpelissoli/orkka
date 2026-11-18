namespace Orkka.Tests.Services;

using Orkka.LocalRunner;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Execution.Agents;
using Orkka.Modules.SourceControl.Contracts;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// Exercises InitRepoInPlace — the "link a plain folder" flow that turns an existing
/// directory into a git repository (init + initial commit) so it can be linked without
/// the user touching a terminal.
/// </summary>
[Collection(Orkka.Tests.GitEngineStaticsCollection.Name)]
public sealed class RunnerOperationsInitInPlaceTests : IDisposable
{
    private readonly string _basePath;
    private readonly RunnerOperations _ops;

    public RunnerOperationsInitInPlaceTests()
    {
        _basePath = Path.Combine(Path.GetTempPath(), $"orkka-runner-initinplace-tests-{Ulid.NewUlid()}");
        Directory.CreateDirectory(_basePath);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Worker:RepoBasePath"] = Path.Combine(_basePath, "cache"),
                // Keeps the pre-import copies inside the test's own temp tree instead of
                // the real ~/.orkka/imports on whoever's machine runs the suite.
                ["Runner:ImportsPath"] = Path.Combine(_basePath, "imports"),
            })
            .Build();

        _ops = new RunnerOperations(
            Mock.Of<IGitWorkspaceManager>(),
            Mock.Of<IAiAgentInvoker>(),
            new ProviderDetection(Mock.Of<ILogger<ProviderDetection>>()),
            config,
            new Orkka.LocalRunner.RunnerHostLink(),
            Mock.Of<ILogger<RunnerOperations>>());
    }

    public void Dispose()
    {
        if (!Directory.Exists(_basePath))
            return;
        foreach (var file in Directory.EnumerateFiles(_basePath, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_basePath, recursive: true);
    }

    private string CreateFolder(string name)
    {
        var path = Path.Combine(_basePath, name);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public async Task InitInPlace_MissingDirectory_Fails()
    {
        var result = await _ops.InitRepoInPlaceAsync(
            new InitRepoInPlaceRequest(Path.Combine(_basePath, "does-not-exist")));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("does not exist");
    }

    [Fact]
    public async Task InitInPlace_PlainFolder_BecomesLinkableRepo()
    {
        var path = CreateFolder("plain-folder");
        await File.WriteAllTextAsync(Path.Combine(path, "app.js"), "console.log('hi');\n");

        var result = await _ops.InitRepoInPlaceAsync(new InitRepoInPlaceRequest(path));

        result.Success.Should().BeTrue();
        result.DefaultBranch.Should().Be("main");

        // The whole point: validation must now pass so the link flow can proceed.
        var info = await _ops.ValidateRepoPathAsync(new ValidateRepoPathRequest(path));
        info.IsGitRepo.Should().BeTrue();
        info.DefaultBranch.Should().Be("main");
    }

    [Fact]
    public async Task InitInPlace_PlainFolder_CommitsExistingFiles()
    {
        var path = CreateFolder("with-files");
        await File.WriteAllTextAsync(Path.Combine(path, "index.html"), "<html></html>\n");

        var result = await _ops.InitRepoInPlaceAsync(new InitRepoInPlaceRequest(path));

        result.Success.Should().BeTrue();
        var tracked = await RunGit(path, "ls-tree -r --name-only HEAD");
        tracked.Should().Contain("index.html");
    }

    [Fact]
    public async Task InitInPlace_NoGitignore_WritesDefaultOne()
    {
        var path = CreateFolder("needs-gitignore");
        Directory.CreateDirectory(Path.Combine(path, "node_modules", "leftpad"));
        await File.WriteAllTextAsync(Path.Combine(path, "node_modules", "leftpad", "index.js"), "x");
        await File.WriteAllTextAsync(Path.Combine(path, ".env"), "SECRET=1\n");
        await File.WriteAllTextAsync(Path.Combine(path, "app.js"), "x");

        var result = await _ops.InitRepoInPlaceAsync(new InitRepoInPlaceRequest(path));

        result.Success.Should().BeTrue();
        var tracked = await RunGit(path, "ls-tree -r --name-only HEAD");
        tracked.Should().Contain("app.js");
        tracked.Should().NotContain("node_modules");
        tracked.Should().NotContain(".env\n");
    }

    [Fact]
    public async Task InitInPlace_ExistingGitignore_IsLeftAlone()
    {
        var path = CreateFolder("has-gitignore");
        await File.WriteAllTextAsync(Path.Combine(path, ".gitignore"), "*.log\n");

        var result = await _ops.InitRepoInPlaceAsync(new InitRepoInPlaceRequest(path));

        result.Success.Should().BeTrue();
        (await File.ReadAllTextAsync(Path.Combine(path, ".gitignore"))).Should().Be("*.log\n");
    }

    [Fact]
    public async Task InitInPlace_EmptyFolder_StillGetsInitialCommit()
    {
        var path = CreateFolder("empty-folder");

        var result = await _ops.InitRepoInPlaceAsync(new InitRepoInPlaceRequest(path));

        result.Success.Should().BeTrue();
        var info = await _ops.ValidateRepoPathAsync(new ValidateRepoPathRequest(path));
        info.IsGitRepo.Should().BeTrue();
    }

    [Fact]
    public async Task InitInPlace_RepoWithoutCommits_GetsInitialCommit()
    {
        var path = CreateFolder("no-commits");
        await RunGit(path, "init -b main");
        await File.WriteAllTextAsync(Path.Combine(path, "README.md"), "# Test\n");

        var result = await _ops.InitRepoInPlaceAsync(new InitRepoInPlaceRequest(path));

        result.Success.Should().BeTrue();
        var info = await _ops.ValidateRepoPathAsync(new ValidateRepoPathRequest(path));
        info.IsGitRepo.Should().BeTrue();
    }

    [Fact]
    public async Task InitInPlace_RepoWithCommits_IsANoOp()
    {
        var path = CreateFolder("already-a-repo");
        await RunGit(path, "init -b trunk");
        await File.WriteAllTextAsync(Path.Combine(path, "README.md"), "# Test\n");
        await RunGit(path, "add -A");
        await RunGit(path, "-c user.name=Orkka -c user.email=orkka@localhost commit -m \"init\"");
        var shaBefore = await RunGit(path, "rev-parse HEAD");

        var result = await _ops.InitRepoInPlaceAsync(new InitRepoInPlaceRequest(path));

        result.Success.Should().BeTrue();
        result.DefaultBranch.Should().Be("trunk");
        (await RunGit(path, "rev-parse HEAD")).Should().Be(shaBefore);
    }

    [Fact]
    public async Task InitInPlace_FolderTrackedByAnEnclosingRepo_IsRefused()
    {
        // The folder picker reaches these constantly: Documents under git, a subfolder of a
        // monorepo. Initialising here would make the parent see every tracked file deleted
        // and replaced by a gitlink.
        var parent = CreateFolder("enclosing-repo");
        var child = Path.Combine(parent, "app");
        Directory.CreateDirectory(child);
        await File.WriteAllTextAsync(Path.Combine(child, "app.js"), "x");
        await RunGit(parent, "init -b main");
        await RunGit(parent, "add -A");
        await RunGit(parent, "-c user.name=Orkka -c user.email=orkka@localhost commit -m \"init\"");

        var result = await _ops.InitRepoInPlaceAsync(new InitRepoInPlaceRequest(child));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("already part of the git repository");
        Directory.Exists(Path.Combine(child, ".git")).Should().BeFalse();

        // And the parent is untouched — nothing staged, nothing deleted.
        (await RunGit(parent, "status --porcelain")).Trim().Should().BeEmpty();
    }

    [Fact]
    public async Task InitInPlace_UntrackedFolderInsideARepo_GetsItsOwnRepo()
    {
        // Same shape, but the parent never tracked these files (it ignores them, or they were
        // just dropped in). Nesting is harmless here, and refusing would strand anyone whose
        // home directory happens to be a git repository.
        var parent = CreateFolder("ignoring-repo");
        await RunGit(parent, "init -b main");
        await File.WriteAllTextAsync(Path.Combine(parent, ".gitignore"), "app/\n");
        await RunGit(parent, "add .gitignore");
        await RunGit(parent, "-c user.name=Orkka -c user.email=orkka@localhost commit -m \"init\"");

        var child = Path.Combine(parent, "app");
        Directory.CreateDirectory(child);
        await File.WriteAllTextAsync(Path.Combine(child, "app.js"), "x");

        var result = await _ops.InitRepoInPlaceAsync(new InitRepoInPlaceRequest(child));

        result.Success.Should().BeTrue();
        Directory.Exists(Path.Combine(child, ".git")).Should().BeTrue();
    }

    [Fact]
    public async Task InitInPlace_ExistingGitignoreMissingHeavyFolders_IsAppendedToNotRewritten()
    {
        var path = CreateFolder("weak-gitignore");
        await File.WriteAllTextAsync(Path.Combine(path, ".gitignore"), "*.log\n");
        Directory.CreateDirectory(Path.Combine(path, "node_modules", "leftpad"));
        await File.WriteAllTextAsync(Path.Combine(path, "node_modules", "leftpad", "index.js"), "x");
        await File.WriteAllTextAsync(Path.Combine(path, ".env"), "SECRET=1\n");
        await File.WriteAllTextAsync(Path.Combine(path, "app.js"), "x");

        var result = await _ops.InitRepoInPlaceAsync(new InitRepoInPlaceRequest(path));

        result.Success.Should().BeTrue();
        var gitignore = await File.ReadAllTextAsync(Path.Combine(path, ".gitignore"));
        gitignore.Should().StartWith("*.log\n", "the user's own rules are never rewritten");
        gitignore.Should().Contain("node_modules/").And.Contain(".env");

        var tracked = await RunGit(path, "ls-tree -r --name-only HEAD");
        tracked.Should().Contain("app.js");
        tracked.Should().NotContain("node_modules");

        // The files themselves are still on disk — ignored, never removed.
        Directory.Exists(Path.Combine(path, "node_modules")).Should().BeTrue();
        File.Exists(Path.Combine(path, ".env")).Should().BeTrue();
    }

    [Fact]
    public async Task InitInPlace_TagsTheImportedStateAsBaseline()
    {
        var path = CreateFolder("baseline-tag");
        await File.WriteAllTextAsync(Path.Combine(path, "app.js"), "x");

        var result = await _ops.InitRepoInPlaceAsync(new InitRepoInPlaceRequest(path));

        result.Success.Should().BeTrue();
        (await RunGit(path, "tag -l")).Should().Contain("orkka-baseline");
        (await RunGit(path, "rev-parse orkka-baseline^{commit}")).Trim()
            .Should().Be((await RunGit(path, "rev-parse HEAD")).Trim());
    }

    [Fact]
    public async Task InitInPlace_BuildFolderWithNoBuildSystem_IsCommitted()
    {
        // Nothing here regenerates a folder called "build", so it is as likely to BE the app
        // as to be its output. Ignoring it would hide the code from the agents and leave it
        // with no commit to restore from.
        var path = CreateFolder("handmade-app");
        Directory.CreateDirectory(Path.Combine(path, "build"));
        await File.WriteAllTextAsync(Path.Combine(path, "build", "app.js"), "console.log(1);\n");
        await File.WriteAllTextAsync(Path.Combine(path, "index.html"), "<html></html>\n");

        var result = await _ops.InitRepoInPlaceAsync(new InitRepoInPlaceRequest(path));

        result.Success.Should().BeTrue();
        (await RunGit(path, "ls-tree -r --name-only HEAD")).Should().Contain("build/app.js");
    }

    [Fact]
    public async Task InitInPlace_BuildFolderWithABuildSystem_IsExcluded()
    {
        var path = CreateFolder("built-app");
        await File.WriteAllTextAsync(Path.Combine(path, "package.json"), "{\"name\":\"app\"}\n");
        Directory.CreateDirectory(Path.Combine(path, "dist"));
        await File.WriteAllTextAsync(Path.Combine(path, "dist", "bundle.js"), "x");
        await File.WriteAllTextAsync(Path.Combine(path, "app.js"), "x");

        var result = await _ops.InitRepoInPlaceAsync(new InitRepoInPlaceRequest(path));

        result.Success.Should().BeTrue();
        var tracked = await RunGit(path, "ls-tree -r --name-only HEAD");
        tracked.Should().Contain("app.js").And.NotContain("dist/");
        result.ExcludedPaths.Should().Contain("dist/");
    }

    [Fact]
    public async Task InitInPlace_CopiesTheFolderBeforeTouchingIt()
    {
        var path = CreateFolder("worth-keeping");
        await File.WriteAllTextAsync(Path.Combine(path, "app.js"), "console.log('hi');\n");
        await File.WriteAllTextAsync(Path.Combine(path, ".env"), "SECRET=1\n");
        Directory.CreateDirectory(Path.Combine(path, "node_modules", "leftpad"));
        await File.WriteAllTextAsync(Path.Combine(path, "node_modules", "leftpad", "index.js"), "x");

        var result = await _ops.InitRepoInPlaceAsync(new InitRepoInPlaceRequest(path));

        result.Success.Should().BeTrue();
        result.SnapshotPath.Should().NotBeNull();

        // Everything the repository will not carry is still recoverable from the copy —
        // which is the whole point, since .env never reaches a commit.
        File.Exists(Path.Combine(result.SnapshotPath!, "app.js")).Should().BeTrue();
        File.Exists(Path.Combine(result.SnapshotPath!, ".env")).Should().BeTrue();

        // Dependencies are regenerable; copying them would double the disk for nothing.
        Directory.Exists(Path.Combine(result.SnapshotPath!, "node_modules")).Should().BeFalse();
    }

    [Fact]
    public async Task InitInPlace_ExistingRepoWithHistory_IsNotCopied()
    {
        // Its own history is already the restore point — a copy would be dead weight.
        var path = CreateFolder("already-versioned");
        await RunGit(path, "init -b main");
        await File.WriteAllTextAsync(Path.Combine(path, "README.md"), "# Test\n");
        await RunGit(path, "add -A");
        await RunGit(path, "-c user.name=Orkka -c user.email=orkka@localhost commit -m \"init\"");

        var result = await _ops.InitRepoInPlaceAsync(new InitRepoInPlaceRequest(path));

        result.Success.Should().BeTrue();
        result.SnapshotPath.Should().BeNull();
    }

    [Theory]
    [InlineData("Documents")]
    [InlineData("Desktop")]
    [InlineData("Downloads")]
    [InlineData("OneDrive - Contoso")]
    public void RejectUnsafeInitPath_ShellFolders_AreRefused(string folder)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        RunnerOperations.RejectUnsafeInitPath(Path.Combine(home, folder))
            .Should().NotBeNull();
    }

    [Fact]
    public void RejectUnsafeInitPath_HomeAndDriveRoot_AreRefused()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        RunnerOperations.RejectUnsafeInitPath(home).Should().NotBeNull();
        RunnerOperations.RejectUnsafeInitPath(Path.GetPathRoot(home)!).Should().NotBeNull();
    }

    [Fact]
    public void RejectUnsafeInitPath_AnOrdinaryProjectFolder_IsAllowed()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        RunnerOperations.RejectUnsafeInitPath(Path.Combine(home, "Documents", "accounting-app"))
            .Should().BeNull();
        RunnerOperations.RejectUnsafeInitPath(Path.Combine(home, "accounting-app"))
            .Should().BeNull();
    }

    private static async Task<string> RunGit(string workingDirectory, string arguments)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = System.Diagnostics.Process.Start(psi)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return output;
    }
}
