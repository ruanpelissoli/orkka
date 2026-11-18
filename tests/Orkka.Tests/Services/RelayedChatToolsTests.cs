namespace Orkka.Tests.Services;

using System.Text.Json;
using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Runners.Contracts;
using Orkka.Runners.Server;

/// <summary>
/// Platform tools on a chat turn relayed to the user's own machine.
///
/// Before this, tools were cloud-only: a "my machine" user's agent was told to write
/// ```document JSON into its reply, and the host parsed it back out. On 2026-09-07 a
/// consolidation of 26 documents hit the model's 64K output ceiling in the middle of that
/// block and produced nothing — a tool call per document has no such ceiling. The turn now
/// carries its tool definitions to the runner, which forwards each call back to the server
/// the Worker holds.
/// </summary>
public sealed class RelayedChatToolsTests
{
    private const string Workspace = "orkka-chat://org/project";

    [Fact]
    public async Task A_capable_runner_gets_the_tool_definitions_and_answers_through_the_registry()
    {
        var registry = new ProjectToolRegistry();
        var link = Link(ChatToolsCapable);
        var invoker = NewInvoker(link, registry);

        invoker.SupportsChatTools.Should().BeTrue();

        var tools = new FakeChatToolServer();
        var (chunks, sent) = await RunAsync(invoker, link, tools);

        chunks.Should().ContainSingle(c => c.Type == ChatChunkType.Done);
        sent!.ProjectTools.Should().ContainSingle()
            .Which.Name.Should().Be("create_document");
        sent.ProjectTools![0].InputSchemaJson.Should().Contain("title");

        // The run's own id is the registry key — a chat turn has no task to key on.
        registry.TryGet(sent.RunId).Should().BeNull("the registration lives exactly as long as the stream");
    }

    [Fact]
    public async Task The_tool_call_a_runner_forwards_mid_run_reaches_this_turns_server()
    {
        var registry = new ProjectToolRegistry();
        var link = Link(ChatToolsCapable);
        var tools = new FakeChatToolServer();
        var invoker = NewInvoker(link, registry);

        // Registration is only valid while the run streams, so the assertion has to happen
        // from inside it — exactly when a real runner would forward its call.
        RunnerToolCallResult? answered = null;
        var (_, sent) = await RunAsync(invoker, link, tools, onFirstChunk: async runId =>
        {
            answered = await ProjectToolCalls.HandleAsync(
                registry,
                new RunnerToolCallRequest(runId, "create_document", """{"title":"Rules"}"""),
                callerOrgId: Org, NullLogger.Instance, CancellationToken.None);
        });

        sent!.ProjectTools.Should().NotBeNull();
        answered!.IsError.Should().BeFalse();
        answered.Text.Should().Be("saved Rules");
        tools.Calls.Should().ContainSingle().Which.Should().Be("create_document");
    }

    [Fact]
    public async Task Another_orgs_runner_cannot_reach_this_turns_tools()
    {
        var registry = new ProjectToolRegistry();
        var link = Link(ChatToolsCapable);
        var tools = new FakeChatToolServer();
        var invoker = NewInvoker(link, registry);

        RunnerToolCallResult? answered = null;
        await RunAsync(invoker, link, tools, onFirstChunk: async runId =>
        {
            answered = await ProjectToolCalls.HandleAsync(
                registry,
                new RunnerToolCallRequest(runId, "create_document", "{}"),
                callerOrgId: Ulid.NewUlid(), NullLogger.Instance, CancellationToken.None);
        });

        answered!.IsError.Should().BeTrue();
        tools.Calls.Should().BeEmpty("the run id is a lookup key, never an authorization");
    }

    [Fact]
    public async Task An_older_runner_gets_no_tool_definitions_and_the_turn_still_runs()
    {
        // The capability is the whole gate: advertising tools to a runner that cannot forward
        // the calls would teach the agent its tools are broken. Such a turn falls back to the
        // fenced-block prompt, which is what every relayed turn used to do.
        var registry = new ProjectToolRegistry();
        var link = Link(capabilities: [RunnerCapabilities.Hybrid]);
        var invoker = NewInvoker(link, registry);

        invoker.SupportsChatTools.Should().BeFalse();

        var (chunks, sent) = await RunAsync(invoker, link, new FakeChatToolServer());

        sent!.ProjectTools.Should().BeNull();
        chunks.Should().Contain(c => c.Type == ChatChunkType.Done);
    }

