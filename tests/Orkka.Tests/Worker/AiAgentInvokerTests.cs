namespace Orkka.Tests.Worker;

using System.Runtime.CompilerServices;
using System.Text.Json;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Projects.Infrastructure;
using Orkka.Modules.Execution.Agents;
using FluentAssertions;
using ih0.Claude.Agent.SDK;
using ih0.Claude.Agent.SDK.Types;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

public sealed class ClaudeAgentInvokerTests
{
    private readonly Mock<IClaudeAgentClient> _clientMock = new();
    private readonly ClaudeAgentInvoker _invoker;

    public ClaudeAgentInvokerTests()
    {
        _invoker = new ClaudeAgentInvoker(
            _clientMock.Object,
            Options.Create(new AnthropicOptions()),
            Options.Create(new AgentLimitsOptions()),
            Options.Create(new AgentSandboxOptions()),
            Mock.Of<IAgentAuditLog>(),
            new DenyingAgentApprovalGate(),
            Mock.Of<ILogger<ClaudeAgentInvoker>>());
    }

    private static AgentRequest CreateRequest(
        AiProvider provider = AiProvider.Anthropic,
        string worktreePath = "/repos/worktree",
        AgentPhase phase = AgentPhase.Planning,
        string? model = null) => new(
        SystemPrompt: "You are a test agent",
        UserMessage: "Do the task",
        WorktreePath: worktreePath,
        Provider: provider,
        Model: model,
        Phase: phase);

    [Fact]
    public async Task InvokeAsync_NonAnthropicProvider_ReturnsError()
    {
        var request = CreateRequest(provider: (AiProvider)999);

        var chunks = await CollectChunks(request);

        chunks.Should().ContainSingle();
        chunks[0].Type.Should().Be(AgentChunkType.Error);
        chunks[0].Content.Should().Contain("Only Anthropic provider is supported");
    }

    [Fact]
    public async Task InvokeAsync_EmptyWorktreePath_ReturnsError()
    {
        var request = CreateRequest(worktreePath: "");

        var chunks = await CollectChunks(request);

        chunks.Should().ContainSingle();
        chunks[0].Type.Should().Be(AgentChunkType.Error);
        chunks[0].Content.Should().Contain("WorktreePath is required");
    }

    [Fact]
    public async Task InvokeAsync_TextBlock_MapsToTextChunk()
    {
        SetupClientReturning(
            CreateAssistantMessage(new TextBlock { Text = "Hello from agent" }));

        var chunks = await CollectChunks(CreateRequest());

        chunks.Should().Contain(c => c.Type == AgentChunkType.Text && c.Content == "Hello from agent");
    }

    [Fact]
    public async Task InvokeAsync_ThinkingBlock_MapsToThinkingChunk()
    {
        SetupClientReturning(
            CreateAssistantMessage(new ThinkingBlock { Thinking = "Let me think...", Signature = "" }));

        var chunks = await CollectChunks(CreateRequest());

        chunks.Should().Contain(c => c.Type == AgentChunkType.Thinking && c.Content == "Let me think...");
    }

    [Fact]
    public async Task InvokeAsync_ToolUseBlock_MapsToToolUseChunk()
    {
        var input = JsonDocument.Parse("""{"path": "src/main.cs"}""").RootElement;
        SetupClientReturning(
            CreateAssistantMessage(new ToolUseBlock { Id = "tool-1", Name = "Read", Input = input }));

        var chunks = await CollectChunks(CreateRequest());

        chunks.Should().Contain(c =>
            c.Type == AgentChunkType.ToolUse &&
            c.Content.Contains("Read") &&
            c.Content.Contains("src/main.cs"));
    }

