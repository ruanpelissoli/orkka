namespace Orkka.Tests.Services;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Orkka.Git;
using Orkka.LocalRunner;
using Orkka.LocalRunner.Cli;
using Orkka.LocalRunner.Contracts;
using Orkka.LocalRunner.Trackers;
using Orkka.Modules.Execution.Agents;

public sealed class WorkspaceProfileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orkka-ws-" + Guid.NewGuid().ToString("N"));

    public WorkspaceProfileTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string P(params string[] parts) => Path.Combine([_root, .. parts]);

    [Fact]
    public void Migration_MovesTheSingleConnection_IntoTheDefaultWorkspace_Once()
    {
        File.WriteAllText(P("hosted.env"), "ORKKA_HOSTED_URL=https://runner.example.com/\nORKKA_HOSTED_TOKEN=orkr_single\n");
        File.WriteAllText(P("runner.env"),
            "ORKKA_GEMINI_API_KEY=gem-key\nORKKA_GIT_AUTHOR_NAME=Dana Silva\nORKKA_GIT_AUTHOR_EMAIL=dana@acme.com\n");

        WorkspaceProfiles.MigrateLegacy(_root).Should().Contain("'default'");

        var profile = WorkspaceProfiles.Get(_root, "default")!;
        profile.HostUrl.Should().Be("https://runner.example.com");
        profile.Token.Should().Be("orkr_single");
        profile.GitAuthorName.Should().Be("Dana Silva");
        profile.GitAuthorEmail.Should().Be("dana@acme.com");
        File.Exists(P("workspaces", "default", "profile.env")).Should().BeTrue();

        File.Exists(P("hosted.env")).Should().BeFalse();
        File.Exists(P("hosted.env.migrated")).Should().BeFalse("a backup of hosted.env would be a plaintext token");
        File.ReadAllText(P("workspaces", "default", "profile.env")).Should().NotContain("orkr_single",
            "the token lives in the workspace's secret store, never in profile.env");
        File.ReadAllText(P("runner.env.migrated")).Should().Contain("ORKKA_GIT_AUTHOR_NAME");
        var runnerEnv = File.ReadAllText(P("runner.env"));
        runnerEnv.Should().Contain("ORKKA_GEMINI_API_KEY=gem-key", "AI CLI tokens are machine-wide and stay");
        runnerEnv.Should().NotContain("ORKKA_GIT_AUTHOR");

        WorkspaceProfiles.MigrateLegacy(_root).Should().BeNull("it runs once");
    }

    [Fact]
    public void Migration_FoldsLegacyStackEnv_FirstAndDoesNothingWithoutAConnection()
    {
        File.WriteAllText(P("stack.env"), "ORKKA_GEMINI_API_KEY=old\n");

        WorkspaceProfiles.MigrateLegacy(_root).Should().BeNull();

        File.Exists(P("stack.env")).Should().BeFalse();
        File.ReadAllText(P("runner.env")).Should().Contain("ORKKA_GEMINI_API_KEY=old");
        WorkspaceProfiles.List(_root).Should().BeEmpty();
    }

    [Fact]
    public void Profiles_RoundTrip_ListDefaultFirst_AndNeverPrintTheirToken()
    {
        WorkspaceProfiles.Save(_root, new WorkspaceProfile { Name = "zeta", HostUrl = "https://h", Token = "orkr_z" });
        WorkspaceProfiles.Save(_root, new WorkspaceProfile { Name = "default", HostUrl = "https://h", Token = "orkr_d" });
        WorkspaceProfiles.Save(_root, new WorkspaceProfile
        {
            Name = "acme", HostUrl = "https://h", Token = "orkr_a", GitAuthorName = "Dana", GitAuthorEmail = "d@acme.com",
        });
        Directory.CreateDirectory(P("workspaces", "no-profile"));

        WorkspaceProfiles.List(_root).Select(p => p.Name).Should().Equal("default", "acme", "zeta");
        WorkspaceProfiles.Get(_root, "acme")!.HasIdentity.Should().BeTrue();
        WorkspaceProfiles.Get(_root, "acme")!.ToString().Should().NotContain("orkr_");
        WorkspaceProfiles.WorkspaceNames(_root).Should().Contain("no-profile");
    }

    [Theory]
    [InlineData("acme", true)]
    [InlineData("job-2", true)]
    [InlineData("Acme", false)]
    [InlineData("../x", false)]
    [InlineData("-x", false)]
    [InlineData("", false)]
    public void WorkspaceNames_AreFolderSafe(string name, bool valid)
        => WorkspaceProfiles.IsValidName(name).Should().Be(valid);
}

