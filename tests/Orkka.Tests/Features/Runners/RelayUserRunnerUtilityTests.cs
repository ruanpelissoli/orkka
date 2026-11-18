namespace Orkka.Tests.Features.Runners;

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Orkka.Modules.Runners.Contracts;
using Orkka.Modules.Runners.PublicApi;
using Orkka.SharedKernel.Ai;

/// <summary>
/// The API host's utility relay is the only way a one-shot utility call made there reaches a
/// model: the user's own runner. These pin its two rules: relay whenever the user's runner is
/// online (whatever their execution preference — there is no platform-key fallback to prefer),
/// and treat every miss as null, never an error, so the caller takes its runner-offline path.
/// </summary>
public sealed class RelayUserRunnerUtilityTests
{
    private readonly Ulid _orgId = Ulid.NewUlid();
    private const string UserId = "user_local";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static AnthropicRequest Request() => new()
    {
        Model = "claude-sonnet-4-6",
        MaxTokens = 2048,
        System = "You are the product owner.",
        Messages =
        [
            new AnthropicMessage("user", "First question"),
            new AnthropicMessage("assistant", "Dropped by the runner proxy"),
            new AnthropicMessage("user", "Second question"),
        ],
    };

    private sealed class RecordingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return response;
        }
    }

    private static HttpResponseMessage JsonResponse(object body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json"),
        };

    private (RelayUserRunnerUtility Sut, RecordingHandler Handler) Build(
        RunnerRelayTarget? relay,
        HttpResponseMessage response)
    {
        var locator = new Mock<IRunnerRelayLocator>();
        locator
            .Setup(l => l.LocateAsync(_orgId, UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(relay);

        var services = new ServiceCollection();
        services.AddScoped(_ => locator.Object);
        var provider = services.BuildServiceProvider();

        var handler = new RecordingHandler(response);
        var factory = new Mock<IHttpClientFactory>();
        factory
            .Setup(f => f.CreateClient(RelayUserRunnerUtility.HttpClientName))
            .Returns(() => new HttpClient(handler));

        var sut = new RelayUserRunnerUtility(
            provider.GetRequiredService<IServiceScopeFactory>(),
            factory.Object,
            NullLogger<RelayUserRunnerUtility>.Instance);
        return (sut, handler);
    }

    private RunnerRelayTarget OnlineRelay() => new(_orgId, UserId, "http://worker-0.orkka-worker:8080");

    [Fact]
    public async Task Serves_a_user_with_an_online_runner_and_maps_the_runner_reply()
    {
        var (sut, handler) = Build(
            OnlineRelay(),
            JsonResponse(new RelayUtilityResponse(true, "Tell me more about the users.", 1200, 80, 900, 0)));

        var response = await sut.TryCompleteAsync(_orgId, UserId, Request());

        response.Should().NotBeNull();
        response!.Text.Should().Be("Tell me more about the users.");
        response.InputTokens.Should().Be(1200);
        response.OutputTokens.Should().Be(80);
        response.CacheReadTokens.Should().Be(900);

        handler.LastRequest!.RequestUri!.ToString()
            .Should().Be("http://worker-0.orkka-worker:8080/internal/runner/utility");

        // Only user-role text crosses the relay (that is all the runner op forwards anyway),
        // joined in order — the same flattening RunnerAnthropicClient does in-process.
        var sent = JsonSerializer.Deserialize<RelayUtilityRequest>(handler.LastBody!, Json)!;
        sent.OrgId.Should().Be(_orgId.ToString());
        sent.UserId.Should().Be(UserId);
        sent.Model.Should().Be("claude-sonnet-4-6");
        sent.System.Should().Be("You are the product owner.");
        sent.UserMessage.Should().Be("First question\n\nSecond question");
        sent.MaxTokens.Should().Be(2048);
    }

    [Fact]
    public async Task Offline_runner_is_a_miss_not_an_error()
    {
        var (sut, handler) = Build(
            relay: null,
            JsonResponse(new RelayUtilityResponse(true, "should not be used")));

        var response = await sut.TryCompleteAsync(_orgId, UserId, Request());

        response.Should().BeNull();
        handler.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task Worker_reporting_not_served_is_a_miss()
    {
        var (sut, _) = Build(
            OnlineRelay(),
            JsonResponse(new RelayUtilityResponse(false)));

        var response = await sut.TryCompleteAsync(_orgId, UserId, Request());

        response.Should().BeNull();
    }

    [Fact]
    public async Task Relay_failure_is_a_miss_not_an_error()
    {
        var (sut, _) = Build(
            OnlineRelay(),
            new HttpResponseMessage(HttpStatusCode.BadGateway));

        var response = await sut.TryCompleteAsync(_orgId, UserId, Request());

        response.Should().BeNull("a wedged relay is a miss the caller handles, never an exception");
    }

    [Fact]
    public async Task No_user_means_no_runner_to_ask()
    {
        var (sut, handler) = Build(
            OnlineRelay(),
            JsonResponse(new RelayUtilityResponse(true, "should not be used")));

        var response = await sut.TryCompleteAsync(_orgId, userId: null, Request());

        response.Should().BeNull();
        handler.LastRequest.Should().BeNull();
    }
}
