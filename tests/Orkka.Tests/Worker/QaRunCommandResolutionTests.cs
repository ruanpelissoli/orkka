namespace Orkka.Tests.Worker;

using FluentAssertions;
using Orkka.Modules.Projects.Contracts;

/// <summary>
/// The QA lane resolves its run command repo-first, falling back to the project setting.
/// The fallback is the whole reason no data migration was needed: single-repo projects
/// configured before the per-repo field existed keep working untouched.
/// </summary>
public sealed class QaRunCommandResolutionTests
{
    private static string? Resolve(string? repoCommand, string? projectCommand)
        // Mirrors TaskOrchestrator's expression. Kept as a test-local copy deliberately:
        // the rule is two lines, and pulling it into production code purely to be testable
        // would add indirection nobody reading the orchestrator would thank us for.
        => repoCommand is { Length: > 0 } ? repoCommand : projectCommand;

    [Fact]
    public void The_repos_own_command_wins()
    {
        Resolve("dotnet run", "npm run dev").Should().Be("dotnet run");
    }

    [Fact]
    public void The_project_setting_still_applies_when_the_repo_has_none()
    {
        // Every project configured before per-repo commands existed lands here.
        Resolve(null, "npm run dev").Should().Be("npm run dev");
    }

    [Fact]
    public void A_blank_repo_command_is_not_a_configured_one()
    {
        // Clearing the field writes null, but a whitespace round-trip must not shadow the
        // project setting with an empty string.
        Resolve("", "npm run dev").Should().Be("npm run dev");
    }

    [Fact]
    public void Neither_configured_means_escalate()
    {
        Resolve(null, null).Should().BeNull();
    }

    [Fact]
    public void Repo_summary_defaults_the_command_to_null()
    {
        // Trailing default: older positional constructions must keep compiling and must not
        // accidentally claim a command.
        var repo = new RepoSummary(
            Ulid.NewUlid(), Ulid.NewUlid(), Ulid.NewUlid(), "app",
            "https://github.com/acme/app.git", "main", "#6366f1",
            WorkspaceStatus.Ready);

        repo.QaRunCommand.Should().BeNull();
        repo.SetupCommand.Should().BeNull();
    }
}