public sealed class WorkspaceCliTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orkka-wscli-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private IReadOnlyList<WorkspaceProfile>? Connect(out string? error, params string[] args)
        => ConnectCommand.Resolve(args, _root, out error, out _);

    [Fact]
    public void Connect_ParsesTheWorkspaceFlags()
    {
        ConnectCommand.ParseArgs(["--workspace", "Acme", "--token", "orkr_x", "--host", "http://h"])
            .Should().Be(new ConnectCommand.ConnectArgs("acme", "orkr_x", "http://h"));
        ConnectCommand.ParseArgs(["-w", "job"]).Workspace.Should().Be("job");
    }

    [Fact]
    public void Connect_AddsProfiles_AndRunsThemAll()
    {
        Connect(out var error).Should().BeNull();
        error.Should().Contain("Usage");

        Connect(out _, "--token", "orkr_first")!.Select(p => p.Name).Should().Equal("default");
        var both = Connect(out _, "--workspace", "acme", "--token", "orkr_second", "--host", "http://127.0.0.1:1/");
        both!.Select(p => p.Name).Should().Equal("default", "acme");
        both[1].HostUrl.Should().Be("http://127.0.0.1:1");

        Connect(out _)!.Should().HaveCount(2, "a bare connect runs every workspace");
    }

    [Fact]
    public void Connect_RefusesAmbiguousOrWrongArguments()
    {
        Connect(out _, "--token", "orkr_a");
        Connect(out _, "--workspace", "acme", "--token", "orkr_b");

        Connect(out var several, "--token", "orkr_c").Should().BeNull();
        several.Should().Contain("several workspaces").And.Contain("--workspace");

        Connect(out var badToken, "--workspace", "acme", "--token", "nope").Should().BeNull();
        badToken.Should().Contain("orkr_");

        Connect(out var missing, "--workspace", "other").Should().BeNull();
        missing.Should().Contain("no workspace 'other'");

        Connect(out var badName, "--workspace", "../etc", "--token", "orkr_x").Should().BeNull();
        badName.Should().Contain("not a workspace name");
    }

    [Fact]
    public void Connect_UpdatingAToken_KeepsTheIdentity()
    {
        Connect(out _, "--workspace", "acme", "--token", "orkr_old");
        WorkspaceCommand.Run(["set-identity", "acme", "--name", "Dana Silva", "--email", "dana@acme.com"], _root, TextWriter.Null, TextWriter.Null)
            .Should().Be(0);

        Connect(out _, "--workspace", "acme", "--token", "orkr_new");

        var profile = WorkspaceProfiles.Get(_root, "acme")!;
        profile.Token.Should().Be("orkr_new");
        profile.GitAuthorName.Should().Be("Dana Silva");
    }

    [Fact]
    public void WorkspaceCommand_ListsWithoutTokens_SetsAndClearsIdentity_AndRemoves()
    {
        Connect(out _, "--workspace", "acme", "--token", "orkr_secret_acme");
        Connect(out _, "--workspace", "globex", "--token", "orkr_secret_globex");
        var output = new StringWriter();

        WorkspaceCommand.Run(["set-identity", "globex", "--name", "D", "--email", "nope"], _root, output, output).Should().Be(1);
        WorkspaceCommand.Run(["set-identity", "globex", "--name", "Dana", "--email", "dana@globex.com"], _root, output, output).Should().Be(0);
        WorkspaceCommand.Run(["list"], _root, output, output).Should().Be(0);

        var text = output.ToString();
        text.Should().Contain("acme").And.Contain("globex").And.Contain("Dana <dana@globex.com>").And.NotContain("orkr_");

        WorkspaceCommand.Run(["set-identity", "globex", "--clear"], _root, output, output).Should().Be(0);
        WorkspaceProfiles.Get(_root, "globex")!.HasIdentity.Should().BeFalse();

        WorkspaceCommand.Run(["remove", "acme"], _root, output, output).Should().Be(0);
        WorkspaceProfiles.List(_root).Select(p => p.Name).Should().Equal("globex");
        Directory.Exists(Path.Combine(_root, "workspaces", "acme")).Should().BeFalse();
        WorkspaceCommand.Run(["remove", "acme"], _root, output, output).Should().Be(1);
    }

    [Fact]
    public void RemovingAWorkspace_DeletesOnlyItsTrackerTokens()
    {
        var secrets = new FileSecretStore(Path.Combine(_root, "secret-store"));
        new TrackerStore(_root, "acme").Upsert(new TrackerConnectionEntry
        {
            Id = "jira-acme", Kind = TrackerKinds.Jira, SiteUrl = "https://acme.atlassian.net", AddedAt = DateTimeOffset.UtcNow,
        });
        new TrackerStore(_root, "globex").Upsert(new TrackerConnectionEntry
        {
            Id = "jira-globex", Kind = TrackerKinds.Jira, SiteUrl = "https://globex.atlassian.net", AddedAt = DateTimeOffset.UtcNow,
        });
        secrets.Set("jira-acme", "tok-a");
        secrets.Set("jira-globex", "tok-g");

        TrackerCommand.RemoveAllSecrets(_root, "acme", TextWriter.Null, secrets);

        secrets.Get("jira-acme").Should().BeNull();
        secrets.Get("jira-globex").Should().Be("tok-g");
    }

    [Fact]
    public void TrackerCommands_NameTheirWorkspace_WhenThereAreSeveral()
    {
        TrackerCommand.TakeWorkspace(["add", "jira", "--workspace", "Acme", "--site", "x"])
            .Should().BeEquivalentTo(("acme", new[] { "add", "jira", "--site", "x" }));
        TrackerCommand.TakeWorkspace(["list", "--workspace=globex"]).Workspace.Should().Be("globex");

        TrackerCommand.ResolveWorkspace(_root, null, out _).Should().Be("default", "a fresh machine has the default workspace");

        Connect(out _, "--workspace", "acme", "--token", "orkr_a");
        TrackerCommand.ResolveWorkspace(_root, null, out _).Should().Be("acme", "the only workspace needs no flag");

        Connect(out _, "--workspace", "globex", "--token", "orkr_g");
        TrackerCommand.ResolveWorkspace(_root, null, out var several).Should().BeNull();
        several.Should().Contain("several workspaces");
        TrackerCommand.ResolveWorkspace(_root, "globex", out _).Should().Be("globex");
        TrackerCommand.ResolveWorkspace(_root, "initech", out var unknown).Should().BeNull();
        unknown.Should().Contain("no workspace 'initech'");
    }
}

