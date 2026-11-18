namespace Orkka.Tests.Architecture;

using FluentAssertions;
using Orkka.SharedKernel.Ai;

/// <summary>
/// Orkka holds no AI key: every agent run, chat turn and utility completion runs on the
/// developer's runner, through their own CLI subscription (<see cref="IUserRunnerUtility"/> for
/// one-shot completions). A runner miss degrades the feature; nothing falls back to calling a
/// provider's HTTP API from the host. This fails if any first-party source starts doing so.
/// </summary>
public sealed class NoAnthropicHttpClientTests
{
    private static readonly string[] Forbidden =
    [
        "api.anthropic.com",
        "IAnthropicApiClient",
        "AddHttpClient(\"Anthropic",
        "\"x-api-key\"",
        "\"anthropic-version\"",
    ];

    [Fact]
    public void NoSource_TalksToTheAnthropicHttpApi()
    {
        var src = Path.Combine(FindRepoRoot(), "src");
        var sep = Path.DirectorySeparatorChar;
        var offenders = Directory
            .EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}obj{sep}") && !f.Contains($"{sep}bin{sep}")
                // The vendored Claude Agent SDK drives the CLI; it is not ours to police.
                && !f.Contains($"{sep}ThirdParty{sep}"))
            .Select(f => (File: Path.GetRelativePath(src, f), Text: File.ReadAllText(f)))
            .SelectMany(f => Forbidden.Where(token => f.Text.Contains(token, StringComparison.Ordinal))
                .Select(token => $"{f.File}: {token}"))
            .ToList();

        offenders.Should().BeEmpty(
            "AI work runs on the developer's runner with their own subscription — never on an " +
            "HTTP client and a key held by the host");
    }

    [Fact]
    public async Task CompleteOnRunner_TurnsAMissIntoAnActionableException()
    {
        var act = () => new NoUserRunnerUtility().CompleteOnRunnerAsync(
            Ulid.NewUlid(), "user_1",
            new AnthropicRequest { Model = "m", Messages = [new AnthropicMessage("user", "plan")] },
            "plan this milestone");

        (await act.Should().ThrowAsync<RunnerUtilityUnavailableException>())
            .Which.Message.Should().Be(
                "Your runner is offline or unavailable — run `./orkka start` to plan this milestone.");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Orkka.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Orkka.slnx not found above the test output.");
    }
}
