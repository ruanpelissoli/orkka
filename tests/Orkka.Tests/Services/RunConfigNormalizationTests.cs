namespace Orkka.Tests.Services;

using FluentAssertions;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Projects.Infrastructure.Services;

/// <summary>
/// The gate between model output (or a hand-edited config) and something Orkka will spawn on
/// the user's machine. Everything it lets through eventually becomes a process, so the cases
/// that matter are the ones it must reject.
/// </summary>
public sealed class RunConfigNormalizationTests
{
    private const string RepoA = "01JQZX000000000000000000A";
    private const string RepoB = "01JQZX000000000000000000B";

    private static RunTargetDto Target(
        string key,
        string repoId = RepoA,
        string command = "npm run dev",
        string[]? dependsOn = null,
        int? port = null,
        string? kind = "web") =>
        new(key, key, kind!, repoId, command, DependsOn: dependsOn, Port: port);

    [Fact]
    public void Normalize_KeepsAWellFormedTarget()
    {
        var result = RunConfigDetector.Normalize([Target("web", port: 3000)], [RepoA]);

        result.Should().HaveCount(1);
        result[0].Key.Should().Be("web");
        result[0].RepoId.Should().Be(RepoA);
        result[0].Port.Should().Be(3000);
    }

    [Fact]
    public void Normalize_DropsTargetsWithNoCommand()
    {
        var result = RunConfigDetector.Normalize(
            [Target("web", command: "   "), Target("api")], [RepoA]);

        result.Select(t => t.Key).Should().Equal("api");
    }

    [Fact]
    public void Normalize_DropsTargetsPointingAtAnUnknownRepo()
    {
        // Two repos means no obvious default, so a bad id cannot be recovered — and a target
        // that names a repo the project does not have must never reach the runner.
        var result = RunConfigDetector.Normalize(
            [Target("web", repoId: "not-a-repo")], [RepoA, RepoB]);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Normalize_RepairsTheRepoIdWhenThereIsOnlyOneRepo()
    {
        // A model echoing the repo NAME instead of its id is the common miss, and with one
        // repo the intent is unambiguous.
        var result = RunConfigDetector.Normalize([Target("web", repoId: "my-app")], [RepoA]);

        result.Should().HaveCount(1);
        result[0].RepoId.Should().Be(RepoA);
    }

    [Fact]
    public void Normalize_SlugsKeysAndDropsDuplicates()
    {
        var result = RunConfigDetector.Normalize(
            [Target("Web App"), Target("web-app"), Target("api")], [RepoA]);

        result.Select(t => t.Key).Should().Equal("web-app", "api");
    }

    [Fact]
    public void Normalize_FallsBackToOtherForAnUnknownKind()
    {
        var result = RunConfigDetector.Normalize([Target("web", kind: "quantum")], [RepoA]);

        result[0].Kind.Should().Be(RunTargetKinds.Other);
    }

    [Fact]
    public void Normalize_DerivesTheUrlFromThePort()
    {
        var result = RunConfigDetector.Normalize([Target("web", port: 5173)], [RepoA]);

        result[0].Url.Should().Be("http://localhost:5173");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(70000)]
    public void Normalize_RejectsImpossiblePorts(int port)
    {
        var result = RunConfigDetector.Normalize([Target("web", port: port)], [RepoA]);

        result[0].Port.Should().BeNull();
        result[0].Url.Should().BeNull();
    }

    [Fact]
    public void Normalize_DropsDependenciesOnTargetsThatDidNotSurvive()
    {
        // Otherwise the runner's ordering waits on something that will never start.
        var result = RunConfigDetector.Normalize(
            [Target("web", dependsOn: ["api", "ghost"]), Target("api")], [RepoA]);

        result.Single(t => t.Key == "web").DependsOn.Should().Equal("api");
    }

    [Fact]
    public void Normalize_DropsASelfDependency()
    {
        var result = RunConfigDetector.Normalize(
            [Target("web", dependsOn: ["web"])], [RepoA]);

        result[0].DependsOn.Should().BeEmpty();
    }
}

public sealed class RunConfigJsonTests
{
    [Fact]
    public void RoundTrips()
    {
        RunTargetDto[] targets =
        [
            new("web", "Web app", RunTargetKinds.Web, "repo-1", "pnpm dev",
                WorkingDirectory: "apps/web", Port: 3000, DependsOn: ["api"]),
        ];

        var restored = RunConfigJson.Deserialize(RunConfigJson.Serialize(targets));

        restored.Should().BeEquivalentTo(targets);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{\"unexpected\":\"shape\"}")]
    public void Deserialize_DegradesToEmptyRatherThanThrowing(string? stored)
    {
        // A config written by an older shape must render as "nothing detected yet" — which
        // the UI fixes with one click — not as a 500 on the project page.
        RunConfigJson.Deserialize(stored).Should().BeEmpty();
    }
}