public sealed class RunnerSupervisorTests : IDisposable
{
    private const string TokenA = "orkr_aaaa_secret_token_for_acme";
    private const string TokenB = "orkr_bbbb_secret_token_for_globex";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orkka-sup-" + Guid.NewGuid().ToString("N"));
    private readonly CapturingLoggerProvider _logs = new();
    private readonly StringWriter _console = new();

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static WorkspaceProfile Profile(string name, string token, string? author = null) => new()
    {
        Name = name,
        // Nothing listens on port 1: every session dials, fails and keeps retrying.
        HostUrl = "http://127.0.0.1:1",
        Token = token,
        GitAuthorName = author,
        GitAuthorEmail = author is null ? null : $"{author.ToLowerInvariant()}@{name}.com",
    };

    private (RunnerSessionFactory Factory, ConsoleStatus Status, MachineRunSlots Slots, RunnerHostRouter Router) Factory()
    {
        var loggers = LoggerFactory.Create(b => b.AddProvider(_logs).SetMinimumLevel(LogLevel.Debug));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Worker:RepoBasePath"] = Path.Combine(_root, "repos") })
            .Build();
        var status = new ConsoleStatus(enabled: true, _console);
        var slots = new MachineRunSlots(2);
        var router = new RunnerHostRouter();
        var http = new HttpClient(new FakeDownloadHandler());
        var probes = new ProviderDetectionProbes
        {
            ReadRunnerEnv = _ => null,
            GhLoggedIn = () => false,
            CliOnPath = _ => false,
            // Hermetic: never spawn the machine's real CLIs (`--version`, `codex login status`).
            CliIsStub = _ => false,
            CliVersion = _ => null,
            CodexLoginStatus = () => null,
            Home = () => _root,
        };
        var factory = new RunnerSessionFactory(
            _root, config, loggers,
            new GitProcess(NullLogger<GitProcess>.Instance),
            new LoggingGitOperationLedger(NullLogger.Instance, GitTopologies.UserRunner),
            Mock.Of<IAiAgentInvoker>(),
            new ProviderDetection(NullLogger<ProviderDetection>.Instance, null, probes),
            new ForgeCli(NullLogger<ForgeCli>.Instance),
            new Orkka.LocalRunner.Apps.AppRunManager(NullLogger<Orkka.LocalRunner.Apps.AppRunManager>.Instance),
            new ProviderAuthOverrides(),
            status, slots, router,
            new JiraCloudClient(http), new AzureDevOpsClient(http), http,
            (root, profile) => new FileSecretStore(Path.Combine(root, "workspaces", profile, "secrets")),
            new Orkka.LocalRunner.Commands.CommandTrust(
                new Orkka.LocalRunner.Commands.CommandApprovalStore(_root),
                new GitProcess(NullLogger<GitProcess>.Instance), NullLogger<Orkka.LocalRunner.Commands.CommandTrust>.Instance));
        return (factory, status, slots, router);
    }