    [Fact]
    public async Task InvokeAsync_ToolResultBlock_MapsToToolResultChunk()
    {
        var contentJson = JsonDocument.Parse("\"file contents here\"").RootElement;
        SetupClientReturning(
            CreateAssistantMessage(new ToolResultBlock
            {
                ToolUseId = "tool-1",
                Content = contentJson,
                IsError = false
            }));

        var chunks = await CollectChunks(CreateRequest());

        chunks.Should().Contain(c =>
            c.Type == AgentChunkType.ToolResult && c.Content.Contains("file contents here"));
    }

    [Fact]
    public async Task InvokeAsync_ResultMessageSuccess_MapsToDoneChunk()
    {
        SetupClientReturning(
            CreateResultMessage(subtype: "success", result: "Plan completed"));

        var chunks = await CollectChunks(CreateRequest());

        chunks.Should().Contain(c =>
            c.Type == AgentChunkType.Done && c.Content == "Plan completed");
    }

    [Fact]
    public async Task InvokeAsync_ResultMessageError_MapsToErrorAndDoneChunks()
    {
        SetupClientReturning(
            CreateResultMessage(subtype: "error", result: "Something went wrong", isError: true));

        var chunks = await CollectChunks(CreateRequest());

        chunks.Should().Contain(c => c.Type == AgentChunkType.Error && c.Content == "Something went wrong");
        chunks.Should().Contain(c => c.Type == AgentChunkType.Done);
    }

    [Fact]
    public async Task InvokeAsync_NoResultMessage_EmitsDoneChunk()
    {
        SetupClientReturning(
            CreateAssistantMessage(new TextBlock { Text = "Some output" }));

        var chunks = await CollectChunks(CreateRequest());

        chunks.Last().Type.Should().Be(AgentChunkType.Done);
    }

    [Fact]
    public async Task InvokeAsync_MultipleBlocks_ProcessedInOrder()
    {
        SetupClientReturning(
            CreateAssistantMessage(
                new ThinkingBlock { Thinking = "thinking...", Signature = "" },
                new TextBlock { Text = "Here is my plan" }),
            CreateResultMessage(subtype: "success", result: "Done"));

        var chunks = await CollectChunks(CreateRequest());

        var types = chunks.Select(c => c.Type).ToList();
        types.Should().ContainInOrder(
            AgentChunkType.Thinking,
            AgentChunkType.Text,
            AgentChunkType.Done);
    }

    [Fact]
    public async Task InvokeAsync_ExceedsToolCallLimit_StopsRun()
    {
        var invoker = new ClaudeAgentInvoker(
            _clientMock.Object,
            Options.Create(new AnthropicOptions()),
            Options.Create(new AgentLimitsOptions { MaxToolCalls = 2, MaxWallClockSeconds = 0 }),
            Options.Create(new AgentSandboxOptions()),
            Mock.Of<IAgentAuditLog>(),
            new DenyingAgentApprovalGate(),
            Mock.Of<ILogger<ClaudeAgentInvoker>>());

        var input = JsonDocument.Parse("""{"command":"ls"}""").RootElement;
        SetupClientReturning(
            CreateAssistantMessage(new ToolUseBlock { Id = "t1", Name = "Bash", Input = input }),
            CreateAssistantMessage(new ToolUseBlock { Id = "t2", Name = "Bash", Input = input }),
            CreateAssistantMessage(new ToolUseBlock { Id = "t3", Name = "Bash", Input = input }));

        var chunks = new List<AgentChunk>();
        await foreach (var chunk in invoker.InvokeAsync(CreateRequest()))
            chunks.Add(chunk);

        chunks.Should().Contain(c => c.Type == AgentChunkType.Error && c.Content.Contains("tool-call limit"));
        chunks.Last().Type.Should().Be(AgentChunkType.Done);
        // The third tool-use message is never processed — the run stops at the cap.
        chunks.Count(c => c.Type == AgentChunkType.ToolUse).Should().Be(2);
    }

