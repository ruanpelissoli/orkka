namespace Orkka.Tests.Services;

using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Orkka.Runners.Server;

/// <summary>
/// A runner hub method that awaits a CLIENT RESULT from the same runner (ticket intake routing a
/// ticket with a utility completion inside <c>ReportTickets</c>) must not hang when the runner
/// invokes another hub method meanwhile (<c>RotateToken</c> on its session's first minute tick).
/// With SignalR's default of one invocation per client the hub's read loop parks waiting for a
/// slot for that invocation, so the completion behind it is never read and the hub method waits
/// out the whole op timeout (R5 e2e run, 2026-11-18: 900 s, then the Story was routed as if the
/// runner were offline; a dump showed DispatchMessagesAsync parked in the invocation semaphore).
/// </summary>
public sealed class RunnerHubClientResultDeadlockTests
{
    [Fact]
    public async Task HubMethodAwaitingAClientResult_SurvivesAnotherInvocationMeanwhile()
    {
        await using var rig = await Rig.StartAsync(RunnerHubTimeouts.MaxParallelInvocationsPerClient);

        var answer = await rig.Connection.InvokeAsync<int>(nameof(DeadlockHub.ReportTickets)).WaitAsync(TimeSpan.FromSeconds(15));

        answer.Should().Be(42);
        (await rig.Rotation.WaitAsync(TimeSpan.FromSeconds(15))).Should().Be("rotated", "the invocation sent meanwhile was served too");
    }

    [Fact]
    public async Task WithOneInvocationPerClient_TheSameExchangeHangs()
    {
        // The mechanism the setting exists for. If SignalR ever stops parking its read loop
        // here, this fails and the setting can be revisited.
        await using var rig = await Rig.StartAsync(1);

        var call = rig.Connection.InvokeAsync<int>(nameof(DeadlockHub.ReportTickets));

        var finished = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(3)));
        finished.Should().NotBe(call, "the client's completion sits behind the invocation the read loop is parked on");
    }

    public sealed class Rig : IAsyncDisposable
    {
        private TestServer _server = null!;
        public HubConnection Connection { get; private set; } = null!;
        public Task<string> Rotation { get; private set; } = Task.FromResult("");

        public static async Task<Rig> StartAsync(int maxParallel)
        {
            var rig = new Rig();
            rig._server = new TestServer(new WebHostBuilder()
                .ConfigureServices(services =>
                    services.AddSignalR().AddHubOptions<DeadlockHub>(o => o.MaximumParallelInvocationsPerClient = maxParallel))
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapHub<DeadlockHub>("/hub"));
                }));

            rig.Connection = new HubConnectionBuilder()
                .WithUrl(new Uri(rig._server.BaseAddress, "hub"), options =>
                {
                    options.HttpMessageHandlerFactory = _ => rig._server.CreateHandler();
                    options.Transports = HttpTransportType.LongPolling;
                })
                .Build();
            // The runner's side of a utility op: while it generates, the session's minute tick
            // invokes RotateToken on the same connection; then the utility result goes back.
            rig.Connection.On<int>("UtilityComplete", async () =>
            {
                rig.Rotation = rig.Connection.InvokeAsync<string>(nameof(DeadlockHub.RotateToken));
                await Task.Delay(300);
                return 42;
            });
            await rig.Connection.StartAsync();
            return rig;
        }

        public async ValueTask DisposeAsync()
        {
            _server.Dispose();
            try { await Connection.DisposeAsync(); }
            catch { /* the server is gone */ }
        }
    }

    public sealed class DeadlockHub(IHubContext<DeadlockHub> hub) : Hub
    {
        // ReportTickets → routing → a utility completion on the same runner, invoked the way
        // RunnerLink does it: through the hub context, by connection id.
        public Task<int> ReportTickets() =>
            hub.Clients.Client(Context.ConnectionId).InvokeAsync<int>("UtilityComplete", Context.ConnectionAborted);

        public string RotateToken() => "rotated";
    }
}
