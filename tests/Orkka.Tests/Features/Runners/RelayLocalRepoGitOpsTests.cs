namespace Orkka.Tests.Features.Runners;

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Orkka.Modules.Runners.Contracts;
using Orkka.Modules.Runners.PublicApi;

/// <summary>
/// The hosted side of git on a folder on the user's machine. A squash merge there is a
/// clone, a merge and a push on a laptop that may be busy; the relay hop must not give up on
/// it the way it gives up on a wedged editor launch. Prod, 2026-09-16: a merge that ran past
/// the 30-second client was reported as "runner not connected" and parked the task.
/// </summary>
public sealed class RelayLocalRepoGitOpsTests
{
    private static readonly Ulid OrgId = Ulid.NewUlid();
    private static readonly Ulid RepoId = Ulid.NewUlid();
    private const string UserId = "user_ruan";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed class RecordingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(response);
        }
    }

    private static HttpResponseMessage RelayAnswer(bool supported, bool success, string? error)
    {
        var payload = new { Supported = supported, Success = success, Value = (string?)null, Error = error };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, Json), Encoding.UTF8, "application/json"),
        };
    }

    private static (RelayLocalRepoGitOps Sut, RecordingHandler Handler, Mock<IHttpClientFactory> Factory) Build(
        HttpResponseMessage response)
    {
        var locator = new Mock<IRunnerRelayLocator>();
        locator.Setup(l => l.LocateAsync(OrgId, UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RunnerRelayTarget(OrgId, UserId, "http://worker-0:8080"));

        var handler = new RecordingHandler(response);
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        factory.Setup(f => f.CreateClient(RelayLocalRepoGitOps.HttpClientName)).Returns(() => new HttpClient(handler));

        return (new RelayLocalRepoGitOps(locator.Object, factory.Object, NullLogger<RelayLocalRepoGitOps>.Instance),
            handler, factory);
    }

    [Fact]
    public async Task ASquashMerge_GoesThroughTheLocalGitClient_NotTheEditorLaunchOne()
    {
        var (sut, handler, factory) = Build(RelayAnswer(true, true, null));

        var result = await sut.SquashMergeAsync(OrgId, RepoId, UserId, "orkka/STO-15", "main", "Land it");

        result.IsSuccess.Should().BeTrue();
        handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/internal/runner/pr/merge");
        factory.Verify(f => f.CreateClient(RelayLocalRepoGitOps.HttpClientName), Times.Once);
        factory.Verify(f => f.CreateClient(RunnerRelay.HttpClientName), Times.Never);
    }

    [Fact]
    public void TheLocalGitBudget_OutlivesAMergeOnABusyLaptop()
    {
        RelayLocalRepoGitOps.Budget.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMinutes(3));
        RelayLocalRepoGitOps.HttpClientName.Should().NotBe(RunnerRelay.HttpClientName);
    }

    [Fact]
    public async Task TheRunnersOwnWords_ComeBackAsTheFailure()
    {
        var (sut, _, _) = Build(RelayAnswer(true, false,
            "Your repository has uncommitted changes on 'main'. Commit or stash them, then merge again."));

        var result = await sut.SquashMergeAsync(OrgId, RepoId, UserId, "orkka/STO-15", "main", "Land it");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("uncommitted changes");
    }
}
