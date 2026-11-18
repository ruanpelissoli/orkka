namespace Orkka.Tests.Features.Agents;

using FluentAssertions;
using Orkka.Modules.Execution.Agents;

/// <summary>
/// A chat turn must not fan out to sub-agents. They run in the background and report back by
/// delivering a task notification INTO the turn that spawned them; each delivery interrupts
/// that turn, and an interruption landing on an in-flight platform tool call kills it — the
/// CLI answers "The tool call was interrupted before a result was received". A chat that had
/// spawned eight of them could not save a single document while its instant built-in tools
/// kept working (2026-09-08). The allow-list alone does not prevent this: it pre-approves
/// tools, it does not remove the CLI's own.
/// </summary>
public sealed class ChatToolSurfaceTests
{
    [Fact]
    public void Chat_runs_refuse_the_sub_agent_tool_under_both_of_its_names()
    {
        var refused = ClaudeAgentInvoker.DisallowedToolsFor(AgentPhase.Research);

        // "Task" and "Agent" are the same CLI tool before and after its rename — a runner may
        // be on either version, so both are refused.
        refused.Should().Contain("Task").And.Contain("Agent");
    }

    [Fact]
    public void Working_phases_keep_their_sub_agents()
    {
        // Developing a large repo legitimately fans out, and a pipeline run has no live
        // conversation for a notification to interrupt.
        ClaudeAgentInvoker.DisallowedToolsFor(AgentPhase.Developing).Should().BeEmpty();
        ClaudeAgentInvoker.DisallowedToolsFor(AgentPhase.Planning).Should().BeEmpty();
    }

    [Fact]
    public void Utility_runs_keep_their_own_stripped_surface()
    {
        ClaudeAgentInvoker.DisallowedToolsFor(AgentPhase.Utility)
            .Should().Contain("Bash").And.Contain("Task");
    }
}
