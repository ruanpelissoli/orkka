namespace Orkka.Tests.Services;

using System.Diagnostics;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// How many runner operations a connected runner actually works on at once.
///
/// Provisioning fans out on the host — brand-kit stages, the PRD, the roadmap all run as
/// concurrent branches — and for a hybrid user every one of them lands on the same runner as
/// a client-result invocation (<c>RunnerSession</c> registers them with
/// <c>HubConnection.On&lt;TReq, TRes&gt;</c>, exactly as this does). If the client dispatches
/// those handlers one at a time, the host's parallelism is a fiction: the ops queue, each pays
/// its own CLI startup, and a step's wall-clock includes everything queued in front of it.
///
/// This pins the real behaviour so a diagnosis never has to guess at it again.
/// </summary>
public sealed class RunnerOpConcurrencyTests : IAsyncLifetime
{
    private TestServer _server = null!;
    private HubConnection _connection = null!;

    public async Task InitializeAsync()
    {
        _server = new TestServer(new WebHostBuilder()
            .ConfigureServices(services => services.AddSignalR())
            .Configure(app =>
            {
                app.UseRouting();
                app.UseEndpoints(endpoints => endpoints.MapHub<OpHub>("/hub"));
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
    public async Task ConcurrentOps_OverlapOnTheRunner_RatherThanQueueing()
    {
        var tracker = new OverlapTracker();
        const int opDuration = 400;

        _connection.On<int, int>("SlowOp", async n =>
        {
            tracker.Enter();
            try
            {
                await Task.Delay(opDuration);
                return n;
            }
            finally
            {
                tracker.Exit();
            }
        });

        var hub = _server.Services.GetRequiredService<IHubContext<OpHub>>();
        var client = hub.Clients.Client(_connection.ConnectionId!);

        var sw = Stopwatch.StartNew();
        var results = await Task.WhenAll(
            client.InvokeAsync<int>("SlowOp", 1, CancellationToken.None),
            client.InvokeAsync<int>("SlowOp", 2, CancellationToken.None),
            client.InvokeAsync<int>("SlowOp", 3, CancellationToken.None));
        sw.Stop();

        results.Should().BeEquivalentTo([1, 2, 3]);

        // The assertion that matters: three 400ms ops sent together either overlap (~400ms
        // total, MaxConcurrent 3) or are worked one at a time (~1200ms, MaxConcurrent 1).
        // Whichever way this lands, provisioning's branch design depends on knowing it.
        tracker.MaxConcurrent.Should().BeGreaterThan(1,
            "the host fans provisioning steps out concurrently; a runner that works them one "
            + "at a time turns that fan-out into a queue, and every step's wall-clock includes "
            + "the ops in front of it");
    }

    private sealed class OverlapTracker
    {
        private readonly Lock _gate = new();
        private int _current;

        public int MaxConcurrent { get; private set; }

        public void Enter()
        {
            lock (_gate)
            {
                _current++;
                if (_current > MaxConcurrent) MaxConcurrent = _current;
            }
        }

        public void Exit()
        {
            lock (_gate) { _current--; }
        }
    }

    public sealed class OpHub : Hub;
}
