namespace Orkka.Tests.Features.Runners;

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Runners.Contracts;
using Orkka.Modules.Runners.PublicApi;

/// <summary>
/// The hosted side of reading a folder on the user's machine: one POST to the Worker pod
/// holding their runner, the runner's own result record back as JSON. Offline is an ordinary
/// answer with the remedy in it, never an exception.
/// </summary>
public sealed class RelayLocalRepoReaderTests
{
    private static readonly Ulid OrgId = Ulid.NewUlid();
    private static readonly Ulid RepoId = Ulid.NewUlid();
    private const string UserId = "user_ruan";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed class RecordingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public JsonElement? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            LastBody = body is null ? null : JsonDocument.Parse(body).RootElement;
            return response;
        }
    }

    private static HttpResponseMessage RelayAnswer(bool supported, bool success, object? value, string? error = null)
    {
        var payload = new
        {
            Supported = supported, Success = success,
            Value = value is null ? null : JsonSerializer.Serialize(value, Json),
            Error = error,
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, Json), Encoding.UTF8, "application/json"),
        };
    }

    private static (RelayLocalRepoReader Sut, RecordingHandler Handler) Build(
        HttpResponseMessage response, bool runnerOnline = true, bool relayConfigured = true)
    {
        var locator = new Mock<IRunnerRelayLocator>();
        locator.Setup(l => l.LocateAsync(OrgId, UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(runnerOnline ? new RunnerRelayTarget(OrgId, UserId, "http://worker-0:8080") : null);

        var handler = new RecordingHandler(response);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(RunnerRelay.HttpClientName)).Returns(() => new HttpClient(handler));

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Runners:RelayUrlTemplate"] = relayConfigured ? "http://{pod}:8080" : null,
        }).Build();

        return (new RelayLocalRepoReader(locator.Object, factory.Object, configuration,
            NullLogger<RelayLocalRepoReader>.Instance), handler);
    }

    [Fact]
    public async Task ATreeRead_PostsTheOpToTheUsersWorkerPod_AndReadsTheRunnersEntries()
    {
        var (sut, handler) = Build(RelayAnswer(true, true, new RunnerTreeResult(true,
            [new RunnerTreeEntry("src", "src", true), new RunnerTreeEntry("README.md", "README.md", false)], null)));

        var result = await sut.ListTreeAsync(OrgId, RepoId, UserId, "main", null);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.Should().HaveCount(2);
        result.Value![0].Should().BeEquivalentTo(new { Name = "src", Path = "src", IsDirectory = true });
        handler.LastRequest!.RequestUri!.ToString().Should().Be("http://worker-0:8080/internal/runner/repo/read");
        handler.LastBody!.Value.GetProperty("op").GetString().Should().Be("tree");
        handler.LastBody!.Value.GetProperty("userId").GetString().Should().Be(UserId);
        handler.LastBody!.Value.GetProperty("branch").GetString().Should().Be("main");
    }

    [Fact]
    public async Task ACommitLog_ComesBackAsCommits()
    {
        var when = DateTimeOffset.UtcNow;
        var (sut, handler) = Build(RelayAnswer(true, true, new RunnerCommitLogResult(true,
            [new RunnerCommitInfo("abc", "Nova", when, "feat: x")], null)));

        var result = await sut.GetCommitLogAsync(OrgId, RepoId, UserId, "main", skip: 10, take: 5, filePath: "src/a.ts");

        result.Value.Should().ContainSingle(c => c.Sha == "abc" && c.Subject == "feat: x");
        handler.LastBody!.Value.GetProperty("op").GetString().Should().Be("commits");
        handler.LastBody!.Value.GetProperty("skip").GetInt32().Should().Be(10);
        handler.LastBody!.Value.GetProperty("take").GetInt32().Should().Be(5);
        handler.LastBody!.Value.GetProperty("path").GetString().Should().Be("src/a.ts");
    }

    [Fact]
    public async Task ARunnerThatIsOffline_IsAnAnswerWithTheRemedy()
    {
        var (sut, _) = Build(RelayAnswer(true, true, null), runnerOnline: false);

        var result = await sut.GetFileContentAsync(OrgId, RepoId, UserId, "main", "README.md");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("./orkka start");
    }

    [Fact]
    public async Task APodWithoutTheRunner_IsTheSameAnswer()
    {
        var (sut, _) = Build(RelayAnswer(supported: false, success: false, null, "Runner offline."));

        var result = await sut.ListBranchesAsync(OrgId, RepoId, UserId);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("./orkka start");
    }

    [Fact]
    public async Task TheRunnersOwnRefusal_ReachesTheCaller_Verbatim()
    {
        var (sut, _) = Build(RelayAnswer(true, false, null, "Bare repo does not exist for this repository."));

        var result = await sut.GetCommitDiffAsync(OrgId, RepoId, UserId, "abc", 1000);

        result.Error.Should().Be("Bare repo does not exist for this repository.");
    }

    [Fact]
    public async Task SyncingTheMirror_UsesTheExistingEnsureCloneRelay_WithTheFolderPath()
    {
        var (sut, handler) = Build(RelayAnswer(true, true, null));

        var result = await sut.EnsureMirrorAsync(OrgId, RepoId, UserId, @"C:\Code\storefront-api");

        result.IsSuccess.Should().BeTrue(result.Error);
        handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/internal/runner/repo/ensure-clone");
        handler.LastBody!.Value.GetProperty("cloneUrlOrPath").GetString().Should().Be(@"C:\Code\storefront-api");
    }

    [Fact]
    public void CanReach_FollowsWhetherARelayIsConfigured()
    {
        Build(RelayAnswer(true, true, null), relayConfigured: true).Sut.CanReach.Should().BeTrue();
        Build(RelayAnswer(true, true, null), relayConfigured: false).Sut.CanReach.Should().BeFalse(
            "the Local edition composes no relay and keeps its in-process reader");
    }
}
