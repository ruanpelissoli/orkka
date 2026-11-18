namespace Orkka.Tests.Worker;

using Orkka.Modules.Execution.Git;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// The diff agents review must be spent on source, not lockfiles: a fresh Next.js app's
/// package-lock.json is ~8,600 lines, sorts second alphabetically, and used to eat the whole
/// review budget — the reviewer saw the lockfile and not one source file (FAN-9). Lockfiles
/// are excluded from the patch and named at the top instead.
/// </summary>
public sealed class GitWorkspaceDiffExclusionTests : IDisposable
{
    private readonly string _basePath;
    private readonly string _originPath;
    private readonly string _barePath;
    private readonly string _worktreePath;
    private readonly Ulid _orgId = Ulid.NewUlid();
    private readonly Ulid _repoId = Ulid.NewUlid();
    private readonly Ulid _taskId = Ulid.NewUlid();
    private readonly GitWorkspaceManager _manager;

    private const string Identity = "-c user.name=Orkka -c user.email=orkka@localhost";

    public GitWorkspaceDiffExclusionTests()
    {
        _basePath = Path.Combine(Path.GetTempPath(), $"orkka-diff-excl-tests-{Ulid.NewUlid()}");
        _originPath = Path.Combine(_basePath, "origin.git");

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Worker:RepoBasePath"] = Path.Combine(_basePath, "cache"),
            })
            .Build();

        _manager = new GitWorkspaceManager(config, Mock.Of<ILogger<GitWorkspaceManager>>());
        _barePath = _manager.GetBareRepoPath(_orgId, _repoId);
        _worktreePath = _manager.GetWorktreePath(_orgId, _repoId, _taskId);
    }

    /// <summary>origin/main with one commit, the bare mirror, and a task worktree whose
    /// single commit adds a huge lockfile, a nested lockfile, a source map, and one source file.</summary>
    private async Task SetupAsync()
    {
        Directory.CreateDirectory(_originPath);
        await RunGit(_originPath, "init --bare --initial-branch=main");

        var authorPath = Path.Combine(_basePath, "author");
        Directory.CreateDirectory(authorPath);
        await RunGit(_basePath, $"clone \"{_originPath}\" author");
        await RunGit(authorPath, "checkout -b main");
        await File.WriteAllTextAsync(Path.Combine(authorPath, "README.md"), "hello\n");
        await RunGit(authorPath, "add -A");
        await RunGit(authorPath, $"{Identity} commit -m \"init\"");
        await RunGit(authorPath, "push -u origin main");

        Directory.CreateDirectory(Path.GetDirectoryName(_barePath)!);
        await RunGit(Path.GetDirectoryName(_barePath)!, $"clone --bare \"{_originPath}\" bare");
        await RunGit(_barePath, "config remote.origin.fetch \"+refs/heads/*:refs/remotes/origin/*\"");
        await RunGit(_barePath, "fetch origin");

        Directory.CreateDirectory(Path.GetDirectoryName(_worktreePath)!);
        await RunGit(_barePath, $"worktree add \"{_worktreePath}\" -b orkka/task origin/main");

        // A lockfile big enough to blow a small budget on its own, plus the things that matter.
        var lockfile = string.Join("\n", Enumerable.Range(0, 2000).Select(i => $"    \"dep-{i}\": \"1.0.{i}\","));
        await File.WriteAllTextAsync(Path.Combine(_worktreePath, "package-lock.json"), "{\n" + lockfile + "\n}\n");
        Directory.CreateDirectory(Path.Combine(_worktreePath, "packages", "web"));
        await File.WriteAllTextAsync(Path.Combine(_worktreePath, "packages", "web", "pnpm-lock.yaml"), lockfile + "\n");
        await File.WriteAllTextAsync(Path.Combine(_worktreePath, "app.js.map"), "{\"mappings\":\"AAAA\"}\n");
        Directory.CreateDirectory(Path.Combine(_worktreePath, "src"));
        await File.WriteAllTextAsync(Path.Combine(_worktreePath, "src", "auth.ts"), "export const secret = hash(password);\n");
        await RunGit(_worktreePath, "add -A");
        await RunGit(_worktreePath, $"{Identity} commit -m \"task work\"");
    }

    [Fact]
    public async Task GetDiff_FromWorktree_ExcludesLockfiles_AndKeepsSourceWithinBudget()
    {
        await SetupAsync();

        // A budget far smaller than the lockfile alone: before the exclusion, src/auth.ts
        // never made it into the patch.
        var result = await _manager.GetDiffAsync(_orgId, _repoId, _taskId, "main", maxChars: 4_000);

        result.IsSuccess.Should().BeTrue(result.Error);
        var diff = result.Value!;
        diff.Should().Contain("src/auth.ts").And.Contain("hash(password)");
        diff.Should().NotContain("dep-1999", "the lockfile body must not be in the patch");
        diff.Should().NotContain("diff --git a/package-lock.json");
        diff.Should().NotContain("diff --git a/packages/web/pnpm-lock.yaml", "nested lockfiles are excluded too");
        diff.Should().NotContain("\"mappings\"", "source maps are generated output");
        diff.Should().StartWith("# Not shown (generated/lock files, 3): ", "excluded files are still named");
        diff.Should().Contain("package-lock.json").And.Contain("packages/web/pnpm-lock.yaml").And.Contain("app.js.map");
    }

    [Fact]
    public async Task GetDiff_FromBareMirror_WhenWorktreeIsGone_ExcludesLockfilesToo()
    {
        await SetupAsync();
        await RunGit(_worktreePath, "push origin orkka/task");
        // The FAN-9 shape: the worktree was cleaned up under the reviewer; the bare fallback serves the diff.
        await RunGit(_barePath, $"worktree remove --force \"{_worktreePath}\"");
        Directory.Exists(_worktreePath).Should().BeFalse();

        var result = await _manager.GetDiffAsync(
            _orgId, _repoId, _taskId, "main", maxChars: 4_000, headBranch: "orkka/task");

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.Should().Contain("src/auth.ts");
        result.Value.Should().NotContain("dep-1999");
        result.Value.Should().StartWith("# Not shown (generated/lock files, 3): ");
    }

    [Theory]
    [InlineData("package-lock.json", true)]
    [InlineData("apps/web/yarn.lock", true)]
    [InlineData("dist/bundle.min.js", true)]
    [InlineData("src/lock-screen.ts", false)]
    [InlineData("docs/locks.md", false)]
    [InlineData("Cargo.toml", false)]
    public void IsDiffExcluded_MatchesOnFileNameOnly(string path, bool excluded)
        => GitWorkspaceManager.IsDiffExcluded(path).Should().Be(excluded);

    public void Dispose()
    {
        if (!Directory.Exists(_basePath))
            return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(_basePath, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_basePath, recursive: true);
        }
        catch (IOException)
        {
            // A git process may still hold a handle; the temp folder is disposable either way.
        }
    }

    private static async Task RunGit(string workingDirectory, string arguments)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = System.Diagnostics.Process.Start(psi)!;
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new Exception($"Git failed ({arguments}): {error}");
    }
}
