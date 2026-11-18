namespace Orkka.Tests.Features.Agents;

using FluentAssertions;
using Orkka.Modules.Agents.Domain;
using Orkka.Modules.Agents.Features.AgentChat;

/// <summary>
/// Milestone support in chat is tool-only, which makes the tool-mode prompt the thing that
/// decides whether it works at all: the MCP schemas are visible to the model, but nothing else
/// tells it that milestones exist or when to file into one. Without this guidance the PM agent
/// files every task into the Backlog and the roadmap stays empty.
/// </summary>
public sealed class MilestoneToolWiringTests
{
    private static ProjectAgent PmAgent() => new()
    {
        OrgId = Ulid.NewUlid(),
        ProjectId = Ulid.NewUlid(),
        Name = "Luna",
        Role = AgentRole.PM,
        SystemPrompt = "You are the PM.",
    };

    private static string BuildPmPrompt(bool toolsEnabled) =>
        SendMessageEndpoint.BuildSystemPrompt(
            PmAgent(), memories: [], history: [], repos: [],
            contextSummary: null, locale: null,
            designAssetContext: null,
            projectName: "Nouri", toolsEnabled: toolsEnabled);

    [Fact]
    public void ToolMode_PmPrompt_NamesBothMilestoneTools()
    {
        var prompt = BuildPmPrompt(toolsEnabled: true);

        prompt.Should().Contain("mcp__orkka__list_milestones");
        prompt.Should().Contain("mcp__orkka__create_milestone");
    }

    [Fact]
    public void ToolMode_PmPrompt_TellsTheAgentToCheckBeforeGuessing()
    {
        var prompt = BuildPmPrompt(toolsEnabled: true);

        // An unrecognized milestone title resolves to the Backlog rather than erroring, so the
        // instruction to omit rather than invent is what keeps work from silently misfiling.
        prompt.Should().Contain("omit the field when unsure");
    }

    [Fact]
    public void FencedMode_PmPrompt_SaysNothingAboutMilestones()
    {
        var prompt = BuildPmPrompt(toolsEnabled: false);

        // Tool-only by decision: a Gemini-backed or runner-routed turn gets the legacy
        // fenced-block path, which has no milestone plumbing. Advertising milestones there
        // would invite the agent to emit a field that is then silently dropped.
        prompt.Should().NotContain("milestone");
        prompt.Should().NotContain("Milestone");
    }

    [Fact]
    public void NonPmAgents_GetNoMilestoneGuidance()
    {
        var designer = new ProjectAgent
        {
            OrgId = Ulid.NewUlid(),
            ProjectId = Ulid.NewUlid(),
            Name = "Muse",
            Role = AgentRole.Designer,
            SystemPrompt = "You are the designer.",
        };

        var prompt = SendMessageEndpoint.BuildSystemPrompt(
            designer, memories: [], history: [], repos: [],
            contextSummary: null, locale: null,
            designAssetContext: null,
            projectName: "Nouri", toolsEnabled: true);

        // Planning is the PM's job; the write tools are role-gated to match.
        prompt.Should().NotContain("mcp__orkka__create_milestone");
    }
}