    [Fact]
    public async Task InvokeAsync_RequestMaxToolCalls_OverridesGlobalLimit()
    {
        // Global limit is permissive; the per-run override (a heavy phase's budget would
        // normally raise it, but a low value here proves the request wins) is the effective cap.
        var invoker = new ClaudeAgentInvoker(
            _clientMock.Object,
            Options.Create(new AnthropicOptions()),
            Options.Create(new AgentLimitsOptions { MaxToolCalls = 100, MaxWallClockSeconds = 0 }),
            Options.Create(new AgentSandboxOptions()),
            Mock.Of<IAgentAuditLog>(),
            new DenyingAgentApprovalGate(),
            Mock.Of<ILogger<ClaudeAgentInvoker>>());

        var input = JsonDocument.Parse("""{"command":"ls"}""").RootElement;
        SetupClientReturning(
            CreateAssistantMessage(new ToolUseBlock { Id = "t1", Name = "Bash", Input = input }),
            CreateAssistantMessage(new ToolUseBlock { Id = "t2", Name = "Bash", Input = input }),
            CreateAssistantMessage(new ToolUseBlock { Id = "t3", Name = "Bash", Input = input }));

        var request = CreateRequest() with { MaxToolCalls = 2 };
        var chunks = new List<AgentChunk>();
        await foreach (var chunk in invoker.InvokeAsync(request))
            chunks.Add(chunk);

        chunks.Should().Contain(c => c.Type == AgentChunkType.Error && c.Content.Contains("2 tool-call limit"));
        chunks.Count(c => c.Type == AgentChunkType.ToolUse).Should().Be(2);
    }

    [Fact]
    public async Task InvokeAsync_NullRequestCaps_FallBackToGlobalLimit()
    {
        // A request that leaves the caps null (ad-hoc callers: chat, utility) must still be
        // bounded by the process-wide default rather than running uncapped.
        var invoker = new ClaudeAgentInvoker(
            _clientMock.Object,
            Options.Create(new AnthropicOptions()),
            Options.Create(new AgentLimitsOptions { MaxToolCalls = 1, MaxWallClockSeconds = 0 }),
            Options.Create(new AgentSandboxOptions()),
            Mock.Of<IAgentAuditLog>(),
            new DenyingAgentApprovalGate(),
            Mock.Of<ILogger<ClaudeAgentInvoker>>());

        var input = JsonDocument.Parse("""{"command":"ls"}""").RootElement;
        SetupClientReturning(
            CreateAssistantMessage(new ToolUseBlock { Id = "t1", Name = "Bash", Input = input }),
            CreateAssistantMessage(new ToolUseBlock { Id = "t2", Name = "Bash", Input = input }));

        var request = CreateRequest();
        request.MaxToolCalls.Should().BeNull();

        var chunks = new List<AgentChunk>();
        await foreach (var chunk in invoker.InvokeAsync(request))
            chunks.Add(chunk);

        chunks.Should().Contain(c => c.Type == AgentChunkType.Error && c.Content.Contains("1 tool-call limit"));
        chunks.Count(c => c.Type == AgentChunkType.ToolUse).Should().Be(1);
    }

    [Fact]
    public async Task InvokeAsync_InitializeControlTimeout_ReturnsActionableError()
    {
        // The CLI spawned but never completed the stream-json control-protocol handshake
        // (outdated CLI, incomplete onboarding, hung process) — the SDK surfaces this as
        // "Control request timeout: initialize".
        SetupClientThrowing(
            new ih0.Claude.Agent.SDK.Exceptions.ClaudeAgentException("Control request timeout: initialize"));

        var chunks = await CollectChunks(CreateRequest());

        var error = chunks.Should().ContainSingle(c => c.Type == AgentChunkType.Error).Subject;
        error.Content.Should().Contain("did not respond (initialize timeout)");
        error.Content.Should().Contain("claude -p");
        error.Content.Should().Contain("npm i -g @anthropic-ai/claude-code");
        error.Content.Should().NotContain("Control request timeout");
        chunks.Last().Type.Should().Be(AgentChunkType.Done);
    }

