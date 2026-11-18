namespace Orkka.Tests.Worker;

using System.Text.Json;
using Orkka.Modules.Execution.Agents;
using FluentAssertions;
using ih0.Claude.Agent.SDK.Types;
using Microsoft.Extensions.Logging.Abstractions;

public sealed class AgentPdfReadGuardTests
{
    private static PreToolUseHookInput ReadInput(string filePath)
    {
        var toolInput = JsonSerializer.Deserialize<JsonElement>(
            JsonSerializer.Serialize(new { file_path = filePath }));
        return new PreToolUseHookInput
        {
            SessionId = "test-session",
            TranscriptPath = "/tmp/transcript",
            Cwd = "/repos/worktree",
            ToolName = "Read",
            ToolInput = toolInput,
        };
    }

    private static async Task<HookOutput> EvaluateAsync(string filePath) =>
        await new AgentPdfReadGuard(NullLogger.Instance)
            .EvaluateRead(ReadInput(filePath), toolUseId: null, context: null);

    [Theory]
    [InlineData("/repos/worktree/.orkka/context/attachments/00-overview.pdf")]
    [InlineData("spec.PDF")]
    public async Task EvaluateRead_DeniesPdf(string filePath)
    {
        // A PDF read makes the CLI stream a `document` content block, which the SDK's
        // message parser throws on — killing the run. Denying keeps the agent alive.
        var output = await EvaluateAsync(filePath);

        output.HookSpecificOutput!.Value.GetRawText().Should().Contain("deny");
        output.Decision.Should().Be("block");
    }

    [Theory]
    [InlineData("/repos/worktree/src/Program.cs")]
    [InlineData("/repos/worktree/.orkka/context/attachments/mockup.png")]
    [InlineData("notes.pdf.md")]
    public async Task EvaluateRead_AllowsEverythingElse(string filePath)
    {
        var output = await EvaluateAsync(filePath);

        output.HookSpecificOutput!.Value.GetRawText().Should().Contain("allow");
        output.Decision.Should().Be("approve");
    }

    [Fact]
    public async Task EvaluateRead_SendsTheAgentToTheExtractedText()
    {
        // The deny is only survivable because the text is offered elsewhere — pointing at the
        // .txt sibling is what keeps the PDF's contents reachable. Telling the agent to run
        // pdftotext (the old wording) sent it chasing a binary the sandbox may not have.
        var output = await EvaluateAsync("spec.pdf");

        output.Reason.Should().Contain(".txt");
        output.Reason.Should().Contain("Do not run pdftotext");
    }

    [Fact]
    public async Task EvaluateRead_NeverLeavesAFieldNull()
    {
        var output = await EvaluateAsync("spec.pdf");

        output.Continue.Should().NotBeNull();
        output.SuppressOutput.Should().NotBeNull();
        output.StopReason.Should().NotBeNull();
        output.SystemMessage.Should().NotBeNull();
        output.Reason.Should().NotBeNull();
    }
}
