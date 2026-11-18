namespace Orkka.Tests.Worker;

using FluentAssertions;
using Orkka.Modules.Execution.Orchestration;
using Orkka.Modules.Projects.Contracts;

public sealed class QaTargetModeTests
{
    private static readonly Ulid Repo = Ulid.NewUlid();
    private static readonly Ulid OtherRepo = Ulid.NewUlid();

    private static RunTargetDto Target(string kind, Ulid repoId, string key = "t")
        => new(key, kind, kind, repoId.ToString(), "run it");

    [Fact]
    public void NoConfig_StaysOnBrowser()
    {
        QaTargetMode.IsApiOnly(null, Repo).Should().BeFalse();
        QaTargetMode.IsApiOnly([], Repo).Should().BeFalse();
    }

    [Fact]
    public void ApiOnly_UsesApiMode()
        => QaTargetMode.IsApiOnly([Target(RunTargetKinds.Api, Repo)], Repo).Should().BeTrue();

    [Fact]
    public void ApiAndWebInSameRepo_StaysOnBrowser()
    {
        // A monorepo serving both: the UI is the higher-level thing to verify, and driving
        // it exercises the API underneath anyway.
        RunTargetDto[] targets =
            [Target(RunTargetKinds.Api, Repo, "api"), Target(RunTargetKinds.Web, Repo, "web")];

        QaTargetMode.IsApiOnly(targets, Repo).Should().BeFalse();
    }

    [Fact]
    public void WebInAnotherRepo_DoesNotBlockApiMode()
    {
        // The QA run is scoped to ONE repo's worktree — a sibling repo's UI is not reachable
        // from it, so it must not drag this repo onto the browser lane.
        RunTargetDto[] targets =
            [Target(RunTargetKinds.Api, Repo, "api"), Target(RunTargetKinds.Web, OtherRepo, "web")];

        QaTargetMode.IsApiOnly(targets, Repo).Should().BeTrue();
    }

    [Fact]
    public void NoTargetsForThisRepo_StaysOnBrowser()
        => QaTargetMode.IsApiOnly([Target(RunTargetKinds.Api, OtherRepo)], Repo).Should().BeFalse();

    [Theory]
    [InlineData(RunTargetKinds.Worker)]
    [InlineData(RunTargetKinds.Database)]
    [InlineData("backend")]  // unrecognised — normalizes to "other", never matches api
    public void NonApiBackendKinds_StayOnBrowser(string kind)
        => QaTargetMode.IsApiOnly([Target(kind, Repo)], Repo).Should().BeFalse();

    [Fact]
    public void RepoIdMatchIsCaseInsensitive()
    {
        // Ulid round-trips uppercase, but the column is model-written text — a lowercased
        // value must not silently fall back to the browser lane.
        var target = Target(RunTargetKinds.Api, Repo) with { RepoId = Repo.ToString().ToLowerInvariant() };

        QaTargetMode.IsApiOnly([target], Repo).Should().BeTrue();
    }
}