    [Fact]
    public async Task InvokeAsync_UnrecognizedClaudeAgentException_KeepsGenericProcessError()
    {
        SetupClientThrowing(
            new ih0.Claude.Agent.SDK.Exceptions.ClaudeAgentException("exit code 1"));

        var chunks = await CollectChunks(CreateRequest());

        var error = chunks.Should().ContainSingle(c => c.Type == AgentChunkType.Error).Subject;
        error.Content.Should().Contain("Agent process failed: exit code 1");
        chunks.Last().Type.Should().Be(AgentChunkType.Done);
    }

    [Fact]
    public void AgentRequest_DefaultValues_AreCorrect()
    {
        var request = new AgentRequest(
            SystemPrompt: "test",
            UserMessage: "test",
            WorktreePath: "/tmp",
            Provider: AiProvider.Anthropic);

        request.Model.Should().BeNull();
        request.MaxTokens.Should().Be(4096);
        request.MaxTurns.Should().Be(25);
        request.Phase.Should().Be(AgentPhase.Planning);
    }

    [Fact]
    public void AgentChunk_DefaultTokens_AreZero()
    {
        var chunk = new AgentChunk(AgentChunkType.Text, "Hello");

        chunk.PromptTokens.Should().Be(0);
        chunk.CompletionTokens.Should().Be(0);
        chunk.SessionId.Should().BeNull();
    }

    [Fact]
    public async Task InvokeAsync_ResumeSessionId_SetsResumeOnOptions()
    {
        ClaudeAgentOptions? captured = null;
        _clientMock
            .Setup(c => c.QueryAsync(
                It.IsAny<string>(), It.IsAny<ClaudeAgentOptions>(), It.IsAny<CancellationToken>()))
            .Callback<string, ClaudeAgentOptions, CancellationToken>((_, options, _) => captured = options)
            .Returns(ToAsyncEnumerable([CreateResultMessage()]));

        await CollectChunks(CreateRequest() with { ResumeSessionId = "sess-abc" });

        captured.Should().NotBeNull();
        captured!.Resume.Should().Be("sess-abc");
    }

    [Fact]
    public async Task InvokeAsync_NoResumeSessionId_LeavesResumeUnset()
    {
        ClaudeAgentOptions? captured = null;
        _clientMock
            .Setup(c => c.QueryAsync(
                It.IsAny<string>(), It.IsAny<ClaudeAgentOptions>(), It.IsAny<CancellationToken>()))
            .Callback<string, ClaudeAgentOptions, CancellationToken>((_, options, _) => captured = options)
            .Returns(ToAsyncEnumerable([CreateResultMessage()]));

        await CollectChunks(CreateRequest());

        captured.Should().NotBeNull();
        captured!.Resume.Should().BeNull();
    }

    [Fact]
    public async Task InvokeAsync_ZeroMaxTurns_LeavesTurnCapUnset()
    {
        // Pipeline phases ship with MaxTurns = 0 (uncapped). That must reach the CLI as
        // "no --max-turns" — a 0 forwarded verbatim would end the run before it started.
        ClaudeAgentOptions? captured = null;
        _clientMock
            .Setup(c => c.QueryAsync(
                It.IsAny<string>(), It.IsAny<ClaudeAgentOptions>(), It.IsAny<CancellationToken>()))
            .Callback<string, ClaudeAgentOptions, CancellationToken>((_, options, _) => captured = options)
            .Returns(ToAsyncEnumerable([CreateResultMessage()]));

        await CollectChunks(CreateRequest() with { MaxTurns = 0 });

        captured.Should().NotBeNull();
        captured!.MaxTurns.Should().BeNull();
    }

