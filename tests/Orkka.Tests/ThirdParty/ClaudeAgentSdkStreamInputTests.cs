namespace Orkka.Tests.ThirdParty;

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using FluentAssertions;
using ih0.Claude.Agent.SDK.Internal;
using ih0.Claude.Agent.SDK.Internal.Transport;
using ih0.Claude.Agent.SDK.Types;

/// <summary>
/// Pins the <c>[orkka patch]</c> in the vendored SDK's <see cref="QueryHandler.StreamInputAsync"/>
/// (see src/ThirdParty/ih0.Claude.Agent.SDK/VENDORED.md, patch 2). stdin doubles as the
/// control-protocol response channel: every hook callback the CLI issues is answered over it.
/// Upstream streamed the prompt, waited for the run's first <c>result</c> message capped at
/// CLAUDE_CODE_STREAM_CLOSE_TIMEOUT (default 60s), and closed stdin when the cap fired — mid-run.
/// A pipeline run's only result arrives minutes in, so every hook-matched tool call after the cap
/// (Write/Edit/Read under Orkka's PreToolUse guards) could never receive its hook response and was
/// cancelled by the CLI ~30ms after the tool_use with the generic "The user doesn't want to take
/// this action right now" denial (toolDenialKind "cancelled") — written into the session
/// transcript, which a later --resume replayed to the model as a human refusal (DEN-5 parked a
/// task to ask whether creating files was forbidden). These tests drive the CLI's side of the
/// protocol against a QueryHandler over an in-memory transport.
/// </summary>
public sealed class ClaudeAgentSdkStreamInputTests
{
    [Fact]
    public async Task Stdin_stays_open_for_hook_callbacks_until_the_first_result()
    {
        // The legacy escape hatch must be dead, not merely defaulted long: with the cap set to
        // 50ms, upstream closed stdin before the 250ms probe below — the patched code ignores it.
        Environment.SetEnvironmentVariable("CLAUDE_CODE_STREAM_CLOSE_TIMEOUT", "50");
        try
        {
            var transport = new RecordingTransport();
            await transport.ConnectAsync();

            HookCallback allow = (_, _, _) => Task.FromResult(new HookOutput { Continue = true });
            var handler = new QueryHandler(
                transport,
                isStreamingMode: true,
                hooks: new Dictionary<string, List<HookMatcherConfig>>
                {
                    ["PreToolUse"] = [new HookMatcherConfig { Matcher = "Write", Hooks = [allow] }],
                });
            await using var _ = handler;
            await handler.StartAsync();

            // Answer the initialize control_request exactly as the CLI does, capturing the
            // callback id the handler registered for the PreToolUse hook.
            var initTask = handler.InitializeAsync();
            var init = await transport.NextOutgoingAsync(
                m => m.TryGetProperty("request", out var r)
                     && r.GetProperty("subtype").GetString() == "initialize");
            var callbackId = init.GetProperty("request").GetProperty("hooks")
                .GetProperty("PreToolUse")[0].GetProperty("hookCallbackIds")[0].GetString()!;
            await transport.PushIncomingAsync(ControlSuccess(init.GetProperty("request_id").GetString()!));
            await initTask.WaitAsync(TimeSpan.FromSeconds(10));

            var streamTask = handler.StreamInputAsync(OneUserMessage());
            await transport.NextOutgoingAsync(
                m => m.TryGetProperty("type", out var t) && t.GetString() == "user");

            // No result yet — long past the legacy 50ms cap, stdin must still be open...
            await Task.Delay(250);
            transport.EndInputCalled.Should().BeFalse(
                "closing stdin mid-run is what made the CLI cancel guarded tool calls with "
                + "'The user doesn't want to take this action right now'");

            // ...and prove it end-to-end: the hook round-trip the CLI performs on every guarded
            // tool call still completes, minutes-into-the-run style.
            await transport.PushIncomingAsync(HookCallbackRequest("req_hook_1", callbackId));
            var response = await transport.WaitForControlResponseAsync("req_hook_1", TimeSpan.FromSeconds(10));
            response.GetProperty("subtype").GetString().Should().Be("success");

            // The first (and only) result ends the run — NOW stdin closes so the CLI can exit.
            await transport.PushIncomingAsync(
                JsonSerializer.SerializeToElement(new { type = "result", subtype = "success" }));
            await streamTask.WaitAsync(TimeSpan.FromSeconds(10));
            transport.EndInputCalled.Should().BeTrue(
                "after the result the CLI is done and waits only for stdin EOF to exit");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_CODE_STREAM_CLOSE_TIMEOUT", null);
        }
    }