    [Fact]
    public async Task OneSessionPerWorkspace_EachWithItsOwnTrackersIdentityAndInstance_AndNoTokenInTheLogs()
    {
        new TrackerStore(_root, "acme").Upsert(new TrackerConnectionEntry
        {
            Id = "jira-acme", Kind = TrackerKinds.Jira, SiteUrl = "https://acme.atlassian.net", AddedAt = DateTimeOffset.UtcNow,
        });
        var (factory, status, _, _) = Factory();
        var supervisor = new RunnerSupervisor(
            [Profile("acme", TokenA, "Dana"), Profile("globex", TokenB)], factory, NullLogger<RunnerSupervisor>.Instance);

        await supervisor.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline
            && !(status.States.GetValueOrDefault("acme") == "unreachable" && status.States.GetValueOrDefault("globex") == "unreachable"))
            await Task.Delay(100);
        await supervisor.StopAsync(CancellationToken.None);

        var workspaces = supervisor.Workspaces;
        workspaces.Select(w => w.Profile.Name).Should().Equal("acme", "globex");
        status.States["acme"].Should().Be("unreachable");
        status.States["globex"].Should().Be("unreachable");
        _console.ToString().Should().Contain("[acme]").And.Contain("[globex]");

        var acme = workspaces[0];
        var globex = workspaces[1];
        acme.Session.InstanceId.Should().NotBe(globex.Session.InstanceId);
        acme.Session.HostLink.Should().NotBeSameAs(globex.Session.HostLink);
        acme.TrackerStore.FilePath.Should().Be(Path.Combine(_root, "workspaces", "acme", "trackers.json"));
        globex.TrackerStore.FilePath.Should().Be(Path.Combine(_root, "workspaces", "globex", "trackers.json"));
        acme.AttachmentCache.TicketsDir.Should().Be(Path.Combine(_root, "workspaces", "acme", "tickets"));
        globex.AttachmentCache.TicketsDir.Should().Be(Path.Combine(_root, "workspaces", "globex", "tickets"));
        acme.Session.Trackers.Store.List().Select(e => e.Id).Should().Equal("jira-acme");
        globex.Session.Trackers.Store.List().Should().BeEmpty();
        acme.Configuration["Git:CommitAuthorName"].Should().Be("Dana");
        acme.Configuration["Git:CommitAuthorEmail"].Should().Be("dana@acme.com");
        globex.Configuration["Git:CommitAuthorName"].Should().BeNull("globex commits as the developer's git config says");
        acme.Configuration["Worker:RepoBasePath"].Should().Be(globex.Configuration["Worker:RepoBasePath"], "the repo cache is shared, keyed by org");

