namespace Orkka.Tests.Services;

using Orkka.LocalRunner.Contracts;
using Orkka.Runners.Server;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

/// <summary>
/// A runner whose connection closes while a call is in flight is a runner that is not
/// connected — the answer every caller already handles — not an unhandled IOException that
/// the relay endpoints turn into a 500 and an error log (prod, 2026-09-17: a runner restarting
/// mid-setup, twice in two minutes).
/// </summary>
public sealed class RunnerLinkDisconnectTests
{
    /// <summary>An op that outruns its budget is reported the same way: the machine did not
    /// answer. Prod, 2026-09-18: a conflict check on a busy laptop ran past four minutes and
    /// the raw SignalR cancellation surfaced as an unhandled 500 on the relay endpoint.</summary>
    [Fact]
    public async Task AnOpThatOutrunsItsBudget_IsReportedAsRunnerUnavailable()
    {
        var proxy = new Mock<ISingleClientProxy>();
        proxy.Setup(p => p.InvokeCoreAsync<string>("GetMergeConflicts", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, object?[] _, CancellationToken token) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token);
                return "never";
            });
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Client("conn-1")).Returns(proxy.Object);

        var link = new RunnerLink(clients.Object, NullLogger<RunnerLink>.Instance,
            new RunnerLinkMessages("Your runner is not connected.", "disconnected mid-run"));
        link.SetRunner("conn-1", new RunnerHello(
            RunnerProtocol.ProtocolVersion, "dev-pc", true, true, [], [],
            Capabilities: [RunnerCapabilities.Hybrid]));

        var act = () => link.InvokeAsync<string>("GetMergeConflicts", [new object()], CancellationToken.None,
            opTimeout: TimeSpan.FromMilliseconds(100));

        (await act.Should().ThrowAsync<RunnerUnavailableException>())
            .WithMessage("*did not answer GetMergeConflicts*");
    }

    [Fact]
    public async Task AConnectionThatClosesMidCall_IsReportedAsRunnerUnavailable()
    {
        var proxy = new Mock<ISingleClientProxy>();
        proxy.Setup(p => p.InvokeCoreAsync<string>("RunSetupCommand", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Connection 'abc' disconnected."));
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Client("conn-1")).Returns(proxy.Object);

        var link = new RunnerLink(clients.Object, NullLogger<RunnerLink>.Instance,
            new RunnerLinkMessages("Your runner is not connected.", "disconnected mid-run"));
        link.SetRunner("conn-1", new RunnerHello(
            RunnerProtocol.ProtocolVersion, "dev-pc", true, true, [], [],
            Capabilities: [RunnerCapabilities.Hybrid]));

        var act = () => link.InvokeAsync<string>("RunSetupCommand", [new object()], CancellationToken.None);

        (await act.Should().ThrowAsync<RunnerUnavailableException>())
            .WithMessage("Your runner is not connected.");
    }
}
