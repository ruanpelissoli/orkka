namespace Orkka.Tests.Services;

using System.Runtime.CompilerServices;
using Orkka.LocalRunner;
using Orkka.LocalRunner.Contracts;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The runner-protocol mechanics behind the registry, against a real SignalR hub: a
/// streamed <c>SendAsync</c> returns as soon as the invocation is on the wire while the
/// chunks pump in the background, so a run's lifetime must come from the tracked stream's
/// own teardown — and a host-side CancelRun must actually stop the producer mid-stream.
/// This is exactly what RunnerSession.StartRun relies on.
/// </summary>
public sealed class RunnerRunTrackingTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    private TestServer _server = null!;
    private HubConnection _connection = null!;

    public async Task InitializeAsync()
    {
        _server = new TestServer(new WebHostBuilder()
            .ConfigureServices(services => services.AddSignalR())
            .Configure(app =>
            {
                app.UseRouting();
                app.UseEndpoints(endpoints => endpoints.MapHub<ProbeHub>("/hub"));
            }));

        _connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_server.BaseAddress, "hub"), options =>
            {
                options.HttpMessageHandlerFactory = _ => _server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();
        await _connection.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
        _server.Dispose();
    }

    [Fact]
    public async Task StreamedSend_ReturnsBeforeTheStreamEnds_AndTheRegistryStillTracksTheRun()
    {
        var registry = new RunnerRunRegistry();
        var probe = new ProbeState();
        registry.TryStart("run-1", "task-1", out var run);

        await _connection.SendAsync(
            RunnerProtocol.StreamRunChunks, "run-1",
            registry.Track(run, Producer(run.Cts.Token, probe)), run.Cts.Token);

        // The send is back; the producer is not done and the run is still on the books.
        probe.TornDown.Should().BeFalse();
        registry.ActiveRunIds.Should().Contain("run-1");
        run.Completed.IsCompleted.Should().BeFalse();

        // Let it finish naturally (bounded producer) — the registry learns of the end.
        await run.Completed.WaitAsync(Wait);
        probe.TornDown.Should().BeTrue();
        registry.ActiveRunIds.Should().BeEmpty();
        await ProbeHub.WaitForDoneAsync("run-1", Wait);
        ProbeHub.ReceivedCount("run-1").Should().Be(ProducerCount);
    }

    [Fact]
    public async Task CancelRun_MidStream_StopsTheProducer_AndCompletesTheRun()
    {
        var registry = new RunnerRunRegistry();
        var probe = new ProbeState();
        registry.TryStart("run-2", "task-2", out var run);

        await _connection.SendAsync(
            RunnerProtocol.StreamRunChunks, "run-2",
            registry.Track(run, SlowProducer(run.Cts.Token, probe)), run.Cts.Token);

        // Wait until the hub has actually received something — the run is truly mid-stream.
        await ProbeHub.WaitForChunksAsync("run-2", atLeast: 2, Wait);
        registry.ActiveRunIds.Should().Contain("run-2");

        // What the host's CancelRun / CancelTaskRuns handler does.
        var cancelled = await registry.CancelTaskRunsAsync("task-2", Wait);

        cancelled.Should().Be(1);
        probe.TornDown.Should().BeTrue("the producer's teardown ran before CancelTaskRuns returned");
        run.Completed.IsCompletedSuccessfully.Should().BeTrue();
        registry.ActiveRunIds.Should().BeEmpty();
        await ProbeHub.WaitForDoneAsync("run-2", Wait);
        ProbeHub.ReceivedCount("run-2").Should().BeLessThan(SlowProducerCount, "the stream was cut short");
    }

    private sealed class ProbeState
    {
        public volatile bool TornDown;
    }

    private const int ProducerCount = 5;
    private const int SlowProducerCount = 1000;

    private static async IAsyncEnumerable<RunnerChunk> Producer(
        CancellationToken ct, ProbeState probe, [EnumeratorCancellation] CancellationToken _ = default)
    {
        try
        {
            for (var i = 0; i < ProducerCount; i++)
            {
                await Task.Delay(50, ct);
                yield return new RunnerChunk("Text", $"chunk {i}");
            }
        }
        finally
        {
            probe.TornDown = true;
        }
    }

    private static async IAsyncEnumerable<RunnerChunk> SlowProducer(
        CancellationToken ct, ProbeState probe, [EnumeratorCancellation] CancellationToken _ = default)
    {
        try
        {
            for (var i = 0; i < SlowProducerCount; i++)
            {
                await Task.Delay(50, ct);
                yield return new RunnerChunk("Text", $"chunk {i}");
            }
        }
        finally
        {
            probe.TornDown = true;
        }
    }

    /// <summary>Stand-in for the host hub's StreamRunChunks: drains the upload.</summary>
    public sealed class ProbeHub : Hub
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> Received = new();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> Done = new();

        public async Task StreamRunChunks(string runId, IAsyncEnumerable<RunnerChunk> chunks)
        {
            try
            {
                await foreach (var _ in chunks)
                    Received.AddOrUpdate(runId, 1, (_, n) => n + 1);
            }
            finally
            {
                Done[runId] = true;
            }
        }

        public static int ReceivedCount(string runId) => Received.GetValueOrDefault(runId);

        public static async Task WaitForChunksAsync(string runId, int atLeast, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (ReceivedCount(runId) < atLeast)
            {
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException($"Hub received only {ReceivedCount(runId)} chunk(s) for {runId}");
                await Task.Delay(20);
            }
        }

        public static async Task WaitForDoneAsync(string runId, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (!Done.ContainsKey(runId))
            {
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException($"Hub never saw the end of {runId}");
                await Task.Delay(20);
            }
        }
    }
}