    [Fact]
    public async Task Cancelled_run_never_closes_stdin_from_the_input_streamer()
    {
        var transport = new RecordingTransport();
        await transport.ConnectAsync();

        HookCallback allow = (_, _, _) => Task.FromResult(new HookOutput { Continue = true });
        var handler = new QueryHandler(
            transport,
            isStreamingMode: true,
            hooks: new Dictionary<string, List<HookMatcherConfig>>
            {
                ["PreToolUse"] = [new HookMatcherConfig { Matcher = "Write", Hooks = [allow] }],
            });
        await using var _ = handler;
        await handler.StartAsync();

        using var runCts = new CancellationTokenSource();
        var streamTask = handler.StreamInputAsync(OneUserMessage(), runCts.Token);
        await transport.NextOutgoingAsync(
            m => m.TryGetProperty("type", out var t) && t.GetString() == "user");

        // Cancellation (wall-clock cap, link drop, worker drain) tears the whole transport
        // down elsewhere — the input streamer must not close stdin on its behalf, or a
        // still-live CLI observes EOF mid-tool-call and writes the "cancelled" denial into
        // the session transcript before the kill lands.
        runCts.Cancel();
        await streamTask.WaitAsync(TimeSpan.FromSeconds(10));
        transport.EndInputCalled.Should().BeFalse();
    }

    private static async IAsyncEnumerable<JsonElement> OneUserMessage()
    {
        yield return JsonSerializer.SerializeToElement(new
        {
            type = "user",
            message = new { role = "user", content = "do the task" },
        });
        await Task.CompletedTask;
    }

    private static JsonElement ControlSuccess(string requestId) =>
        JsonSerializer.SerializeToElement(new
        {
            type = "control_response",
            response = new { subtype = "success", request_id = requestId, response = new { } },
        });

    /// <summary>The control_request the CLI sends for a PreToolUse SDK hook.</summary>
    private static JsonElement HookCallbackRequest(string requestId, string callbackId) =>
        JsonSerializer.SerializeToElement(new
        {
            type = "control_request",
            request_id = requestId,
            request = new
            {
                subtype = "hook_callback",
                callback_id = callbackId,
                tool_use_id = "toolu_01",
                input = new Dictionary<string, object>
                {
                    ["hook_event_name"] = "PreToolUse",
                    ["session_id"] = "s",
                    ["transcript_path"] = "t",
                    ["cwd"] = "c",
                    ["tool_name"] = "Write",
                    ["tool_input"] = new { file_path = "a.ts", content = "x" },
                    ["tool_use_id"] = "toolu_01",
                },
            },
        });

    /// <summary>In-memory transport that records whether stdin was closed (EndInputAsync).</summary>
    private sealed class RecordingTransport : ITransport
    {
        private readonly Channel<JsonElement> _incoming = Channel.CreateUnbounded<JsonElement>();
        private readonly Channel<JsonElement> _outgoing = Channel.CreateUnbounded<JsonElement>();

        public bool IsReady { get; private set; }
        public volatile bool EndInputCalled;

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            IsReady = true;
            return Task.CompletedTask;
        }

        public ValueTask PushIncomingAsync(JsonElement message) => _incoming.Writer.WriteAsync(message);

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

        /// <summary>Reads outgoing messages until one matches; non-matching ones are consumed.</summary>
        public async Task<JsonElement> NextOutgoingAsync(Func<JsonElement, bool> match)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await foreach (var message in _outgoing.Reader.ReadAllAsync(cts.Token))
            {
                if (match(message))
                    return message;
            }
            throw new InvalidOperationException("No matching outgoing message.");
        }

        public async Task<JsonElement> WaitForControlResponseAsync(string requestId, TimeSpan timeout)
        {
            var response = await NextOutgoingAsync(m =>
                m.TryGetProperty("type", out var type) && type.GetString() == "control_response"
                && m.TryGetProperty("response", out var r)
                && r.TryGetProperty("request_id", out var id) && id.GetString() == requestId);
            return response.GetProperty("response");
        }

        public Task EndInputAsync(CancellationToken cancellationToken = default)
        {
            EndInputCalled = true;
            return Task.CompletedTask;
        }

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
}