    [Fact]
    public async Task InvokeAsync_PositiveMaxTurns_SetsTurnCap()
    {
        ClaudeAgentOptions? captured = null;
        _clientMock
            .Setup(c => c.QueryAsync(
                It.IsAny<string>(), It.IsAny<ClaudeAgentOptions>(), It.IsAny<CancellationToken>()))
            .Callback<string, ClaudeAgentOptions, CancellationToken>((_, options, _) => captured = options)
            .Returns(ToAsyncEnumerable([CreateResultMessage()]));

        await CollectChunks(CreateRequest() with { MaxTurns = 7 });

        captured.Should().NotBeNull();
        captured!.MaxTurns.Should().Be(7);
    }

    [Fact]
    public async Task InvokeAsync_ZeroMaxToolCalls_RunsUncapped()
    {
        // The cap that killed real runs ("reached the 40 tool-call limit"): with the phase
        // budget's 0 the run must stream every tool call through to completion.
        var invoker = new ClaudeAgentInvoker(
            _clientMock.Object,
            Options.Create(new AnthropicOptions()),
            Options.Create(new AgentLimitsOptions { MaxToolCalls = 2, MaxWallClockSeconds = 0 }),
            Options.Create(new AgentSandboxOptions()),
            Mock.Of<IAgentAuditLog>(),
            new DenyingAgentApprovalGate(),
            Mock.Of<ILogger<ClaudeAgentInvoker>>());

        var input = JsonDocument.Parse("""{"command":"ls"}""").RootElement;
        SetupClientReturning(
            CreateAssistantMessage(new ToolUseBlock { Id = "t1", Name = "Bash", Input = input }),
            CreateAssistantMessage(new ToolUseBlock { Id = "t2", Name = "Bash", Input = input }),
            CreateAssistantMessage(new ToolUseBlock { Id = "t3", Name = "Bash", Input = input }));

        var chunks = new List<AgentChunk>();
        await foreach (var chunk in invoker.InvokeAsync(CreateRequest() with { MaxToolCalls = 0 }))
            chunks.Add(chunk);

        chunks.Should().NotContain(c => c.Type == AgentChunkType.Error);
        chunks.Count(c => c.Type == AgentChunkType.ToolUse).Should().Be(3);
    }

    [Fact]
    public async Task InvokeAsync_DoneChunk_CarriesSessionId()
    {
        // The session id from the CLI's result message must surface on the Done chunk —
        // agent chat persists it to resume the warm session on the next turn.
        SetupClientReturning(CreateResultMessage(subtype: "success", result: "ok"));

        var chunks = await CollectChunks(CreateRequest());

        chunks.Should().Contain(c => c.Type == AgentChunkType.Done && c.SessionId == "test-session");
    }

