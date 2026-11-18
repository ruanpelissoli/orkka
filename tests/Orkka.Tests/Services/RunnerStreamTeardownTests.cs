namespace Orkka.Tests.Services;

using System.Collections.Concurrent;
using System.Threading.Channels;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orkka.LocalRunner.Contracts;
using Orkka.Runners.Server;

/// <summary>
/// A client-to-server chunk stream normally ends by the consumer going away — the run was
/// torn down, or the runner's connection dropped. Both used to escape
/// <c>StreamRunChunks</c> and be logged by SignalR's <c>DefaultHubDispatcher</c> as
/// "Failed to invoke hub method", at Error: in production that benign teardown was 100% of
/// the Error-level volume of both worker pods, which is exactly how a real error gets missed.
///
/// These tests pin the distinction: an ordinary teardown ends the stream quietly, an actual
/// fault inside the stream still reaches the dispatcher as an Error.
/// </summary>
public sealed class RunnerStreamTeardownTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    /// <summary>How long an Error is given to show up before "quietly" is believed.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);

    private readonly LogCapture _logs = new();
    private TestServer _server = null!;
    private RunnerLink _link = null!;

    public Task InitializeAsync()
    {
        _server = new TestServer(new WebHostBuilder()
            .ConfigureLogging(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Debug);
                builder.AddProvider(_logs);
            })
            .ConfigureServices(services =>
            {
                services.AddSignalR();
                services.AddSingleton(sp => new RunnerLink(
                    sp.GetRequiredService<IHubContext<TeardownProbeHub>>().Clients,
                    sp.GetRequiredService<ILogger<RunnerLink>>(),
                    new RunnerLinkMessages("Runner offline", "Runner disconnected mid-run")));
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseEndpoints(endpoints => endpoints.MapHub<TeardownProbeHub>("/hub"));
            }));

        _link = _server.Services.GetRequiredService<RunnerLink>();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _server.Dispose();
        return Task.CompletedTask;
    }

    private async Task<HubConnection> ConnectAsync()
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_server.BaseAddress, "hub"), options =>
            {
                options.HttpMessageHandlerFactory = _ => _server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();
        await connection.StartAsync();
        return connection;
    }

    [Fact]
    public async Task ConsumerTearsTheRunDownMidStream_EndsQuietly()
    {
        const string runId = "run-consumer-gone";
        var consumer = Consume(_link.OpenRunChannel(runId));
        await using var connection = await ConnectAsync();

        await connection.SendAsync(RunnerProtocol.StreamRunChunks, runId, SlowProducer());
        await consumer.WaitForChunksAsync(atLeast: 2, Wait);

        // What RunnerLink.CloseRunChannel / OnDisconnected do when the browser session
        // closes or the run is cancelled while the runner is still uploading.
        _link.CloseRunChannel(runId);

        await TeardownProbeHub.WaitForDoneAsync(runId, Wait);
        TeardownProbeHub.FailureOf(runId).Should().BeNull();
        (await DispatcherErrorsAsync(runId, Settle)).Should().BeEmpty(
            "an ordinary consumer teardown is not a hub-method failure");
        _logs.Records.Should().Contain(
            r => r.Level == LogLevel.Debug && r.Message.Contains(runId) && r.Message.Contains("consumer gone"),
            "the teardown stays traceable, just not at Error");
    }

    [Fact]
    public async Task RunnerConnectionDropsMidStream_EndsQuietly()
    {
        const string runId = "run-connection-closed";
        var consumer = Consume(_link.OpenRunChannel(runId));
        var connection = await ConnectAsync();

        await connection.SendAsync(RunnerProtocol.StreamRunChunks, runId, SlowProducer());
        await consumer.WaitForChunksAsync(atLeast: 2, Wait);

        // The runner's process died / the laptop lid closed: SignalR aborts the connection
        // and completes the hub method's stream with an OperationCanceledException.
        await connection.DisposeAsync();

        await TeardownProbeHub.WaitForDoneAsync(runId, Wait);
        TeardownProbeHub.FailureOf(runId).Should().BeNull();
        (await DispatcherErrorsAsync(runId, Settle)).Should().BeEmpty(
            "a dropped connection is already reported by RunnerLink at Warning");
        _logs.Records.Should().Contain(
            r => r.Level == LogLevel.Debug && r.Message.Contains(runId) && r.Message.Contains("connection closed"),
            "this is the cancellation path, not the closed-channel one — the guard on "
            + "Context.ConnectionAborted is what keeps it quiet");
    }

    [Fact]
    public async Task AFaultInsideTheStream_StillSurfacesAsAnError()
    {
        const string runId = "run-faulted";
        Consume(_link.OpenRunChannel(runId));
        await using var connection = await ConnectAsync();

        await connection.SendAsync(RunnerProtocol.StreamRunChunks, runId, FaultingProducer());

        await TeardownProbeHub.WaitForDoneAsync(runId, Wait);
        TeardownProbeHub.FailureOf(runId).Should().NotBeNull("the fault must leave the hub method");
        var errors = await DispatcherErrorsAsync(runId, Wait);
        errors.Should().NotBeEmpty("a genuine fault must still reach the dispatcher as an Error");
        errors[0].Message.Should().Contain("the runner blew up mid-upload");
    }

    [Fact]
    public async Task DrainAsync_RethrowsCancellation_WhenTheConnectionIsStillAlive()
    {
        // Not every OperationCanceledException is a disconnect — only the ones the aborted
        // connection explains are swallowed.
        var channel = Channel.CreateUnbounded<RunnerChunk>();
        using var factory = LoggerFactory.Create(_ => { });

        var act = async () => await RunnerChunkPump.DrainAsync(
            "run-live", Throwing(new OperationCanceledException()), channel,
            CancellationToken.None, factory.CreateLogger("test"));

        await act.Should().ThrowAsync<OperationCanceledException>();
        channel.Reader.Completion.IsCompleted.Should().BeTrue("the run's channel closes either way");
    }

    [Fact]
    public async Task DrainAsync_UploadCancelledByTheRunner_EndsQuietly()
    {
        // The normal end of a relayed chat turn: the consumer finished first, the run was told
        // to stop, and the runner cancelled its upload instead of completing it. SignalR
        // reports that as a HubException — it must not become the Worker's Error line.
        var channel = Channel.CreateUnbounded<RunnerChunk>();
        using var factory = LoggerFactory.Create(_ => { });

        var act = async () => await RunnerChunkPump.DrainAsync(
            "run-done", Throwing(new HubException("Stream canceled by client.")), channel,
            CancellationToken.None, factory.CreateLogger("test"));

        await act.Should().NotThrowAsync();
        channel.Reader.Completion.IsCompleted.Should().BeTrue();
    }

    /// <summary>
    /// The dispatcher logs its verdict after the hub method's task settles, off the message
    /// loop — so "no Error" has to be given a moment to be wrong.
    /// </summary>
    private async Task<IReadOnlyList<LogRecord>> DispatcherErrorsAsync(string runId, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;
        while (DateTime.UtcNow < deadline && DispatcherErrorsFor(runId).Count == 0)
            await Task.Delay(25);
        return DispatcherErrorsFor(runId);
    }

    private IReadOnlyList<LogRecord> DispatcherErrorsFor(string runId) =>
        [.. _logs.Records.Where(r =>
            r.Level == LogLevel.Error
            && (r.Message.Contains(RunnerProtocol.StreamRunChunks) || r.Message.Contains(runId)))];

    /// <summary>Consumes the run's channel the way the proxy does, counting as it goes.</summary>
    private static Consumer Consume(Channel<RunnerChunk> channel)
    {
        var consumer = new Consumer();
        consumer.Pump = Task.Run(async () =>
        {
            await foreach (var _ in channel.Reader.ReadAllAsync())
                Interlocked.Increment(ref consumer.Count);
        });
        return consumer;
    }

    private sealed class Consumer
    {
        public int Count;
        public Task Pump = Task.CompletedTask;

        public async Task WaitForChunksAsync(int atLeast, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (Volatile.Read(ref Count) < atLeast)
            {
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException($"Hub received only {Volatile.Read(ref Count)} chunk(s)");
                await Task.Delay(20);
            }
        }
    }

    private static async IAsyncEnumerable<RunnerChunk> SlowProducer()
    {
        for (var i = 0; i < 1000; i++)
        {
            await Task.Delay(25);
            yield return new RunnerChunk("Text", $"chunk {i}");
        }
    }

    private static async IAsyncEnumerable<RunnerChunk> FaultingProducer()
    {
        yield return new RunnerChunk("Text", "one");
        await Task.Delay(25);
        throw new InvalidOperationException("the runner blew up mid-upload");
    }

