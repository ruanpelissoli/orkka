namespace Orkka.Tests.Worker;

using System.Text.Json;
using Orkka.Modules.Execution.Agents;
using FluentAssertions;
using ih0.Claude.Agent.SDK.Types;
using Microsoft.Extensions.Logging.Abstractions;

public sealed class RunCostGuardTests : IDisposable
{
    private readonly string _transcript = Path.Combine(
        Path.GetTempPath(), $"orkka-cost-guard-{Guid.NewGuid():N}.jsonl");

    public void Dispose()
    {
        try { File.Delete(_transcript); } catch { /* temp cleanup */ }
    }

    private PreToolUseHookInput ToolInput() => new()
    {
        SessionId = "test-session",
        TranscriptPath = _transcript,
        Cwd = "/repos/worktree",
        ToolName = "Bash",
        ToolInput = JsonSerializer.Deserialize<JsonElement>("""{"command":"ls"}"""),
    };

    private void WriteAssistantUsage(string messageId, long input, long output)
    {
        File.AppendAllText(_transcript,
            JsonSerializer.Serialize(new
            {
                type = "assistant",
                message = new
                {
                    id = messageId,
                    usage = new
                    {
                        input_tokens = input,
                        output_tokens = output,
                        cache_read_input_tokens = 0,
                        cache_creation_input_tokens = 0,
                    },
                },
            }) + "\n");
    }

    private static string Decision(HookOutput output) =>
        output.HookSpecificOutput?.GetRawText() ?? "";

    [Fact]
    public async Task AllowsWhileUnderTheCeiling()
    {
        // 100K in + 100K out on sonnet-4-6 = $0.30 + $1.50 = $1.80 base.
        WriteAssistantUsage("msg_1", 100_000, 100_000);
        var guard = new RunCostGuard("claude-sonnet-4-6", maxCostUsd: 5.00m, NullLogger.Instance);

        var output = await guard.EvaluateToolUse(ToolInput(), toolUseId: null, context: null);

        Decision(output).Should().Contain("allow");
    }

    [Fact]
    public async Task DeniesEveryToolCallOnceTheCeilingIsReached()
    {
        // 1M in + 1M out = $3 + $15 = $18 base — over a $5 ceiling.
        WriteAssistantUsage("msg_1", 1_000_000, 1_000_000);
        var guard = new RunCostGuard("claude-sonnet-4-6", maxCostUsd: 5.00m, NullLogger.Instance);

        var first = await guard.EvaluateToolUse(ToolInput(), toolUseId: null, context: null);
        var second = await guard.EvaluateToolUse(ToolInput(), toolUseId: null, context: null);

        Decision(first).Should().Contain("deny");
        Decision(second).Should().Contain("deny");
        // A budget stop, never a "terminated"/"restricted" message — the agent must wrap
        // up, not conclude its tools are broken.
        first.Reason!.ToLowerInvariant().Should().Contain("budget");
        first.Reason!.ToLowerInvariant().Should().NotContain("restricted");
    }

    [Fact]
    public async Task ReportsExhausted_AndAsksForAHandOffNotAFinish()
    {
        WriteAssistantUsage("msg_1", 1_000_000, 1_000_000);
        var guard = new RunCostGuard("claude-sonnet-4-6", maxCostUsd: 5.00m, NullLogger.Instance);
        guard.Exhausted.Should().BeFalse();

        var output = await guard.EvaluateToolUse(ToolInput(), toolUseId: null, context: null);

        // The invoker reads this to flag the Done chunk: a pause the orchestrator must ask
        // about, never a run that finished on its own.
        guard.Exhausted.Should().BeTrue();
        guard.LastCostUsd.Should().Be(18.00m);
        var reason = output.Reason!.ToLowerInvariant();
        reason.Should().Contain("hand-off");
        reason.Should().Contain("pause");
        reason.Should().Contain("not a request to finish");
    }

    [Fact]
    public async Task DeduplicatesRepeatedLinesForTheSameMessage()
    {
        // The CLI appends one line per content block; both carry the same message id and
        // the same (completed) usage. Summing them would double-bill the run:
        // 200K in ($0.60) + 200K out ($3.00) = $3.60 deduped, $7.20 if double-counted.
        WriteAssistantUsage("msg_1", 200_000, 200_000);
        WriteAssistantUsage("msg_1", 200_000, 200_000);
        var guard = new RunCostGuard("claude-sonnet-4-6", maxCostUsd: 5.00m, NullLogger.Instance);

        var output = await guard.EvaluateToolUse(ToolInput(), toolUseId: null, context: null);

        Decision(output).Should().Contain("allow");
    }

    [Fact]
    public async Task MissingTranscriptNeverFailsTheRun()
    {
        var guard = new RunCostGuard("claude-sonnet-4-6", maxCostUsd: 5.00m, NullLogger.Instance);

        var output = await guard.EvaluateToolUse(ToolInput(), toolUseId: null, context: null);

        Decision(output).Should().Contain("allow");
    }

    [Fact]
    public async Task NeverLeavesAFieldNull()
    {
        // See AgentHookResponses: an unset property reaches the CLI as an explicit null,
        // which zod-rejects the whole response and runs the command unguarded.
        WriteAssistantUsage("msg_1", 1_000_000, 1_000_000);
        var guard = new RunCostGuard("claude-sonnet-4-6", maxCostUsd: 1.00m, NullLogger.Instance);

        var output = await guard.EvaluateToolUse(ToolInput(), toolUseId: null, context: null);

        output.Continue.Should().NotBeNull();
        output.SuppressOutput.Should().NotBeNull();
        output.StopReason.Should().NotBeNull();
        output.Decision.Should().BeOneOf("approve", "block");
        output.SystemMessage.Should().NotBeNull();
        output.Reason.Should().NotBeNull();
        output.HookSpecificOutput.Should().NotBeNull();
    }
}