        _logs.Messages.Should().NotBeEmpty();
        _logs.Messages.Should().NotContain(m => m.Contains(TokenA, StringComparison.Ordinal) || m.Contains(TokenB, StringComparison.Ordinal));
        _console.ToString().Should().NotContain(TokenA).And.NotContain(TokenB);
    }

    /// <summary>`./orkka start` writes a profile for every workspace created in the UI; the
    /// runner it already started must serve it without a restart (hot-add).</summary>
    [Fact]
    public async Task A_profile_added_while_running_gets_its_own_session()
    {
        RunnerSupervisor.DiscoveryInterval = TimeSpan.FromMilliseconds(100);
        var (factory, _, _, _) = Factory();
        var onDisk = new List<WorkspaceProfile>();
        var supervisor = new RunnerSupervisor([], factory, NullLogger<RunnerSupervisor>.Instance, () => [.. onDisk]);

        await supervisor.StartAsync(CancellationToken.None);
        supervisor.Workspaces.Should().BeEmpty();
        lock (onDisk)
            onDisk.Add(Profile("acme", TokenA));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && supervisor.Workspaces.Count == 0)
            await Task.Delay(50);
        supervisor.NewProfiles().Should().BeEmpty("a known profile is not started twice");
        await supervisor.StopAsync(CancellationToken.None);

        supervisor.Workspaces.Select(w => w.Profile.Name).Should().Equal("acme");
    }

    [Fact]
    public async Task EachHello_CarriesItsOwnInstanceAndTrackers_AndNoToken()
    {
        new TrackerStore(_root, "acme").Upsert(new TrackerConnectionEntry
        {
            Id = "jira-acme", Kind = TrackerKinds.Jira, SiteUrl = "https://acme.atlassian.net", AddedAt = DateTimeOffset.UtcNow,
            CheckedAt = DateTimeOffset.UtcNow, Healthy = true,
        });
        var (factory, _, _, _) = Factory();
        var acme = factory.Create(Profile("acme", TokenA));
        var globex = factory.Create(Profile("globex", TokenB));

        var helloA = await acme.Session.BuildHelloAsync(CancellationToken.None);
        var helloB = await globex.Session.BuildHelloAsync(CancellationToken.None);

        helloA.InstanceId.Should().Be(acme.Session.InstanceId).And.NotBe(helloB.InstanceId);
        helloA.Trackers!.Select(t => t.Id).Should().Equal("jira-acme");
        helloB.Trackers.Should().BeEmpty();
        helloA.Capabilities.Should().Contain(RunnerCapabilities.TrackerPoll);
        var json = JsonSerializer.Serialize(helloA) + JsonSerializer.Serialize(helloB);
        json.Should().NotContain(TokenA).And.NotContain(TokenB);
    }

    [Fact]
    public async Task TheMachineWideSlots_CapConcurrentRunsAcrossSessions()
    {
        var (factory, _, slots, router) = Factory();
        var acme = factory.Create(Profile("acme", TokenA)).Session.Operations;
        var globex = factory.Create(Profile("globex", TokenB)).Session.Operations;

        var running = 0;
        var peak = 0;
        var gates = Enumerable.Range(0, 3).Select(_ => new TaskCompletionSource()).ToArray();
        var started = new ConcurrentQueue<int>();
        async IAsyncEnumerable<RunnerChunk> Run(int index, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            started.Enqueue(index);
            await gates[index].Task.WaitAsync(ct);
            Interlocked.Decrement(ref running);
            yield return new RunnerChunk("Done", "");
        }

        async Task<int> Drain(IAsyncEnumerable<RunnerChunk> stream)
        {
            var count = 0;
            await foreach (var _ in stream)
                count++;
            return count;
        }

        var runs = new[]
        {
            Drain(acme.RunGated("r0", "task-0", ct => Run(0, ct), CancellationToken.None)),
            Drain(globex.RunGated("r1", "task-1", ct => Run(1, ct), CancellationToken.None)),
            Drain(acme.RunGated("r2", "task-2", ct => Run(2, ct), CancellationToken.None)),
        };
        await WaitUntil(() => started.Count == 2);
        await Task.Delay(200);
        started.Should().HaveCount(2, "the third run waits for a slot");
        slots.Available.Should().Be(0);
        router.Resolve(started.First() == 1 ? "task-1" : "task-0").Should().NotBeNull("a running task routes to its session");

        var first = started.First();
        gates[first].SetResult();
        await WaitUntil(() => started.Count == 3);
        foreach (var gate in gates)
            gate.TrySetResult();
        (await Task.WhenAll(runs)).Should().OnlyContain(c => c == 1);

        peak.Should().Be(2);
        slots.Available.Should().Be(2);
        router.Resolve("task-0").Should().BeNull("finished runs unroute, and with two sessions there is no default");
    }

    [Fact]
    public void TheRouter_SendsATasksQuestionsToItsSession()
    {
        var router = new RunnerHostRouter();
        var a = new RunnerHostLink();
        var b = new RunnerHostLink();
        router.AddSession(a);
        router.Resolve("t1").Should().BeSameAs(a, "with one session there is nothing to choose");

        router.AddSession(b);
        router.Resolve("t1").Should().BeNull();
        using (router.Route("t1", b))
            router.Resolve("t1").Should().BeSameAs(b);
        router.Resolve("t1").Should().BeNull();
    }

    [Theory]
    [InlineData(null, 2)]
    [InlineData("3", 3)]
    [InlineData("0", 2)]
    [InlineData("abc", 2)]
    [InlineData("999", 2)]
    public void SlotCapacity_IsConfigurable_WithASaneDefault(string? value, int expected)
        => MachineRunSlots.ParseCapacity(value).Should().Be(expected);

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        condition().Should().BeTrue();
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value
            && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose()
        {
        }

        private sealed class Logger(CapturingLoggerProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
                => provider.Messages.Enqueue(formatter(state, exception) + (exception is null ? "" : " " + exception));
        }
    }
}
