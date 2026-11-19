namespace Orkka.Tests.Features.Runners;

using FluentAssertions;
using Moq;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Agents.Contracts;
using Orkka.Runners.Server;
using Orkka.SharedKernel.Ai;

/// <summary>Readiness where only the listed providers can run.</summary>
file sealed class Ready(params AiProvider[] connected) : IProviderReadiness
{
    public string? GetBlocker(AiProvider provider) => connected.Contains(provider) ? null : "not signed in";
    public IReadOnlyList<AiProvider> Connected { get; } = connected;
    public string? GetAnyProviderBlocker() => connected.Length > 0 ? null : "nothing signed in";
}

/// <summary>
/// Utility one-shots (titles, plans, decomposition) are system work too: a workspace that chose
/// Copilot runs them on Copilot Auto when the runner has it, and on the old rule otherwise.
/// </summary>
public sealed class RunnerUtilityWorkspaceProviderTests
{
    private static readonly AnthropicRequest Request = new()
    {
        Model = "claude-sonnet-5",
        MaxTokens = 512,
        Messages = [new AnthropicMessage("user", "Title this")],
    };

    private static (Mock<IRunnerLink> Link, List<RunnerUtilityRequest> Sent) Link()
    {
        var sent = new List<RunnerUtilityRequest>();
        var link = new Mock<IRunnerLink>();
        link.SetupGet(l => l.Status).Returns((true, new RunnerHello(
            5, "laptop", true, true, [], [], Capabilities: [RunnerCapabilities.UtilityProvider])));
        link.Setup(l => l.InvokeAsync<RunnerUtilityResult>(
                RunnerProtocol.UtilityComplete, It.IsAny<object?[]>(), It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>()))
            .Callback<string, object?[], CancellationToken, TimeSpan?>((_, args, _, _) => sent.Add((RunnerUtilityRequest)args[0]!))
            .ReturnsAsync(new RunnerUtilityResult("ok", 1, 1, 0, 0, null));
        return (link, sent);
    }

    [Fact]
    public async Task WorkspaceChoseCopilot_AndItIsReady_RunsOnCopilotAuto()
    {
        var (link, sent) = Link();
        await new RunnerAnthropicClient(link.Object,
                new Ready(AiProvider.Anthropic, AiProvider.GitHubCopilot), null, AiProvider.GitHubCopilot)
            .CreateMessageAsync(Request);

        sent.Single().Should().Match<RunnerUtilityRequest>(r =>
            r.Model == "copilot/auto" && r.Provider == nameof(AiProvider.GitHubCopilot));
    }

    [Fact]
    public async Task WorkspaceChoseCopilot_ButItIsNotReady_KeepsTheRequestedClaudeModel()
    {
        var (link, sent) = Link();
        await new RunnerAnthropicClient(link.Object, new Ready(AiProvider.Anthropic), null, AiProvider.GitHubCopilot)
            .CreateMessageAsync(Request);

        sent.Single().Should().Match<RunnerUtilityRequest>(r => r.Model == "claude-sonnet-5" && r.Provider == null);
    }

    [Fact]
    public async Task Automatic_IsUnchanged()
    {
        var (link, sent) = Link();
        await new RunnerAnthropicClient(link.Object, new Ready(AiProvider.Anthropic, AiProvider.GitHubCopilot))
            .CreateMessageAsync(Request);

        sent.Single().Model.Should().Be("claude-sonnet-5");
    }
}