#pragma warning disable CS1998 // the throw is the point; there is nothing to await
    private static async IAsyncEnumerable<RunnerChunk> Throwing(Exception ex)
    {
        throw ex;
#pragma warning disable CS0162 // unreachable, but required to make this an iterator
        yield break;
#pragma warning restore CS0162
    }
#pragma warning restore CS1998

    /// <summary>The production shape of <c>StreamRunChunks</c>, plus a completion signal.</summary>
    public sealed class TeardownProbeHub(RunnerLink link, ILogger<TeardownProbeHub> logger) : Hub
    {
        private static readonly ConcurrentDictionary<string, Exception?> Finished = new();

        public async Task StreamRunChunks(string runId, IAsyncEnumerable<RunnerChunk> chunks)
        {
            if (!link.TryGetRunChannel(runId, out var channel))
                return;

            try
            {
                await RunnerChunkPump.DrainAsync(runId, chunks, channel, Context.ConnectionAborted, logger);
                Finished[runId] = null;
            }
            catch (Exception ex)
            {
                Finished[runId] = ex;
                throw;
            }
        }

        public static Exception? FailureOf(string runId) => Finished.GetValueOrDefault(runId);

        public static async Task WaitForDoneAsync(string runId, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (!Finished.ContainsKey(runId))
            {
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException($"Hub never saw the end of {runId}");
                await Task.Delay(20);
            }
        }
    }

    private sealed record LogRecord(string Category, LogLevel Level, string Message);

    private sealed class LogCapture : ILoggerProvider
    {
        private readonly ConcurrentQueue<LogRecord> _records = new();

        public IReadOnlyCollection<LogRecord> Records => [.. _records];

        public ILogger CreateLogger(string categoryName) => new Sink(categoryName, _records);

        public void Dispose() { }

        private sealed class Sink(string category, ConcurrentQueue<LogRecord> records) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var message = formatter(state, exception);
                if (exception is not null)
                    message = $"{message} {exception}";
                records.Enqueue(new LogRecord(category, logLevel, message));
            }
        }
    }
}
