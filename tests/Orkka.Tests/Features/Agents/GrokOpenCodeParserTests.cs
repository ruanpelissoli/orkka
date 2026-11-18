namespace Orkka.Tests.Features.Agents;

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Execution.Agents;

/// <summary>
/// Drives the grok and opencode JSONL line mappers directly (protected internal +
/// InternalsVisibleTo), alongside their model-namespace helpers.
///
/// Both schemas are documented by example rather than as a stable contract, so both mappers
/// are deliberately tolerant — and grok's is written against BOTH published shapes (a
/// type-tagged stream and an Agent Client Protocol JSON-RPC stream) because picking one and
/// being wrong yields a run that streams nothing and reports no error. These tests pin the
/// tolerance, not the vendors' schemas.
/// </summary>
public class GrokOpenCodeParserTests
{
    private sealed class NoopAuditLog : IAgentAuditLog
    {
        public Task RecordAsync(AgentAuditRecord record, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private static GrokAgentInvoker Grok() => new(
        Options.Create(new AgentLimitsOptions()),
        new NoopAuditLog(),
        NullLogger<GrokAgentInvoker>.Instance);

    private static OpenCodeAgentInvoker OpenCode() => new(
        Options.Create(new AgentLimitsOptions()),
        new NoopAuditLog(),
        NullLogger<OpenCodeAgentInvoker>.Instance);

    // ── grok: model ids ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("grok-4.6", "grok-4.6")]
    [InlineData("grok-4.6-fast", "grok-4.6-fast")]
    public void Grok_model_ids_pass_through_unchanged(string catalogId, string expected)
        // Unlike copilot/ollama/opencode, xAI needs no namespace — nothing else in the
        // catalog starts with "grok", so the catalog id IS the CLI id.
        => GrokAgentInvoker.ToCliModel(catalogId).Should().Be(expected);

    [Theory]
    [InlineData("grok")]
    [InlineData("")]
    [InlineData("   ")]
    public void Grok_degenerate_model_ids_fall_back_to_the_default(string catalogId)
        => GrokAgentInvoker.ToCliModel(catalogId).Should().Be("grok-4.6");

    // ── grok: type-tagged stream ─────────────────────────────────────────────

    [Fact]
    public void Grok_text_events_stream_and_accumulate()
    {
        var invoker = Grok();
        var state = new CliAgentInvokerBase.CliRunState();

        var first = invoker.MapLine("""{"type":"text","text":"Hello, "}""", state).ToList();
        var second = invoker.MapLine("""{"type":"text","text":"world."}""", state).ToList();

        first.Should().ContainSingle();
        first[0].Type.Should().Be(AgentChunkType.Text);
        second.Should().ContainSingle();
        // Appending, not replacing: both published shapes stream partial chunks, so
        // replacing would leave the result holding only the last fragment.
        state.FinalText.Should().Be("Hello, world.");
    }

    [Fact]
    public void Grok_thought_maps_to_thinking_and_never_reaches_the_result()
    {
        var state = new CliAgentInvokerBase.CliRunState();
        var chunks = Grok().MapLine("""{"type":"thought","text":"Considering options"}""", state).ToList();

        chunks.Should().ContainSingle();
        chunks[0].Type.Should().Be(AgentChunkType.Thinking);
        state.FinalText.Should().BeEmpty();
    }

    [Fact]
    public void Grok_tool_call_and_finished_update_map_to_tooluse_then_toolresult()
    {
        var invoker = Grok();
        var state = new CliAgentInvokerBase.CliRunState();

        var call = invoker.MapLine(
            """{"type":"tool_call","title":"bash","command":"ls -la"}""", state).ToList();
        call.Should().ContainSingle();
        call[0].Type.Should().Be(AgentChunkType.ToolUse);
        call[0].Content.Should().Be("bash: ls -la");

        var update = invoker.MapLine(
            """{"type":"tool_call_update","status":"completed","output":"file.txt"}""", state).ToList();
        update.Should().ContainSingle();
        update[0].Type.Should().Be(AgentChunkType.ToolResult);
        update[0].Content.Should().Be("file.txt");
    }

    [Fact]
    public void Grok_tool_result_falls_through_an_empty_content_block_to_the_next_candidate()
    {
        var state = new CliAgentInvokerBase.CliRunState();

        // A content block carrying no text must not mask the later candidates — stopping the
        // chain on "" would show a tool that returned nothing.
        var chunks = Grok().MapLine(
            """{"type":"tool_call_update","status":"completed","content":[],"title":"edited 3 files"}""",
            state).ToList();

        chunks.Should().ContainSingle();
        chunks[0].Content.Should().Be("edited 3 files");
    }

    [Fact]
    public void Grok_in_progress_tool_updates_are_progress_noise_and_produce_nothing()
    {
        var state = new CliAgentInvokerBase.CliRunState();

        Grok().MapLine("""{"type":"tool_call_update","status":"in_progress"}""", state)
            .Should().BeEmpty();
    }

    [Fact]
    public void Grok_usage_event_records_tokens()
    {
        var state = new CliAgentInvokerBase.CliRunState();

        Grok().MapLine(
            """{"type":"usage","input_tokens":120,"output_tokens":45,"cached_input_tokens":80}""",
            state).ToList();

        state.PromptTokens.Should().Be(120);
        state.CompletionTokens.Should().Be(45);
        state.CacheReadTokens.Should().Be(80);
    }

    [Fact]
    public void Grok_error_event_is_captured_as_the_stream_error()
    {
        var state = new CliAgentInvokerBase.CliRunState();

        Grok().MapLine("""{"type":"error","message":"rate limited"}""", state).ToList();

        state.StreamError.Should().Be("rate limited");
    }

    // ── grok: Agent Client Protocol stream ───────────────────────────────────

    [Fact]
    public void Grok_acp_message_chunks_map_to_text_and_accumulate()
    {
        var invoker = Grok();
        var state = new CliAgentInvokerBase.CliRunState();

        invoker.MapLine(
            """{"method":"session/update","params":{"sessionId":"s_1","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Part one. "}}}}""",
            state).ToList();
        var second = invoker.MapLine(
            """{"method":"session/update","params":{"sessionId":"s_1","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Part two."}}}}""",
            state).ToList();

        second.Should().ContainSingle();
        second[0].Type.Should().Be(AgentChunkType.Text);
        state.FinalText.Should().Be("Part one. Part two.");
        state.SessionId.Should().Be("s_1");
    }

    [Fact]
    public void Grok_acp_thought_chunks_map_to_thinking()
    {
        var state = new CliAgentInvokerBase.CliRunState();
        var chunks = Grok().MapLine(
            """{"method":"session/update","params":{"update":{"sessionUpdate":"agent_thought_chunk","content":{"type":"text","text":"Hmm"}}}}""",
            state).ToList();

        chunks.Should().ContainSingle();
        chunks[0].Type.Should().Be(AgentChunkType.Thinking);
    }

    [Fact]
    public void Grok_acp_result_frame_carries_the_resumable_session_id()
    {
        var state = new CliAgentInvokerBase.CliRunState();

        Grok().MapLine("""{"id":3,"result":{"sessionId":"ses_abc","stopReason":"end_turn"}}""", state)
            .Should().BeEmpty();

        // Reported on the Done chunk so the next turn resumes instead of re-sending the
        // whole system prompt and history.
        state.SessionId.Should().Be("ses_abc");
    }

    [Fact]
    public void Grok_acp_error_frame_is_captured_as_the_stream_error()
    {
        var state = new CliAgentInvokerBase.CliRunState();

        Grok().MapLine("""{"id":3,"error":{"code":-32000,"message":"model unavailable"}}""", state).ToList();

        state.StreamError.Should().Be("model unavailable");
    }

    // ── opencode: model ids ──────────────────────────────────────────────────

    [Theory]
    [InlineData("opencode/anthropic/claude-sonnet-4-6", "anthropic/claude-sonnet-4-6")]
    [InlineData("opencode/xai/grok-4.6", "xai/grok-4.6")]
    [InlineData("OPENCODE/OPENAI/GPT-5.6-SOL", "OPENAI/GPT-5.6-SOL")]
    [InlineData("anthropic/claude-opus-5", "anthropic/claude-opus-5")] // already CLI form
    public void OpenCode_strips_only_the_harness_namespace(string catalogId, string expected)
        // The CLI's -m wants "provider/model" — the vendor half must SURVIVE the strip, which
        // is what separates this from the copilot/ollama single-segment namespaces.
        => OpenCodeAgentInvoker.ToCliModel(catalogId).Should().Be(expected);

    [Theory]
    [InlineData("opencode")]
    [InlineData("opencode/")]
    [InlineData("   ")]
    public void OpenCode_degenerate_model_ids_fall_back_to_the_anchor(string catalogId)
        => OpenCodeAgentInvoker.ToCliModel(catalogId).Should().Be("anthropic/claude-sonnet-4-6");

    // ── opencode: event stream ───────────────────────────────────────────────

    [Fact]
    public void OpenCode_regrowing_text_part_replaces_itself_instead_of_stacking()
    {
        var invoker = OpenCode();
        var state = new CliAgentInvokerBase.CliRunState();

        // opencode streams ONE growing part, re-emitting it with more text each time. A
        // plain append would produce "Hel" + "Hello" + "Hello, world." — every prefix
        // repeated inside the final answer.
        invoker.MapLine("""{"type":"text","sessionID":"ses_1","part":{"id":"prt_1","type":"text","text":"Hel"}}""", state).ToList();
        invoker.MapLine("""{"type":"text","sessionID":"ses_1","part":{"id":"prt_1","type":"text","text":"Hello"}}""", state).ToList();
        invoker.MapLine("""{"type":"text","sessionID":"ses_1","part":{"id":"prt_1","type":"text","text":"Hello, world."}}""", state).ToList();

        state.FinalText.Should().Be("Hello, world.");
        state.SessionId.Should().Be("ses_1");
    }

    [Fact]
    public void OpenCode_distinct_text_parts_append()
    {
        var invoker = OpenCode();
        var state = new CliAgentInvokerBase.CliRunState();

        invoker.MapLine("""{"type":"text","part":{"id":"prt_1","text":"First. "}}""", state).ToList();
        invoker.MapLine("""{"type":"text","part":{"id":"prt_2","text":"Second."}}""", state).ToList();

        state.FinalText.Should().Be("First. Second.");
    }

    [Fact]
    public void OpenCode_tool_use_emits_both_the_call_and_its_result()
    {
        var state = new CliAgentInvokerBase.CliRunState();
        var chunks = OpenCode().MapLine(
            """{"type":"tool_use","part":{"id":"prt_3","callID":"call_1","tool":"bash","state":{"status":"completed","title":"ls -la","output":"file.txt"}}}""",
            state).ToList();

        // The event fires on COMPLETION, so one line must produce both — a lone ToolUse would
        // leave the timeline showing a tool that never returned.
        chunks.Should().HaveCount(2);
        chunks[0].Type.Should().Be(AgentChunkType.ToolUse);
        chunks[0].Content.Should().Be("bash: ls -la");
        chunks[1].Type.Should().Be(AgentChunkType.ToolResult);
        chunks[1].Content.Should().Be("file.txt");
    }

    [Fact]
    public void OpenCode_step_finish_tokens_add_up_across_steps()
    {
        var invoker = OpenCode();
        var state = new CliAgentInvokerBase.CliRunState();

        // Each step reports only ITS OWN tokens (unlike codex/copilot cumulative totals),
        // so a two-step run must sum rather than overwrite.
        invoker.MapLine(
            """{"type":"step_finish","part":{"type":"step-finish","reason":"tool-calls","tokens":{"input":100,"output":20,"reasoning":5,"cache":{"read":40,"write":10}}}}""",
            state).ToList();
        invoker.MapLine(
            """{"type":"step_finish","part":{"type":"step-finish","reason":"stop","tokens":{"input":150,"output":30,"reasoning":0,"cache":{"read":60,"write":0}}}}""",
            state).ToList();

        state.PromptTokens.Should().Be(250);
        state.CompletionTokens.Should().Be(55); // output + reasoning
        state.CacheReadTokens.Should().Be(100);
    }

    [Fact]
    public void OpenCode_error_event_flattens_name_and_nested_message()
    {
        var state = new CliAgentInvokerBase.CliRunState();

        OpenCode().MapLine(
            """{"type":"error","sessionID":"ses_1","error":{"name":"ProviderAuthError","data":{"message":"no credentials for anthropic"}}}""",
            state).ToList();

        state.StreamError.Should().Be("ProviderAuthError: no credentials for anthropic");
    }

    [Fact]
    public void OpenCode_step_start_is_skipped()
        => OpenCode().MapLine(
            """{"type":"step_start","part":{"type":"step-start","snapshot":"abc123"}}""",
            new CliAgentInvokerBase.CliRunState()).Should().BeEmpty();

    // ── tolerance: neither mapper may throw on junk ──────────────────────────

    [Theory]
    [InlineData("""{"type":"something_we_have_never_seen","payload":{}}""")]
    [InlineData("""{"no_type_at_all":true}""")]
    [InlineData("""{"type":"text"}""")]
    [InlineData("[]")]
    [InlineData("\"a bare string\"")]
    [InlineData("42")]
    public void Unknown_shapes_produce_no_chunks_and_never_throw(string line)
    {
        // Both schemas are documented by example only. An unrecognized line must be skipped,
        // never thrown — a schema change upstream should cost detail in the timeline, not the
        // whole run.
        Grok().MapLine(line, new CliAgentInvokerBase.CliRunState()).Should().BeEmpty();
        OpenCode().MapLine(line, new CliAgentInvokerBase.CliRunState()).Should().BeEmpty();
    }
}
