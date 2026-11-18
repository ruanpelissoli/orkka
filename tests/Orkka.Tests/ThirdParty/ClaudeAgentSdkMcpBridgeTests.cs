namespace Orkka.Tests.ThirdParty;

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using FluentAssertions;
using ih0.Claude.Agent.SDK;
using ih0.Claude.Agent.SDK.Internal;
using ih0.Claude.Agent.SDK.Internal.Transport;

/// <summary>
/// Pins the <c>[orkka patch]</c> in the vendored SDK's <see cref="QueryHandler"/> (see
/// src/ThirdParty/ih0.Claude.Agent.SDK/VENDORED.md). The Claude Code CLI drives SDK-hosted MCP
/// servers through <c>control_request</c>/<c>mcp_message</c> and awaits a <c>control_response</c>
/// for EVERY message — including the <c>notifications/initialized</c> notification it sends
/// right after <c>initialize</c>. A notification has no <c>id</c>; upstream 0.1.6 serialized a
/// default <see cref="JsonElement"/> for it, threw "Operation is not valid due to the current
/// state of the object", answered the CLI with an error response, and the CLI logged
/// "Failed to connect SDK MCP server" — leaving every run without its in-process tools.
/// These tests speak the CLI's side of the protocol against a QueryHandler over a fake transport.
/// </summary>
public sealed class ClaudeAgentSdkMcpBridgeTests
{
    [Fact]
    public async Task Notification_without_id_gets_success_control_response()
    {
        await using var harness = await Harness.StartAsync(new FakeToolServer());

        var init = await harness.SendMcpAsync(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"claude-code","version":"2.1.240"}}}""");
        init.GetProperty("subtype").GetString().Should().Be("success");
        init.GetProperty("response").GetProperty("mcp_response").GetProperty("result")
            .GetProperty("serverInfo").GetProperty("name").GetString().Should().Be("orkka");

        // The exact message the CLI sends after initialize — and the one that used to fail.
        var notified = await harness.SendMcpAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        notified.GetProperty("subtype").GetString().Should().Be("success",
            "an error control_response to the notification is what the CLI logs as 'Failed to connect SDK MCP server'");
        var mcp = notified.GetProperty("response").GetProperty("mcp_response");
        mcp.GetProperty("jsonrpc").GetString().Should().Be("2.0");
        mcp.GetProperty("id").ValueKind.Should().Be(JsonValueKind.Null);
        mcp.TryGetProperty("error", out _).Should().BeFalse();

        // And the server is actually usable afterwards — tools are listed.
        var list = await harness.SendMcpAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        list.GetProperty("subtype").GetString().Should().Be("success");
        list.GetProperty("response").GetProperty("mcp_response").GetProperty("result").GetProperty("tools")
            .EnumerateArray().Select(t => t.GetProperty("name").GetString())
            .Should().Equal("list_documents", "get_document");
    }

    [Fact]
    public async Task Tool_call_round_trips_through_the_in_process_server()
    {
        var server = new FakeToolServer();
        await using var harness = await Harness.StartAsync(server);

        var call = await harness.SendMcpAsync(
            """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"get_document","arguments":{"documentId":"abc"}}}""");

        call.GetProperty("subtype").GetString().Should().Be("success");
        var mcp = call.GetProperty("response").GetProperty("mcp_response");
        mcp.GetProperty("id").GetInt32().Should().Be(7);
        mcp.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()
            .Should().Be("get_document:abc");
        server.Calls.Should().ContainSingle().Which.Should().Be("get_document");
    }

    [Fact]
    public async Task Notification_for_unknown_server_is_a_jsonrpc_error_not_a_transport_error()
    {
        // The "server not found" branch built its id the same broken way.
        await using var harness = await Harness.StartAsync(new FakeToolServer());

        var response = await harness.SendMcpAsync(
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""", serverName: "nope");

        response.GetProperty("subtype").GetString().Should().Be("success");
        var mcp = response.GetProperty("response").GetProperty("mcp_response");
        mcp.GetProperty("id").ValueKind.Should().Be(JsonValueKind.Null);
        mcp.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32601);
    }

    /// <summary>A QueryHandler wired to an in-memory transport, driven from the CLI's side.</summary>
    private sealed class Harness(ChannelTransport transport, QueryHandler handler) : IAsyncDisposable
    {
        private int _requests;

        public static async Task<Harness> StartAsync(ISdkMcpServer server)
        {
            var transport = new ChannelTransport();
            await transport.ConnectAsync();
            var handler = new QueryHandler(
                transport,
                isStreamingMode: true,
                sdkMcpServers: new Dictionary<string, ISdkMcpServer> { [server.Name] = server });
            await handler.StartAsync();
            return new Harness(transport, handler);
        }

        /// <summary>Sends one control_request/mcp_message exactly as the CLI does and returns
        /// the matching control_response's <c>response</c> object.</summary>
        public async Task<JsonElement> SendMcpAsync(string mcpMessageJson, string serverName = "orkka")
        {
            var requestId = $"req_{Interlocked.Increment(ref _requests)}";
            var envelope = "{\"type\":\"control_request\",\"request_id\":\"" + requestId
                + "\",\"request\":{\"subtype\":\"mcp_message\",\"server_name\":\"" + serverName
                + "\",\"message\":" + mcpMessageJson + "}}";
            using var doc = JsonDocument.Parse(envelope);
            await transport.PushIncomingAsync(doc.RootElement.Clone());
            return await transport.WaitForControlResponseAsync(requestId, TimeSpan.FromSeconds(10));
        }

        public async ValueTask DisposeAsync()
        {
            transport.CompleteIncoming();
            await handler.DisposeAsync();
        }
    }

    private sealed class ChannelTransport : ITransport
    {
        private readonly Channel<JsonElement> _incoming = Channel.CreateUnbounded<JsonElement>();
        private readonly Channel<JsonElement> _outgoing = Channel.CreateUnbounded<JsonElement>();

        public bool IsReady { get; private set; }

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            IsReady = true;
            return Task.CompletedTask;
        }

        public ValueTask PushIncomingAsync(JsonElement message) => _incoming.Writer.WriteAsync(message);

        public void CompleteIncoming() => _incoming.Writer.TryComplete();

        public async IAsyncEnumerable<JsonElement> ReadMessagesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var message in _incoming.Reader.ReadAllAsync(cancellationToken))
                yield return message;
        }

        public async Task WriteAsync(string data, CancellationToken cancellationToken = default)
        {
            foreach (var line in data.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                using var doc = JsonDocument.Parse(line);
                await _outgoing.Writer.WriteAsync(doc.RootElement.Clone(), cancellationToken);
            }
        }

        public async Task<JsonElement> WaitForControlResponseAsync(string requestId, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            await foreach (var message in _outgoing.Reader.ReadAllAsync(cts.Token))
            {
                if (message.TryGetProperty("type", out var type) && type.GetString() == "control_response"
                    && message.TryGetProperty("response", out var response)
                    && response.TryGetProperty("request_id", out var id) && id.GetString() == requestId)
                    return response;
            }
            throw new InvalidOperationException($"No control_response for {requestId}.");
        }

        public Task EndInputAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task CloseAsync(CancellationToken cancellationToken = default)
        {
            IsReady = false;
            _incoming.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsReady = false;
            _incoming.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Shape of TaskPipelineToolServer: a named read-only server with a couple of tools.</summary>
    private sealed class FakeToolServer : ISdkMcpServer
    {
        public List<string> Calls { get; } = [];

        public string Name => "orkka";
        public string Version => "1.0.0";

        public Task<IReadOnlyList<SdkMcpToolDefinition>> ListToolsAsync(CancellationToken cancellationToken = default)
        {
            using var schema = JsonDocument.Parse("""{"type":"object","properties":{}}""");
            IReadOnlyList<SdkMcpToolDefinition> tools =
            [
                new() { Name = "list_documents", Description = "List documents", InputSchema = schema.RootElement.Clone() },
                new() { Name = "get_document", Description = "Get a document", InputSchema = schema.RootElement.Clone() },
            ];
            return Task.FromResult(tools);
        }

        public Task<SdkMcpToolResult> CallToolAsync(string name, JsonElement arguments, CancellationToken cancellationToken = default)
        {
            Calls.Add(name);
            var id = arguments.TryGetProperty("documentId", out var idEl) ? idEl.GetString() : "";
            return Task.FromResult(new SdkMcpToolResult
            {
                Content = [new SdkMcpTextContent { Text = $"{name}:{id}" }]
            });
        }
    }
}