    [Fact]
    public async Task InvokeAsync_InitReportsToolServerFailed_LogsError()
    {
        // Production symptom: the CLI's mcp-logs said "Failed to connect SDK MCP server" and
        // the agent ran without a single mcp__orkka__* tool, while the worker log showed
        // nothing. The CLI's init event is the only host-side evidence — it must be logged.
        var loggerMock = new Mock<ILogger<ClaudeAgentInvoker>>();
        var invoker = CreateInvoker(loggerMock.Object);
        SetupClientReturning(
            CreateInitMessage("""{"mcp_servers":[{"name":"orkka","status":"failed"}]}"""),
            CreateResultMessage(subtype: "success", result: "ok"));

        var chunks = new List<AgentChunk>();
        await foreach (var chunk in invoker.InvokeAsync(CreateRequest() with { ToolServer = new FakeSdkServer() }))
            chunks.Add(chunk);

        // Logged, never fatal — the run still produced its result.
        chunks.Should().NotContain(c => c.Type == AgentChunkType.Error);
        loggerMock.Verify(l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("'orkka'") && v.ToString()!.Contains("failed")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_InitReportsToolServerConnected_DoesNotLogError()
    {
        var loggerMock = new Mock<ILogger<ClaudeAgentInvoker>>();
        var invoker = CreateInvoker(loggerMock.Object);
        SetupClientReturning(
            CreateInitMessage("""{"mcp_servers":[{"name":"orkka","status":"connected"}]}"""),
            CreateResultMessage(subtype: "success", result: "ok"));

        await foreach (var _ in invoker.InvokeAsync(CreateRequest() with { ToolServer = new FakeSdkServer() })) { }

        loggerMock.Verify(l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    [Fact]
    public async Task InvokeAsync_InitOmitsRegisteredToolServer_LogsError()
    {
        // A server the host registered but the CLI never even listed is just as dead.
        var loggerMock = new Mock<ILogger<ClaudeAgentInvoker>>();
        var invoker = CreateInvoker(loggerMock.Object);
        SetupClientReturning(
            CreateInitMessage("""{"mcp_servers":[]}"""),
            CreateResultMessage(subtype: "success", result: "ok"));

        await foreach (var _ in invoker.InvokeAsync(CreateRequest() with { ToolServer = new FakeSdkServer() })) { }

        loggerMock.Verify(l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("'orkka'") && v.ToString()!.Contains("missing")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    private ClaudeAgentInvoker CreateInvoker(ILogger<ClaudeAgentInvoker> logger) => new(
        _clientMock.Object,
        Options.Create(new AnthropicOptions()),
        Options.Create(new AgentLimitsOptions()),
        Options.Create(new AgentSandboxOptions()),
        Mock.Of<IAgentAuditLog>(),
        new DenyingAgentApprovalGate(),
        logger);

    private static SystemMessage CreateInitMessage(string dataJson) => new()
    {
        Subtype = "init",
        Data = JsonDocument.Parse(dataJson).RootElement.Clone()
    };

    /// <summary>Stand-in for the pipeline's TaskPipelineToolServer — only the name matters here.</summary>
    private sealed class FakeSdkServer : ISdkMcpServer
    {
        public string Name => "orkka";
        public string Version => "1.0.0";

        public Task<IReadOnlyList<SdkMcpToolDefinition>> ListToolsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SdkMcpToolDefinition>>([]);

        public Task<SdkMcpToolResult> CallToolAsync(string name, JsonElement arguments, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SdkMcpToolResult { Content = [] });
    }

    private void SetupClientThrowing(Exception exception)
    {
        _clientMock
            .Setup(c => c.QueryAsync(
                It.IsAny<string>(),
                It.IsAny<ClaudeAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(ThrowingAsyncEnumerable(exception));
    }

    private static async IAsyncEnumerable<Message> ThrowingAsyncEnumerable(
        Exception exception, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        throw exception;
#pragma warning disable CS0162 // yield makes this an iterator; the throw above always fires first
        yield break;
#pragma warning restore CS0162
    }

    private void SetupClientReturning(params Message[] messages)
    {
        _clientMock
            .Setup(c => c.QueryAsync(
                It.IsAny<string>(),
                It.IsAny<ClaudeAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(ToAsyncEnumerable(messages));
    }

    private static ResultMessage CreateResultMessage(
        string subtype = "success", string result = "", bool isError = false, int durationMs = 1000) =>
        new()
        {
            Subtype = subtype,
            Result = result,
            IsError = isError,
            DurationMs = durationMs,
            DurationApiMs = 0,
            NumTurns = 1,
            SessionId = "test-session"
        };

    private static AssistantMessage CreateAssistantMessage(params ContentBlock[] blocks)
    {
        return new AssistantMessage
        {
            Content = blocks.ToList().AsReadOnly(),
            Model = "claude-sonnet-4-20250514"
        };
    }

    private async Task<List<AgentChunk>> CollectChunks(AgentRequest request)
    {
        var chunks = new List<AgentChunk>();
        await foreach (var chunk in _invoker.InvokeAsync(request))
        {
            chunks.Add(chunk);
        }
        return chunks;
    }

    private static async IAsyncEnumerable<Message> ToAsyncEnumerable(
        Message[] messages, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var message in messages)
        {
            ct.ThrowIfCancellationRequested();
            yield return message;
        }
        await Task.CompletedTask;
    }
}