    [Fact]
    public void Presence_capabilities_decide_the_prompt_before_the_turn_is_built()
    {
        // ChatTurnPreparer reads this: the API pod holds no runner link, so the persisted
        // capability list is how it knows whether a relayed turn can carry tools.
        var capable = JsonSerializer.Serialize(new[] { RunnerCapabilities.Hybrid, RunnerCapabilities.ChatTools });

        RunnerPresenceRules.HasCapability(capable, RunnerCapabilities.ChatTools).Should().BeTrue();
        RunnerPresenceRules.HasCapability(
            JsonSerializer.Serialize(new[] { RunnerCapabilities.Hybrid }),
            RunnerCapabilities.ChatTools).Should().BeFalse();
        RunnerPresenceRules.HasCapability(null, RunnerCapabilities.ChatTools).Should().BeFalse();
        RunnerPresenceRules.HasCapability("not json", RunnerCapabilities.ChatTools)
            .Should().BeFalse("a malformed list must not be read as capable");
    }

    // ------------------------------------------------------------------ harness

    private static readonly Ulid Org = Ulid.NewUlid();

    private static readonly string[] ChatToolsCapable =
        [RunnerCapabilities.Hybrid, RunnerCapabilities.ProjectTools, RunnerCapabilities.ChatTools];

    private static RunnerChatInvoker NewInvoker(FakeLink link, ProjectToolRegistry registry) =>
        new(link.Object, NullLogger<RunnerChatInvoker>.Instance,
            toolRegistry: registry, orgId: Org);

    /// <summary>Drives one relayed turn. The fake link answers StartChatRun by feeding a Done
    /// chunk, so the invoker's stream completes the way a real runner's would; a hook runs
    /// while the stream is open, which is the only moment a forwarded tool call is valid.</summary>
    private static async Task<(List<ChatChunk> Chunks, RunnerChatRunRequest? Sent)> RunAsync(
        RunnerChatInvoker invoker, FakeLink link, IChatToolServer tools,
        Func<string, Task>? onFirstChunk = null)
    {
        var chunks = new List<ChatChunk>();
        await foreach (var chunk in invoker.InvokeAgentAsync(
            Workspace, "system", "consolidate the docs", "claude-sonnet-5",
            toolServer: tools))
        {
            if (onFirstChunk is not null && link.Sent is not null)
            {
                await onFirstChunk(link.Sent.RunId);
                onFirstChunk = null;
            }
            chunks.Add(chunk);
        }
        return (chunks, link.Sent);
    }

    private static FakeLink Link(string[] capabilities) => new(capabilities);

    /// <summary>A connected runner that accepts one chat run and immediately completes it,
    /// capturing what the host sent.</summary>
    private sealed class FakeLink
    {
        public FakeLink(string[] capabilities)
        {
            var hello = new RunnerHello(
                RunnerProtocol.ProtocolVersion, "TEST-PC", true, true, [],
                [new RunnerProviderStatus("claude", true, true)], capabilities);

            var channel = Channel.CreateUnbounded<RunnerChunk>();
            var mock = new Mock<IRunnerLink>();
            mock.SetupGet(l => l.IsConnected).Returns(true);
            mock.SetupGet(l => l.Status).Returns((true, hello));
            mock.Setup(l => l.GetProviderBlocker(It.IsAny<AiProvider>())).Returns((string?)null);
            mock.Setup(l => l.OpenRunChannel(It.IsAny<string>())).Returns(channel);
            mock.Setup(l => l.SendAsync(
                    RunnerProtocol.StartChatRun, It.IsAny<object?>(), It.IsAny<CancellationToken>()))
                .Callback((string _, object? arg, CancellationToken _) =>
                {
                    Sent = (RunnerChatRunRequest)arg!;
                    channel.Writer.TryWrite(new RunnerChunk("Done", ""));
                    channel.Writer.TryComplete();
                })
                .Returns(Task.CompletedTask);
            mock.Setup(l => l.ConsumeRunAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string _, CancellationToken ct) => channel.Reader.ReadAllAsync(ct));

            Object = mock.Object;
        }

        public IRunnerLink Object { get; }

        public RunnerChatRunRequest? Sent { get; private set; }
    }

    private sealed class FakeChatToolServer : IChatToolServer
    {
        public List<string> Calls { get; } = [];

        public string ServerName => "orkka";

        public IReadOnlyList<ChatToolDefinition> ListTools() =>
        [
            new("create_document", "Save a document",
                """{"type":"object","properties":{"title":{"type":"string"}}}"""),
        ];

        public Task<ChatToolResult> CallToolAsync(
            string toolName, JsonElement arguments, CancellationToken ct = default)
        {
            Calls.Add(toolName);
            var title = arguments.TryGetProperty("title", out var t) ? t.GetString() : "untitled";
            return Task.FromResult(new ChatToolResult($"saved {title}"));
        }
    }
}
