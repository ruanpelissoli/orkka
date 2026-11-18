namespace Orkka.Tests.Services;

using FluentAssertions;
using Orkka.LocalRunner;
using Orkka.Modules.Execution.Agents;

/// <summary>
/// The runner's utility op (brief/PRD synthesis, decomposition, brand kits) folds a CLI
/// chunk stream into one answer and retries a failure once. Both halves are pinned here
/// because their failure mode was invisible: a brief synthesis that hit the CLI's
/// output-token cap was retried into the same cap, and the two dead generations outran
/// the host's 15-minute op ceiling — a "stuck" provisioning step for what the host's own
/// key then finished in a minute.
/// </summary>
public sealed class RunnerOperationsUtilityTests
{
    private const string CapError =
        "API Error: Claude's response exceeded the 8192 output token maximum. " +
        "To configure this behavior, set the CLAUDE_CODE_MAX_OUTPUT_TOKENS environment variable.";

    private const string ProcessError =
        "Agent process failed: Command failed (exit code: 1)\nError output: Check stderr output for details";

    [Fact]
    public async Task CollectUtilityResult_KeepsTheFirstError_NotTheProcessExitThatFollows()
    {
        // The CLI reports the cause in its result message and then exits 1, which the
        // invoker surfaces as a second, generic error chunk.
        var result = await RunnerOperations.CollectUtilityResultAsync(Stream(
            new AgentChunk(AgentChunkType.Error, CapError),
            new AgentChunk(AgentChunkType.Error, ProcessError)));

        result.Error.Should().Be(CapError);
        result.Text.Should().BeNull();
    }

    [Fact]
    public async Task CollectUtilityResult_FoldsTextAndUsage()
    {
        var result = await RunnerOperations.CollectUtilityResultAsync(Stream(
            new AgentChunk(AgentChunkType.Text, "# Brief\n"),
            new AgentChunk(AgentChunkType.Text, "Body."),
            new AgentChunk(AgentChunkType.Done, "",
                PromptTokens: 6178, CompletionTokens: 3082, CacheReadTokens: 10, CacheWriteTokens: 20)));

        result.Error.Should().BeNull();
        result.Text.Should().Be("# Brief\nBody.");
        result.InputTokens.Should().Be(6178);
        result.OutputTokens.Should().Be(3082);
        result.CacheReadTokens.Should().Be(10);
        result.CacheWriteTokens.Should().Be(20);
    }

    [Fact]
    public async Task CollectUtilityResult_DoneCarriesTheTextWhenNothingWasStreamed()
    {
        var result = await RunnerOperations.CollectUtilityResultAsync(Stream(
            new AgentChunk(AgentChunkType.Done, "final answer")));

        result.Text.Should().Be("final answer");
    }

    [Fact]
    public async Task CollectUtilityResult_InvokerThrow_BecomesTheError()
    {
        var result = await RunnerOperations.CollectUtilityResultAsync(Throwing());

        result.Error.Should().Be("boom");
        result.Text.Should().BeNull();
    }

    [Theory]
    [InlineData(ProcessError)]
    [InlineData("API Error: Connection closed mid-response. The response above may be incomplete.")]
    [InlineData("The claude CLI reported an execution error without details.")]
    public void ShouldRetryUtility_OtherFailures_True(string error)
    {
        RunnerOperations.ShouldRetryUtility(error).Should().BeTrue();
    }

    [Fact]
    public void ShouldRetryUtility_OutputTokenCap_False()
    {
        // Same prompt, same cap, same death — a retry only delays the host's fallback.
        RunnerOperations.ShouldRetryUtility(CapError).Should().BeFalse();
    }

    [Fact]
    public void ShouldRetryUtility_NoError_False()
    {
        RunnerOperations.ShouldRetryUtility(null).Should().BeFalse();
    }

    private static async IAsyncEnumerable<AgentChunk> Stream(params AgentChunk[] chunks)
    {
        foreach (var chunk in chunks)
        {
            await Task.Yield();
            yield return chunk;
        }
    }

    private static async IAsyncEnumerable<AgentChunk> Throwing()
    {
        await Task.Yield();
        throw new InvalidOperationException("boom");
#pragma warning disable CS0162 // unreachable — keeps this an iterator
        yield break;
#pragma warning restore CS0162
    }
}
