namespace Orkka.Tests.Projects;

using FluentAssertions;
using Moq;
using Orkka.Modules.Projects.Domain;
using Orkka.Modules.Projects.Features.ProjectRepos;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.SharedKernel.Security;
using Orkka.SharedKernel.Utilities;

/// <summary>
/// Which reader sees a repository: the requesting user's runner, for a remote and a folder
/// alike — Orkka holds no credential for anyone's repositories, so the clone lives on the
/// developer's machine. Only a host with no route to a runner falls back to the platform.
/// </summary>
public sealed class RepoReadersTests
{
    private static readonly Ulid OrgId = Ulid.NewUlid();
    private static readonly Ulid RepoId = Ulid.NewUlid();
    private const string UserId = "user_ruan";

    private readonly Mock<ILocalRepoReader> _local = new();

    private RepoReaders Build(bool canReach)
    {
        _local.SetupGet(l => l.CanReach).Returns(canReach);
        var user = new Mock<ICurrentUser>();
        user.SetupGet(u => u.UserId).Returns(UserId);
        user.SetupGet(u => u.OrgId).Returns(OrgId);
        return new RepoReaders(_local.Object, user.Object);
    }

    private static ProjectRepo Repo(string url) => new()
    {
        Id = RepoId, OrgId = OrgId, ProjectId = Ulid.NewUlid(),
        Name = "storefront-api", GitUrl = url,
    };

    [Fact]
    public void ARemoteRepository_IsReadThroughTheRunnerToo()
    {
        var readers = Build(canReach: true);

        readers.For(Repo("git@github.com:acme/app.git"))
            .Should().BeOfType<RepoReaders.LocalRepoReaderAdapter>();
        readers.CanReachRunner.Should().BeTrue();
    }

    [Fact]
    public async Task WithNoRouteToTheMachine_EveryReadSaysTheRunnerIsOffline()
    {
        var readers = Build(canReach: false);
        var reader = readers.For(Repo(@"C:\Code\storefront-api"));

        readers.CanReachRunner.Should().BeFalse();
        var tree = await reader.ListTreeAsync(OrgId, RepoId, "main");
        tree.IsFailure.Should().BeTrue("Orkka keeps no copy of anyone's code to read instead");
        tree.Error.Should().Contain("runner is offline");
        (await reader.EnsureBareCloneAsync(OrgId, RepoId, "git@github.com:acme/app.git"))
            .IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task AFolderOnTheUsersMachine_IsReadThroughTheirRunner_AsTheCurrentUser()
    {
        var readers = Build(canReach: true);
        var entries = new List<RepoTreeEntry> { new("src", "src", true) };
        _local.Setup(l => l.ListTreeAsync(OrgId, RepoId, UserId, "main", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<List<RepoTreeEntry>>.Success(entries));

        var reader = readers.For(Repo(@"C:\Code\storefront-api"));
        var tree = await reader.ListTreeAsync(OrgId, RepoId, "main");

        reader.Should().BeOfType<RepoReaders.LocalRepoReaderAdapter>();
        tree.Value.Should().BeEquivalentTo(entries);
    }

    [Fact]
    public async Task SyncingAFolder_RefreshesTheRunnersMirrorOfThatFolder()
    {
        var readers = Build(canReach: true);
        _local.Setup(l => l.EnsureMirrorAsync(OrgId, RepoId, UserId, @"C:\Code\storefront-api", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        var result = await readers.For(Repo(@"C:\Code\storefront-api"))
            .EnsureBareCloneAsync(OrgId, RepoId, @"C:\Code\storefront-api");

        result.IsSuccess.Should().BeTrue(result.Error);
        _local.Verify(l => l.EnsureMirrorAsync(OrgId, RepoId, UserId, @"C:\Code\storefront-api", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BranchWrites_AreRefusedFromTheHostedSide()
    {
        var readers = Build(canReach: true);
        var reader = readers.For(Repo(@"C:\Code\storefront-api"));

        (await reader.CreateBranchAsync(OrgId, RepoId, "feature/x", "main")).IsFailure.Should().BeTrue();
        (await reader.EnsureBranchAsync(OrgId, RepoId, "feature/x")).IsFailure.Should().BeTrue();
        _local.Verify(l => l.CanReach, Times.AtLeastOnce);
        _local.VerifyNoOtherCalls();
    }
}
