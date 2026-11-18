namespace Orkka.Tests.Features.Agents;

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Execution.Agents;

/// <summary>
/// The vendor-CLI pre-flight: a run whose CLI cannot be resolved must fail before any
/// process spawn, and the message must say WHY the run routed to this provider (the model)
/// and what fixes it (install command, or switch the model) — not cmd.exe's raw
/// "'copilot' is not recognized as an internal or external command" (prod, 2026-08-24).
/// </summary>
public sealed class CliInvokerPreflightTests : IDisposable
{
    private sealed class NoopAuditLog : IAgentAuditLog
    {
        public Task RecordAsync(AgentAuditRecord record, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    // Swapped rather than blanking the real PATH: the resolver also scans ~/.local/bin, so
    // a dev machine with the CLI genuinely installed would otherwise pass the gate.
    public CliInvokerPreflightTests() =>
        CliAgentInvokerBase.ResolveCli = _ => null;

    public void Dispose() =>
        CliAgentInvokerBase.ResolveCli = Orkka.SharedKernel.Ai.ProviderCliResolver.Resolve;

    [Fact]
    public async Task Invoke_FailsBeforeSpawn_NamingModelAndRemedy_WhenTheCliIsUnresolvable()
    {
        var invoker = new CopilotAgentInvoker(
            Options.Create(new AgentLimitsOptions()),
            new NoopAuditLog(),
            NullLogger<CopilotAgentInvoker>.Instance);

        var request = new AgentRequest(
            SystemPrompt: "s", UserMessage: "u", WorktreePath: Path.GetTempPath(),
            Provider: AiProvider.GitHubCopilot,
            Model: "copilot/claude-sonnet-5", AgentName: "Atlas");

        var chunks = new List<AgentChunk>();
        await foreach (var chunk in invoker.InvokeAsync(request))
            chunks.Add(chunk);

        chunks.Select(c => c.Type).Should().Equal(AgentChunkType.Error, AgentChunkType.Done);

        var message = chunks[0].Content;
        message.Should().Contain("copilot CLI is not installed");
        message.Should().Contain("copilot/claude-sonnet-5"); // why the run routed here
        message.Should().Contain("Atlas");
        message.Should().Contain("npm install -g @github/copilot");
        message.Should().Contain("switch the agent's model");
    }

    [Fact]
    public async Task Invoke_StillRejectsProviderMismatch_BeforeTheCliCheck()
    {
        var invoker = new CodexAgentInvoker(
            Options.Create(new AgentLimitsOptions()),
            new NoopAuditLog(),
            NullLogger<CodexAgentInvoker>.Instance);

        var request = new AgentRequest(
            SystemPrompt: "s", UserMessage: "u", WorktreePath: Path.GetTempPath(),
            Provider: AiProvider.Anthropic);

        var chunks = new List<AgentChunk>();
        await foreach (var chunk in invoker.InvokeAsync(request))
            chunks.Add(chunk);

        chunks[0].Type.Should().Be(AgentChunkType.Error);
        chunks[0].Content.Should().Contain("Only the OpenAI provider is supported");
    }

    // ── run-time auth feedback ─────────────────────────────────────────────────────

    private sealed class RecordingFeedback : IProviderAuthFeedback
    {
        public readonly List<(AiProvider Provider, string Detail)> Failures = [];
        public readonly List<AiProvider> Successes = [];

        public void ReportAuthFailure(AiProvider provider, string detail) => Failures.Add((provider, detail));
        public void ReportSuccess(AiProvider provider) => Successes.Add(provider);
    }

    /// <summary>A copilot-shaped invoker whose "CLI" never spawns: the attempt outcome is
    /// scripted, so the engine's post-run classification is exercised on its own.</summary>
    private sealed class ScriptedInvoker(
        IProviderAuthFeedback feedback,
        Action<CliAgentInvokerBase.CliRunState, CliAgentInvokerBase.CliRunOutcome> script)
        : CliAgentInvokerBase(new AgentLimitsOptions(), new NoopAuditLog(), NullLogger.Instance, feedback)
    {
        protected override AiProvider Provider => AiProvider.GitHubCopilot;
        protected override string CliName => "copilot";
        protected override string DefaultModel => "copilot/auto";

        protected override System.Diagnostics.ProcessStartInfo BuildStartInfo(AgentRequest request, string model)
            => throw new NotSupportedException("scripted — nothing is spawned");
        protected override string BuildStdin(AgentRequest request) => "";
        protected internal override IEnumerable<AgentChunk> MapLine(string line, CliRunState state) => [];

#pragma warning disable CS1998 // scripted: no awaits, but the signature is an async iterator
        protected override async IAsyncEnumerable<AgentChunk> RunAttemptsAsync(
            AgentRequest request, string model, CliRunState state, CliRunOutcome outcome,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            script(state, outcome);
            yield break;
        }
#pragma warning restore CS1998
    }

    private static AgentRequest CopilotRequest() => new(
        SystemPrompt: "s", UserMessage: "u", WorktreePath: Path.GetTempPath(),
        Provider: AiProvider.GitHubCopilot, Model: "copilot/auto", AgentName: "Nova");

    [Fact]
    public async Task Invoke_AuthShapedExit_ReportsAuthFailure_AndRewritesTheError()
    {
        CliAgentInvokerBase.ResolveCli = _ => "copilot"; // past the pre-flight, into the run
        var feedback = new RecordingFeedback();
        var invoker = new ScriptedInvoker(feedback, (_, outcome) =>
        {
            outcome.ProcessFailed = true;
            outcome.ExitCode = 1;
            outcome.Stderr = "Error: You are not logged in. Run `copilot login` first.";
            outcome.FailureMessage =
                "copilot CLI exited with code 1.\n--- copilot CLI stderr ---\n" + outcome.Stderr;
        });

        var chunks = new List<AgentChunk>();
        await foreach (var chunk in invoker.InvokeAsync(CopilotRequest()))
            chunks.Add(chunk);

        chunks.Select(c => c.Type).Should().Equal(AgentChunkType.Error, AgentChunkType.Done);
        chunks[0].Content.Should().Be(
            "copilot refused this run because it isn't signed in on this machine "
            + "(Error: You are not logged in. Run `copilot login` first.). "
            + "Run `copilot login` there, then retry.");
        chunks[0].Content.Should().NotContain("exited with code", "the exit-code framing is replaced, not prefixed");

        feedback.Failures.Should().ContainSingle()
            .Which.Should().Be((AiProvider.GitHubCopilot, chunks[0].Content));
        feedback.Successes.Should().BeEmpty();
    }

    [Fact]
    public async Task Invoke_NonAuthExit_KeepsTheExitMessage_AndReportsNothing()
    {
        CliAgentInvokerBase.ResolveCli = _ => "copilot";
        var feedback = new RecordingFeedback();
        var invoker = new ScriptedInvoker(feedback, (_, outcome) =>
        {
            outcome.ProcessFailed = true;
            outcome.ExitCode = 1;
            outcome.Stderr = "read ECONNRESET";
            outcome.FailureMessage = "copilot CLI exited with code 1.\n--- copilot CLI stderr ---\nread ECONNRESET";
        });

        var chunks = new List<AgentChunk>();
        await foreach (var chunk in invoker.InvokeAsync(CopilotRequest()))
            chunks.Add(chunk);

        chunks[0].Content.Should().StartWith("copilot CLI exited with code 1.");
        feedback.Failures.Should().BeEmpty("a dropped connection must not lock the provider out");
        feedback.Successes.Should().BeEmpty("a failed run is no proof the login works");
    }

    [Fact]
    public async Task Invoke_AuthShapedStreamError_IsRewrittenToo()
    {
        CliAgentInvokerBase.ResolveCli = _ => "copilot";
        var feedback = new RecordingFeedback();
        var invoker = new ScriptedInvoker(feedback, (state, _) =>
            state.StreamError = "GitHub authentication failed: Bad credentials");

        var chunks = new List<AgentChunk>();
        await foreach (var chunk in invoker.InvokeAsync(CopilotRequest()))
            chunks.Add(chunk);

        chunks.Select(c => c.Type).Should().Equal(AgentChunkType.Error, AgentChunkType.Done);
        chunks[0].Content.Should().Contain("Run `copilot login` there, then retry.");
        feedback.Failures.Should().ContainSingle();
        feedback.Successes.Should().BeEmpty();
    }

    [Fact]
    public async Task Invoke_CleanCompletion_ReportsSuccess()
    {
        CliAgentInvokerBase.ResolveCli = _ => "copilot";
        var feedback = new RecordingFeedback();
        var invoker = new ScriptedInvoker(feedback, (state, _) => state.FinalText = "done");

        var chunks = new List<AgentChunk>();
        await foreach (var chunk in invoker.InvokeAsync(CopilotRequest()))
            chunks.Add(chunk);

        chunks.Select(c => c.Type).Should().Equal(AgentChunkType.Done);
        feedback.Successes.Should().Equal(AiProvider.GitHubCopilot);
        feedback.Failures.Should().BeEmpty();
    }
}
