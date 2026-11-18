namespace Orkka.Tests.Features.Agents;

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Execution.Agents;

/// <summary>
/// Drives the codex/gemini/copilot JSONL line mappers directly (protected internal +
/// InternalsVisibleTo). The codex/gemini event shapes mirror captured CLI transcripts; the
/// copilot shapes mirror documented plausible forms pending real-CLI capture. The mappers
/// must be tolerant — garbage and unknown event types produce no chunks and never throw.
/// </summary>
public class CliAgentInvokerParserTests
{
    private sealed class NoopAuditLog : IAgentAuditLog
    {
        public Task RecordAsync(AgentAuditRecord record, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private static CodexAgentInvoker Codex() => new(
        Options.Create(new AgentLimitsOptions()),
        new NoopAuditLog(),
        NullLogger<CodexAgentInvoker>.Instance);

    private static GeminiAgentInvoker Gemini() => new(
        Options.Create(new AgentLimitsOptions()),
        new NoopAuditLog(),
        NullLogger<GeminiAgentInvoker>.Instance);

    private static CopilotAgentInvoker Copilot() => new(
        Options.Create(new AgentLimitsOptions()),
        new NoopAuditLog(),
        NullLogger<CopilotAgentInvoker>.Instance);

    // ── codex ────────────────────────────────────────────────────────────────

    [Fact]
    public void Codex_agent_message_maps_to_text_and_final_result()
    {
        var state = new CliAgentInvokerBase.CliRunState();
        var chunks = Codex().MapLine(
            """{"type":"item.completed","item":{"id":"item_1","item_type":"agent_message","text":"All done."}}""",
            state).ToList();

        chunks.Should().ContainSingle();
        chunks[0].Type.Should().Be(AgentChunkType.Text);
        chunks[0].Content.Should().Be("All done.");
        state.FinalText.Should().Be("All done.");
    }

    [Fact]
    public void Codex_command_execution_maps_to_tooluse_then_toolresult()
    {
        var invoker = Codex();
        var state = new CliAgentInvokerBase.CliRunState();

        var started = invoker.MapLine(
            """{"type":"item.started","item":{"id":"item_2","item_type":"command_execution","command":"ls -la","status":"in_progress"}}""",
            state).ToList();
        started.Should().ContainSingle();
        started[0].Type.Should().Be(AgentChunkType.ToolUse);
        started[0].Content.Should().Be("command_execution: ls -la");

        var completed = invoker.MapLine(
            """{"type":"item.completed","item":{"id":"item_2","item_type":"command_execution","command":"ls -la","aggregated_output":"file.txt","exit_code":0}}""",
            state).ToList();
        completed.Should().ContainSingle();
        completed[0].Type.Should().Be(AgentChunkType.ToolResult);
        completed[0].Content.Should().Be("file.txt");
    }

    [Fact]
    public void Codex_turn_completed_accumulates_usage_across_turns()
    {
        var invoker = Codex();
        var state = new CliAgentInvokerBase.CliRunState();

        invoker.MapLine(
            """{"type":"turn.completed","usage":{"input_tokens":100,"cached_input_tokens":40,"output_tokens":25}}""",
            state).Should().BeEmpty();
        invoker.MapLine(
            """{"type":"turn.completed","usage":{"input_tokens":50,"cached_input_tokens":10,"output_tokens":5}}""",
            state).Should().BeEmpty();

        state.PromptTokens.Should().Be(150);
        state.CacheReadTokens.Should().Be(50);
        state.CompletionTokens.Should().Be(30);
    }

    [Fact]
    public void Codex_reasoning_maps_to_thinking()
    {
        var state = new CliAgentInvokerBase.CliRunState();
        var chunks = Codex().MapLine(
            """{"type":"item.completed","item":{"item_type":"reasoning","text":"Considering the diff."}}""",
            state).ToList();

        chunks.Should().ContainSingle();
        chunks[0].Type.Should().Be(AgentChunkType.Thinking);
    }

    [Fact]
    public void Codex_error_event_is_captured_not_yielded()
    {
        var state = new CliAgentInvokerBase.CliRunState();
        Codex().MapLine("""{"type":"error","message":"stream disconnected"}""", state)
            .Should().BeEmpty();
        state.StreamError.Should().Be("stream disconnected");
    }

    [Theory]
    [InlineData("""{"type":"thread.started","thread_id":"t_1"}""")]
    [InlineData("""{"type":"turn.started"}""")]
    [InlineData("""{"type":"some.future.event","payload":{"x":1}}""")]
    [InlineData("""{"noType":true}""")]
    [InlineData("""[1,2,3]""")]
    public void Codex_unknown_events_produce_no_chunks(string line)
    {
        var state = new CliAgentInvokerBase.CliRunState();
        Codex().MapLine(line, state).Should().BeEmpty();
    }

    // ── gemini ───────────────────────────────────────────────────────────────

    [Fact]
    public void Gemini_assistant_text_maps_to_text()
    {
        var state = new CliAgentInvokerBase.CliRunState();
        var chunks = Gemini().MapLine(
            """{"type":"assistant","text":"Here is the plan."}""",
            state).ToList();

        chunks.Should().ContainSingle();
        chunks[0].Type.Should().Be(AgentChunkType.Text);
        state.FinalText.Should().Be("Here is the plan.");
    }

    [Fact]
    public void Gemini_nested_message_content_parts_are_joined()
    {
        var state = new CliAgentInvokerBase.CliRunState();
        var chunks = Gemini().MapLine(
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Part one. "},{"type":"text","text":"Part two."}]}}""",
            state).ToList();

        chunks.Should().ContainSingle();
        chunks[0].Content.Should().Be("Part one. Part two.");
    }

    [Fact]
    public void Gemini_tool_call_and_result_map_to_tool_chunks()
    {
        var invoker = Gemini();
        var state = new CliAgentInvokerBase.CliRunState();

        var call = invoker.MapLine(
            """{"type":"tool_call","name":"read_file","args":{"path":"src/app.ts"}}""",
            state).ToList();
        call.Should().ContainSingle();
        call[0].Type.Should().Be(AgentChunkType.ToolUse);
        call[0].Content.Should().StartWith("read_file:");

        var result = invoker.MapLine(
            """{"type":"tool_result","output":"export const x = 1;"}""",
            state).ToList();
        result.Should().ContainSingle();
        result[0].Type.Should().Be(AgentChunkType.ToolResult);
    }

    [Fact]
    public void Gemini_final_stats_event_sets_usage_totals()
    {
        var state = new CliAgentInvokerBase.CliRunState();
        Gemini().MapLine(
            """{"type":"result","response":"Done.","stats":{"models":{"gemini-3-flash":{"tokens":{"prompt":1200,"candidates":300,"total":1500,"cached":800}}}}}""",
            state).Should().BeEmpty();

        state.FinalText.Should().Be("Done.");
        state.PromptTokens.Should().Be(1200);
        state.CompletionTokens.Should().Be(300);
        state.CacheReadTokens.Should().Be(800);
    }

    [Fact]
    public void Gemini_error_event_is_captured_not_yielded()
    {
        var state = new CliAgentInvokerBase.CliRunState();
        Gemini().MapLine("""{"type":"error","message":"quota exceeded"}""", state)
            .Should().BeEmpty();
        state.StreamError.Should().Be("quota exceeded");
    }

    [Theory]
    [InlineData("""{"type":"init","cwd":"/tmp"}""")]
    [InlineData("""{"unrelated":"shape"}""")]
    [InlineData("\"just a string\"")] // JSON string token, not an object
    public void Gemini_unknown_events_produce_no_chunks(string line)
    {
        var state = new CliAgentInvokerBase.CliRunState();
        Gemini().MapLine(line, state).Should().BeEmpty();
    }

    [Theory]
    // Two generations of dead ids must map to ids the API-key backend actually serves:
    // the Code Assist aliases, and the "-preview" ids Google has since shut down or
    // deprecated. Everything else passes through untouched.
    [InlineData("gemini-3-pro", "gemini-3.1-pro-preview")]
    [InlineData("gemini-3-flash", "gemini-3.6-flash")]
    [InlineData("GEMINI-3-PRO", "gemini-3.1-pro-preview")]
    [InlineData("gemini-3-pro-preview", "gemini-3.1-pro-preview")]
    [InlineData("gemini-3-flash-preview", "gemini-3.6-flash")]
    [InlineData("gemini-3.1-pro-preview", "gemini-3.1-pro-preview")]
    [InlineData("gemini-3.6-flash", "gemini-3.6-flash")]
    [InlineData("gemini-2.5-pro", "gemini-2.5-pro")]
    public void Gemini_legacy_model_ids_map_to_api_served_ids(string catalogId, string cliModel)
        => GeminiAgentInvoker.ToGeminiCliModel(catalogId).Should().Be(cliModel);

    // ── copilot ──────────────────────────────────────────────────────────────
    // Shapes are plausible-per-docs, pending real-CLI capture: the mapper accepts both
    // flat Claude-SDK-style events and codex-style item envelopes.

    [Theory]
    [InlineData("copilot/claude-sonnet-5", "claude-sonnet-5")]
    [InlineData("copilot/gpt-5.6-sol", "gpt-5.6-sol")]
    [InlineData("copilot/claude-sonnet-4.5", "claude-sonnet-4.5")] // retired id still strips — agents saved on it keep running
    [InlineData("copilot/gpt-5", "gpt-5")]
    [InlineData("copilot/auto", "auto")]
    [InlineData("copilot", "auto")] // bare provider id degrades to auto, never an empty --model
    [InlineData("claude-sonnet-4.5", "claude-sonnet-4.5")] // already-bare ids pass through
    public void Copilot_model_ids_strip_the_catalog_namespace(string catalogId, string cliModel)
        => CopilotAgentInvoker.ToCliModel(catalogId).Should().Be(cliModel);

    [Fact]
    public void Copilot_flat_assistant_text_maps_to_text_and_final_result()
    {
        var state = new CliAgentInvokerBase.CliRunState();
        var chunks = Copilot().MapLine(
            """{"type":"assistant","text":"Done — tests pass."}""",
            state).ToList();

        chunks.Should().ContainSingle();
        chunks[0].Type.Should().Be(AgentChunkType.Text);
        state.FinalText.Should().Be("Done — tests pass.");
    }

    [Fact]
    public void Copilot_nested_message_content_parts_are_joined()
    {
        var state = new CliAgentInvokerBase.CliRunState();
        var chunks = Copilot().MapLine(
            """{"type":"message","message":{"role":"assistant","content":[{"type":"text","text":"Part one. "},{"type":"text","text":"Part two."}]}}""",
            state).ToList();

        chunks.Should().ContainSingle();
        chunks[0].Content.Should().Be("Part one. Part two.");
    }

    [Fact]
    public void Copilot_item_envelope_agent_message_maps_to_text()
    {
        var state = new CliAgentInvokerBase.CliRunState();
        var chunks = Copilot().MapLine(
            """{"type":"item.completed","item":{"type":"agent_message","text":"All done."}}""",
            state).ToList();

        chunks.Should().ContainSingle();
        chunks[0].Type.Should().Be(AgentChunkType.Text);
        state.FinalText.Should().Be("All done.");
    }

    [Fact]
    public void Copilot_tool_call_and_result_map_to_tool_chunks()
    {
        var invoker = Copilot();
        var state = new CliAgentInvokerBase.CliRunState();

        var call = invoker.MapLine(
            """{"type":"tool_call","name":"shell","arguments":{"command":"npm test"}}""",
            state).ToList();
        call.Should().ContainSingle();
        call[0].Type.Should().Be(AgentChunkType.ToolUse);
        call[0].Content.Should().StartWith("shell:");

        var result = invoker.MapLine(
            """{"type":"tool_result","output":"42 passing"}""",
            state).ToList();
        result.Should().ContainSingle();
        result[0].Type.Should().Be(AgentChunkType.ToolResult);
    }

    [Fact]
    public void Copilot_usage_on_any_event_sets_totals_replacing_not_adding()
    {
        var invoker = Copilot();
        var state = new CliAgentInvokerBase.CliRunState();

        invoker.MapLine(
            """{"type":"turn.completed","usage":{"input_tokens":100,"output_tokens":20,"cache_read_input_tokens":40}}""",
            state).Should().BeEmpty();
        invoker.MapLine(
            """{"type":"turn.completed","usage":{"input_tokens":300,"output_tokens":60,"cache_read_input_tokens":90}}""",
            state).Should().BeEmpty();

        state.PromptTokens.Should().Be(300);
        state.CompletionTokens.Should().Be(60);
        state.CacheReadTokens.Should().Be(90);
    }

    [Fact]
    public void Copilot_error_event_is_captured_not_yielded()
    {
        var state = new CliAgentInvokerBase.CliRunState();
        Copilot().MapLine("""{"type":"error","message":"premium request quota exhausted"}""", state)
            .Should().BeEmpty();
        state.StreamError.Should().Be("premium request quota exhausted");
    }

    [Theory]
    [InlineData("""{"type":"session.started","session_id":"s_1"}""")]
    [InlineData("""{"type":"some.future.event","payload":{"x":1}}""")]
    [InlineData("""{"noType":true}""")]
    [InlineData("""[1,2,3]""")]
    public void Copilot_unknown_events_produce_no_chunks(string line)
    {
        var state = new CliAgentInvokerBase.CliRunState();
        Copilot().MapLine(line, state).Should().BeEmpty();
    }

    // ── session-id capture (resume support) ─────────────────────────────────

    [Fact]
    public void Codex_thread_started_captures_session_id_without_chunks()
    {
        var state = new CliAgentInvokerBase.CliRunState();
        Codex().MapLine(
            """{"type":"thread.started","thread_id":"0199a213-81c0-7800-8fbc-a459b02b7a15"}""",
            state).Should().BeEmpty();

        state.SessionId.Should().Be("0199a213-81c0-7800-8fbc-a459b02b7a15");
    }

    [Fact]
    public void Copilot_session_id_captured_from_any_event_first_wins()
    {
        var invoker = Copilot();
        var state = new CliAgentInvokerBase.CliRunState();

        invoker.MapLine("""{"type":"session.started","session_id":"s_1"}""", state)
            .Should().BeEmpty();
        invoker.MapLine("""{"type":"session.started","session_id":"s_2"}""", state)
            .Should().BeEmpty();

        state.SessionId.Should().Be("s_1");
    }
}
