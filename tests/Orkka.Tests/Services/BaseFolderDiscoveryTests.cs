namespace Orkka.Tests.Services;

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Orkka.Git;
using Orkka.LocalRunner;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Execution.Git;

/// <summary>
/// Base-folder projects on the developer's machine, against real git: what <c>DiscoverRepos</c>
/// finds (and leaves alone), a remote's first mirror seeded from the developer's checkout, and
/// task folders that never land inside the base folder.
/// </summary>
public sealed class BaseFolderDiscoveryTests : IDisposable
{
    private const string Identity = "-c user.name=Orkka -c user.email=orkka@localhost";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"orkka-base-folder-tests-{Ulid.NewUlid()}");
    private readonly GitProcess _git = new(NullLogger<GitProcess>.Instance);

    public BaseFolderDiscoveryTests() => Directory.CreateDirectory(_root);

    // ── DiscoverRepos ───────────────────────────────────────────────────

    [Fact]
    public async Task Monorepo_IsTheFolderItself_WithItsTopLevelAreas()
    {
        var mono = Path.Combine(_root, "shop");
        await InitRepoAsync(mono, origin: "https://github.com/acme/shop.git");
        Directory.CreateDirectory(Path.Combine(mono, "apps"));
        Directory.CreateDirectory(Path.Combine(mono, "services"));
        Directory.CreateDirectory(Path.Combine(mono, "node_modules"));
        Directory.CreateDirectory(Path.Combine(mono, ".github"));

        var result = await RepoDiscovery.DiscoverAsync(_git, mono);

        result.Error.Should().BeNull();
        result.Shape.Should().Be(DiscoveredShapes.Monorepo);
        var repo = result.Repos.Should().ContainSingle().Subject;
        repo.RelativePath.Should().Be("");
        repo.OriginUrl.Should().Be("https://github.com/acme/shop.git");
        repo.HasCommits.Should().BeTrue();
        result.Areas.Should().BeEquivalentTo(["apps", "services"]);
    }

    [Fact]
    public async Task FolderOfRepos_FindsReposToDepthTwo_SkippingDependencyFoldersAndSubmodules()
    {
        await InitRepoAsync(Path.Combine(_root, "api"), origin: "https://github.com/acme/api.git");
        await InitRepoAsync(Path.Combine(_root, "web"), origin: null);
        await InitRepoAsync(Path.Combine(_root, "services", "billing"), origin: "git@github.com:acme/billing.git");
        // Too deep, inside node_modules, inside a repo, hidden, and Orkka's own task folders.
        await InitRepoAsync(Path.Combine(_root, "a", "b", "too-deep"), origin: null);
        await InitRepoAsync(Path.Combine(_root, "node_modules", "left-pad"), origin: null);
        await InitRepoAsync(Path.Combine(_root, "api", "vendor", "submodule"), origin: null);
        await InitRepoAsync(Path.Combine(_root, ".cache", "hidden"), origin: null);
        await InitRepoAsync(Path.Combine(_root, "web-worktrees", "SHOP-1"), origin: null);

        var result = await RepoDiscovery.DiscoverAsync(_git, _root);

        result.Error.Should().BeNull();
        result.Shape.Should().Be(DiscoveredShapes.MultiRepo);
        result.Repos.Select(r => r.RelativePath).Should().BeEquivalentTo(["api", "services/billing", "web"]);
        result.Repos.Single(r => r.RelativePath == "web").OriginUrl.Should().BeNull();
        result.Repos.Single(r => r.RelativePath == "services/billing").OriginUrl
            .Should().Be("git@github.com:acme/billing.git");
    }

    [Fact]
    public async Task Origin_IsTheConfiguredUrl_NotItsInsteadOfRewrite()
    {
        // A developer whose git rewrites GitHub URLs (a mirror, a local path): the repository
        // is still the GitHub one — that is where its pull request goes.
        var api = Path.Combine(_root, "api");
        await InitRepoAsync(api, origin: "https://github.com/acme/api.git");
        var mirror = new Uri(Path.Combine(_root, "mirror") + Path.DirectorySeparatorChar).AbsoluteUri;
        await GitOut(api, $"config url.{mirror}.insteadOf https://github.com/acme/");
        (await GitOut(api, "remote get-url origin")).Should().StartWith("file:", "the rewrite is in effect");

        var result = await RepoDiscovery.DiscoverAsync(_git, _root);

        result.Repos.Single().OriginUrl.Should().Be("https://github.com/acme/api.git");
    }

    [Fact]
    public async Task Repo_ReportsBranchDirtyAndDescription_AndNothingIsWritten()
    {
        var api = Path.Combine(_root, "api");
        await InitRepoAsync(api, origin: null,
            readme: "# API\n\n[![build](https://x/badge.svg)](https://x)\n\nThe orders and billing HTTP API.\nServes the web app.\n\nMore text.");
        await File.WriteAllTextAsync(Path.Combine(api, "scratch.txt"), "uncommitted");
        var before = Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories).Count();

        var repo = (await RepoDiscovery.DiscoverAsync(_git, _root)).Repos.Single();

        repo.CurrentBranch.Should().Be("main");
        repo.DefaultBranch.Should().Be("main");
        repo.IsDirty.Should().BeTrue();
        repo.HasUnpushedCommits.Should().BeFalse();
        repo.Description.Should().Be("The orders and billing HTTP API. Serves the web app.");
        Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories).Count().Should().Be(before);
    }

    [Fact]
    public async Task OrkkaJsonDescription_WinsOverTheReadme()
    {
        var api = Path.Combine(_root, "api");
        await InitRepoAsync(api, origin: null, readme: "# API\n\nFrom the readme.",
            orkkaJson: """{ "description": "Orders API (from orkka.json)", "stack": "node" }""");

        var repo = (await RepoDiscovery.DiscoverAsync(_git, _root)).Repos.Single();

        repo.Description.Should().Be("Orders API (from orkka.json)");
        repo.Stack.Should().Be("node");
    }

    [Fact]
    public async Task UnpushedCommits_AreReported()
    {
        var remote = Path.Combine(_root, "remotes", "api.git");
        Directory.CreateDirectory(remote);
        await Git(remote, "init --bare --initial-branch=main");
        var api = Path.Combine(_root, "work", "api");
        await InitRepoAsync(api, origin: remote);
        await Git(api, "push -u origin main");
        await File.WriteAllTextAsync(Path.Combine(api, "b.txt"), "b");
        await Git(api, "add -A");
        await Git(api, $"{Identity} commit -m second");

        var repo = (await RepoDiscovery.DiscoverAsync(_git, Path.Combine(_root, "work"))).Repos.Single();

        repo.HasUnpushedCommits.Should().BeTrue();
        repo.IsDirty.Should().BeFalse();
    }

    [Fact]
    public void Description_IsClippedTo300Characters()
    {
        var text = "# T\n\n" + new string('x', 500);
        RepoDiscovery.FirstParagraph(text)!.Length.Should().Be(300);
    }

    [Fact]
    public async Task DriveRootHomeAndRelativePaths_AreRefused()
    {
        var driveRoot = Path.GetPathRoot(Path.GetTempPath())!;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        (await RepoDiscovery.DiscoverAsync(_git, driveRoot)).Error.Should().Contain("root of a drive");
        (await RepoDiscovery.DiscoverAsync(_git, home)).Error.Should().Contain("home folder");
        (await RepoDiscovery.DiscoverAsync(_git, "relative/path")).Error.Should().Contain("full path");
        (await RepoDiscovery.DiscoverAsync(_git, Path.Combine(_root, "nope"))).Error.Should().Contain("does not exist");
    }

    // ── Mirror seeding and worktree placement ───────────────────────────

    [Fact]
    public async Task RemoteMirror_IsSeededFromTheLocalCheckout()
    {
        var remote = Path.Combine(_root, "remotes", "api.git");
        Directory.CreateDirectory(remote);
        await Git(remote, "init --bare --initial-branch=main");
        var checkout = Path.Combine(_root, "base", "api");
        await InitRepoAsync(checkout, origin: remote);
        await Git(checkout, "push -u origin main");
        // A branch only the checkout has: it can only reach the mirror through the seed.
        await Git(checkout, "branch local-only");
        var remoteUrl = new Uri(remote).AbsoluteUri;

        var (manager, orgId, repoId) = NewManager();
        var result = await manager.EnsureBareCloneAsync(orgId, repoId, remoteUrl, checkout);

        result.IsSuccess.Should().BeTrue(result.Error);
        var bare = manager.GetBareRepoPath(orgId, repoId);
        (await GitOut(bare, "remote get-url origin")).Should().Be(remoteUrl);
        (await GitOut(bare, "config --get remote.origin.fetch")).Should().Be("+refs/heads/*:refs/remotes/origin/*");
        (await GitOut(bare, "for-each-ref --format=%(refname)")).Should()
            .Contain("refs/heads/local-only").And.Contain("refs/remotes/origin/main");

        // Refreshes keep working against the remote.
        (await manager.EnsureBareCloneAsync(orgId, repoId, remoteUrl)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task UnusableCheckout_FallsBackToTheNormalClone()
    {
        var remote = Path.Combine(_root, "remotes", "api.git");
        Directory.CreateDirectory(remote);
        await Git(remote, "init --bare --initial-branch=main");
        var author = Path.Combine(_root, "author");
        await InitRepoAsync(author, origin: remote);
        await Git(author, "push -u origin main");

        var (manager, orgId, repoId) = NewManager();
        var result = await manager.EnsureBareCloneAsync(
            orgId, repoId, new Uri(remote).AbsoluteUri, Path.Combine(_root, "not-a-checkout"));

        result.IsSuccess.Should().BeTrue(result.Error);
        (await GitOut(manager.GetBareRepoPath(orgId, repoId), "for-each-ref --format=%(refname)"))
            .Should().Contain("refs/remotes/origin/main");
    }

    [Fact]
    public async Task BaseFolderRepo_KeepsTaskFoldersInTheCache_NeverInsideTheBaseFolder()
    {
        var baseFolder = Path.Combine(_root, "base");
        var web = Path.Combine(baseFolder, "web");
        await InitRepoAsync(web, origin: null);

        var (manager, orgId, repoId) = NewManager();
        (await manager.EnsureBareCloneAsync(orgId, repoId, web, localCheckoutPath: web)).IsSuccess.Should().BeTrue();
        var taskId = Ulid.NewUlid();
        var worktree = await manager.CreateWorktreeAsync(orgId, repoId, taskId, "orkka/WEB-1", "main", "WEB-1");

        worktree.IsSuccess.Should().BeTrue(worktree.Error);
        Path.GetFullPath(worktree.Value!).Should().StartWith(Path.GetFullPath(Path.Combine(_root, "cache")));
        Path.GetFullPath(worktree.Value!).Should().NotStartWith(Path.GetFullPath(baseFolder));
        Directory.Exists(Path.Combine(baseFolder, "web-worktrees")).Should().BeFalse();

        // A later refresh that does not name the checkout (chat, the repo browser) keeps it so.
        (await manager.EnsureBareCloneAsync(orgId, repoId, web)).IsSuccess.Should().BeTrue();
        manager.GetWorktreePath(orgId, repoId, Ulid.NewUlid()).Should().NotStartWith(Path.GetFullPath(baseFolder));
    }

    [Fact]
    public async Task FolderLinkedOneByOne_StillGetsItsSiblingTaskFolder()
    {
        var app = Path.Combine(_root, "linked", "app");
        await InitRepoAsync(app, origin: null);

        var (manager, orgId, repoId) = NewManager();
        (await manager.EnsureBareCloneAsync(orgId, repoId, app)).IsSuccess.Should().BeTrue();

        manager.GetWorktreePath(orgId, repoId, Ulid.NewUlid())
            .Should().StartWith(Path.Combine(_root, "linked", "app-worktrees"));
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private (GitWorkspaceManager Manager, Ulid OrgId, Ulid RepoId) NewManager()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Worker:RepoBasePath"] = Path.Combine(_root, "cache"),
            })
            .Build();
        return (new GitWorkspaceManager(config, Mock.Of<ILogger<GitWorkspaceManager>>()), Ulid.NewUlid(), Ulid.NewUlid());
    }

    private static async Task InitRepoAsync(string dir, string? origin, string? readme = null, string? orkkaJson = null)
    {
        Directory.CreateDirectory(dir);
        await Git(dir, "init --initial-branch=main");
        await File.WriteAllTextAsync(Path.Combine(dir, "README.md"), readme ?? $"# {Path.GetFileName(dir)}\n");
        if (orkkaJson is not null)
            await File.WriteAllTextAsync(Path.Combine(dir, "orkka.json"), orkkaJson);
        if (orkkaJson?.Contains("node") == true)
            await File.WriteAllTextAsync(Path.Combine(dir, "package.json"), "{}");
        await Git(dir, "add -A");
        await Git(dir, $"{Identity} commit -m init");
        if (origin is not null)
            await Git(dir, $"remote add origin \"{origin}\"");
    }

    private static async Task Git(string dir, string args)
    {
        var (code, _, error) = await RunAsync(dir, args);
        if (code != 0)
            throw new InvalidOperationException($"git {args} failed: {error}");
    }

    private static async Task<string> GitOut(string dir, string args)
    {
        var (code, output, error) = await RunAsync(dir, args);
        if (code != 0)
            throw new InvalidOperationException($"git {args} failed: {error}");
        return output.Trim();
    }

    private static async Task<(int Code, string Output, string Error)> RunAsync(string dir, string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", args)
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = System.Diagnostics.Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output, await error);
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Temp folder — best effort.
        }
    }
}
