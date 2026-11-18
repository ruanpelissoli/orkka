namespace Orkka.Tests.Worker;

using FluentAssertions;
using Orkka.Modules.Execution.Orchestration;
using Orkka.Modules.Projects.Contracts;

/// <summary>
/// The QA lane's run command when nobody configured one: derived from the run targets, in
/// bash terms, infra first and detached, in the target's own folder, with the example env
/// copied in. Each clause exists because its absence is a QA run that fails before testing.
/// </summary>
public sealed class QaRunCommandDerivationTests
{
    private static readonly Ulid RepoA = Ulid.Parse("01JQZX0000000000000000000A");
    private static readonly Ulid RepoB = Ulid.Parse("01JQZX0000000000000000000B");

    [Fact]
    public void Derive_PrefersTheWebTargetAndStartsItsInfraDetached()
    {
        RunTargetDto[] targets =
        [
            new("db", "Postgres", "infra", RepoA.ToString(), "docker compose up postgres", Port: 5432),
            new("api", "API", "api", RepoA.ToString(), "go run ./cmd/api", Port: 8080, DependsOn: ["db"]),
            new("web", "Web", "web", RepoA.ToString(), "npm run dev", WorkingDirectory: "apps/web",
                InstallCommand: "npm install", Port: 3000, DependsOn: ["api"]),
        ];

        var command = QaRunCommandDerivation.Derive(targets, RepoA);

        command.Should().Be(
            "docker compose up -d postgres && cd apps/web && "
            + "( [ -f .env ] || [ ! -f .env.example ] || cp .env.example .env ) && "
            + "npm install && npm run dev");
    }

    [Fact]
    public void Derive_FallsBackToTheApiWhenThereIsNoUi()
    {
        RunTargetDto[] targets =
        [
            new("api", "API", "api", RepoA.ToString(), "dotnet run", Port: 5000),
        ];

        QaRunCommandDerivation.Derive(targets, RepoA).Should().EndWith("dotnet run");
    }

    [Fact]
    public void Derive_IgnoresOtherReposAndInfraOnlyRepos()
    {
        RunTargetDto[] targets =
        [
            new("web", "Web", "web", RepoB.ToString(), "npm run dev", Port: 3000),
            new("db", "Postgres", "infra", RepoA.ToString(), "docker compose up postgres", Port: 5432),
        ];

        QaRunCommandDerivation.Derive(targets, RepoA).Should().BeNull();
        QaRunCommandDerivation.Derive(null, RepoA).Should().BeNull();
        QaRunCommandDerivation.Derive([], RepoA).Should().BeNull();
    }

    [Theory]
    [InlineData("docker compose up postgres", "docker compose up -d postgres")]
    [InlineData("docker-compose up", "docker-compose up -d")]
    [InlineData("docker compose up -d postgres", "docker compose up -d postgres")]
    [InlineData("docker compose up --detach db", "docker compose up --detach db")]
    [InlineData("redis-server", "redis-server")]
    public void Detach_AddsTheFlagOnlyToComposeUp(string input, string expected)
    {
        QaRunCommandDerivation.Detach(input).Should().Be(expected);
    }

    [Fact]
    public void DeriveSetupCommand_IsTheInstallCommandInItsFolder()
    {
        RunTargetDto[] targets =
        [
            new("web", "Web", "web", RepoA.ToString(), "npm run dev", WorkingDirectory: "apps/web",
                InstallCommand: "npm ci"),
        ];

        QaRunCommandDerivation.DeriveSetupCommand(targets, RepoA).Should().Be("cd apps/web && npm ci");
        QaRunCommandDerivation.DeriveSetupCommand(
            [new("api", "API", "api", RepoA.ToString(), "go run .")], RepoA).Should().BeNull();
    }
}
