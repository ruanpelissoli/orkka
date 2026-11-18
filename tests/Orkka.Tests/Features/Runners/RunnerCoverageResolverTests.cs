namespace Orkka.Tests.Features.Runners;

using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Runners.Contracts;
using Orkka.Modules.Runners.Domain;
using Orkka.Modules.Runners.Infrastructure;
using Orkka.Modules.Runners.PublicApi;
using Orkka.Modules.Tasks.Infrastructure.Services;

/// <summary>
/// Every task runs on its creator's runner; the coverage reading only decides what they are
/// told. Coverage goes through the real <see cref="RunnerFleet"/> over an in-memory
/// <see cref="RunnersDbContext"/>, so the live-presence-versus-stale-snapshot rule is exercised
/// on the rows the hub actually writes rather than on a fake fleet.
/// </summary>
public sealed class RunnerCoverageResolverTests : IDisposable
{
    private const string Claude = "claude-sonnet-5";
    private const string Gpt = "gpt-5.6-sol";
    private const string Copilot = "copilot/claude-sonnet-5";
    private const string Machine = "ruan-laptop";
    private const string UserId = "user_runner_owner";

    private static readonly Ulid OrgId = Ulid.NewUlid();

    private readonly RunnersDbContext _db;

    public RunnerCoverageResolverTests()
    {
        var options = new DbContextOptionsBuilder<RunnersDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new RunnersDbContext(options, currentUser: null);
    }

    public void Dispose() => _db.Dispose();

    private Task<RunnerCoverageDecision> ResolveAsync(
        string? model, string? agentModel = null, string? projectDefault = null)
        => new RunnerCoverageResolver(new RunnerFleet(_db)).ResolveAsync(
            new RunnerCoverageQuery(OrgId, UserId, model, agentModel, projectDefault));

    [Fact]
    public async Task ACoveredProvider_RunsOnTheRunner_WithNothingToSay()
    {
        await SeedSnapshotAsync(DateTimeOffset.UtcNow, Ready(RunnerProviders.Claude));

        var d = await ResolveAsync(Claude);

        d.RunnerUserId.Should().Be(UserId);
        d.CoveredByRunner.Should().BeTrue();
        d.Reason.Should().Be(RunnerCoverageReason.RunnerCoversProvider);
        d.Explanation.Should().BeNull();
    }

    [Fact]
    public async Task ASignedOutProvider_StillRunsOnTheRunner_AndSaysSo()
    {
        await SeedSnapshotAsync(DateTimeOffset.UtcNow,
            Ready(RunnerProviders.Claude), Installed(RunnerProviders.Codex, "codex is not logged in"));

        var d = await ResolveAsync(Gpt);

        d.RunnerUserId.Should().Be(UserId, "there is nowhere else for it to run");
        d.Reason.Should().Be(RunnerCoverageReason.ProviderNotSignedIn);
        d.ProviderDetail.Should().Be("codex is not logged in");
        d.Explanation.Should().NotBeNull()
            .And.Contain(Gpt)
            .And.Contain(Machine)
            .And.Contain("(as of just now)")
            .And.NotContain("Billed");
    }

    [Theory]
    [InlineData(Claude)]
    [InlineData(Copilot)]
    public async Task ARunnerThatNeverConnected_StillGetsTheTask(string model)
    {
        var d = await ResolveAsync(model);

        d.RunnerUserId.Should().Be(UserId);
        d.Reason.Should().Be(RunnerCoverageReason.NoRunnerCoverageKnown);
        d.Explanation.Should().BeNull();
    }

    [Fact]
    public async Task ProviderAbsentFromTheHello_IsNotSignedIn()
    {
        await SeedSnapshotAsync(DateTimeOffset.UtcNow, Ready(RunnerProviders.Claude));

        var d = await ResolveAsync(Gpt);

        d.Reason.Should().Be(RunnerCoverageReason.ProviderNotSignedIn);
        d.ProviderDetail.Should().BeNull();
        d.Explanation.Should().NotBeNull();
    }

    [Fact]
    public async Task CliMissing_IsNotSignedIn_EvenIfAuthenticatedIsReported()
    {
        // The copilot .cmd-shim incident: "authenticated" without a spawnable CLI still fails.
        await SeedSnapshotAsync(DateTimeOffset.UtcNow,
            new RunnerProviderStatus(RunnerProviders.Codex, CliFound: false, Authenticated: true));

        (await ResolveAsync(Gpt)).Reason.Should().Be(RunnerCoverageReason.ProviderNotSignedIn);
    }

    [Fact]
    public async Task LivePresence_BeatsAStaleSnapshot()
    {
        await SeedSnapshotAsync(DateTimeOffset.UtcNow.AddDays(-1),
            Ready(RunnerProviders.Claude), Installed(RunnerProviders.Codex));
        var presence = await SeedPresenceAsync(DateTimeOffset.UtcNow,
            Ready(RunnerProviders.Claude), Ready(RunnerProviders.Codex));

        var d = await ResolveAsync(Gpt);

        d.Reason.Should().Be(RunnerCoverageReason.RunnerCoversProvider,
            "the user signed in to Codex since the snapshot, and the live Hello knows it");
        d.CoverageAsOf.Should().Be(presence.LastSeenAt);
        d.RunnerMachineName.Should().Be(presence.MachineName);
    }

    [Fact]
    public async Task StalePresence_YieldsToTheSnapshot()
    {
        var snapshot = await SeedSnapshotAsync(DateTimeOffset.UtcNow.AddMinutes(-5),
            Ready(RunnerProviders.Claude));
        // Older than RunnerPresenceRules.OnlineWindow: a dead pod's leftover row.
        await SeedPresenceAsync(DateTimeOffset.UtcNow - RunnerPresenceRules.OnlineWindow - TimeSpan.FromMinutes(1),
            Ready(RunnerProviders.Claude), Ready(RunnerProviders.Codex));

        var d = await ResolveAsync(Gpt);

        d.Reason.Should().Be(RunnerCoverageReason.ProviderNotSignedIn);
        d.CoverageAsOf.Should().Be(snapshot.CapturedAt);
        d.RunnerMachineName.Should().Be(Machine);
    }

    [Fact]
    public async Task StalePresence_WithoutASnapshot_IsUnknownCoverage()
    {
        await SeedPresenceAsync(DateTimeOffset.UtcNow.AddHours(-1), Ready(RunnerProviders.Claude));

        (await ResolveAsync(Gpt)).Reason.Should().Be(RunnerCoverageReason.NoRunnerCoverageKnown);
    }

    [Fact]
    public async Task ModelPrecedence_FlowsIntoTheDecision()
    {
        await SeedSnapshotAsync(DateTimeOffset.UtcNow, Ready(RunnerProviders.Claude));

        var agentWins = await ResolveAsync(null, agentModel: Gpt, projectDefault: Claude);
        agentWins.Model.Should().Be(Gpt);
        agentWins.Provider.Should().Be(AiProvider.OpenAI);
        agentWins.Reason.Should().Be(RunnerCoverageReason.ProviderNotSignedIn);

        var overrideWins = await ResolveAsync(Claude, agentModel: Gpt, projectDefault: Gpt);
        overrideWins.Model.Should().Be(Claude);
        overrideWins.Reason.Should().Be(RunnerCoverageReason.RunnerCoversProvider);

        var blankOverrideIgnored = await ResolveAsync("   ", agentModel: null, projectDefault: Claude);
        blankOverrideIgnored.Model.Should().Be(Claude);
        blankOverrideIgnored.Reason.Should().Be(RunnerCoverageReason.RunnerCoversProvider);
    }

    [Fact]
    public async Task NoModelAnywhere_DefaultsToAnthropic()
    {
        await SeedSnapshotAsync(DateTimeOffset.UtcNow, Ready(RunnerProviders.Claude));

        var d = await ResolveAsync(null);

        d.Model.Should().BeNull();
        d.Provider.Should().Be(AiProvider.Anthropic);
        d.Reason.Should().Be(RunnerCoverageReason.RunnerCoversProvider);
    }

    [Fact]
    public void IsReady_RequiresBothTheCliAndTheLogin()
    {
        var coverage = new RunnerProviderCoverage(Machine, DateTimeOffset.UtcNow, Online: false,
        [
            Ready(RunnerProviders.Claude),
            Installed(RunnerProviders.Codex),
            new RunnerProviderStatus(RunnerProviders.Gemini, CliFound: false, Authenticated: false),
            new RunnerProviderStatus(RunnerProviders.Copilot, CliFound: false, Authenticated: true),
        ]);

        coverage.IsReady(RunnerProviders.Claude).Should().BeTrue();
        coverage.IsReady(RunnerProviders.Codex).Should().BeFalse("installed but signed out");
        coverage.IsReady(RunnerProviders.Gemini).Should().BeFalse("not installed");
        coverage.IsReady(RunnerProviders.Copilot).Should().BeFalse("no CLI to spawn");
        coverage.IsReady(RunnerProviders.Grok).Should().BeFalse("not reported at all");
        coverage.IsReady("CLAUDE").Should().BeTrue("CLI keys compare case-insensitively");
    }

    [Fact]
    public async Task TheDefaultResolver_RunsOnTheAskingUser_WithUnknownCoverage()
    {
        var d = await new UnknownRunnerCoverageResolver().ResolveAsync(
            new RunnerCoverageQuery(OrgId, UserId, null, Gpt, Claude));

        d.RunnerUserId.Should().Be(UserId);
        d.Model.Should().Be(Gpt);
        d.Provider.Should().Be(AiProvider.OpenAI);
        d.Reason.Should().Be(RunnerCoverageReason.NoRunnerCoverageKnown);
        d.Explanation.Should().BeNull();
    }

    [Fact]
    public void Explain_WithoutMachineOrAge_StillReads()
    {
        var d = new RunnerCoverageDecision(UserId, AiProvider.Google, "gemini-3.6-flash",
            CoveredByRunner: false, RunnerCoverageReason.ProviderNotSignedIn,
            CoverageAsOf: null, RunnerMachineName: null, ProviderDetail: null);

        d.Explanation.Should().Contain("gemini-3.6-flash (Gemini), which isn't signed in on your runner. Sign in");
    }

    // ── Seeding ─────────────────────────────────────────────────────────────

    private static RunnerProviderStatus Ready(string cli) => new(cli, CliFound: true, Authenticated: true);

    private static RunnerProviderStatus Installed(string cli, string? detail = null)
        => new(cli, CliFound: true, Authenticated: false, Models: null, Detail: detail);

    private async Task<RunnerProviderSnapshot> SeedSnapshotAsync(
        DateTimeOffset capturedAt, params RunnerProviderStatus[] providers)
    {
        var snapshot = new RunnerProviderSnapshot
        {
            Id = Ulid.NewUlid(),
            OrgId = OrgId,
            UserId = UserId,
            MachineName = Machine,
            ProvidersJson = JsonSerializer.Serialize(providers),
            CapturedAt = capturedAt,
        };
        _db.RunnerProviderSnapshots.Add(snapshot);
        await _db.SaveChangesAsync();
        return snapshot;
    }

    private async Task<RunnerPresence> SeedPresenceAsync(
        DateTimeOffset lastSeenAt, params RunnerProviderStatus[] providers)
    {
        var presence = new RunnerPresence
        {
            Id = Ulid.NewUlid(),
            OrgId = OrgId,
            UserId = UserId,
            WorkerInstance = "orkka-worker-0",
            ConnectionId = "conn-1",
            MachineName = Machine + "-live",
            ConnectedAt = lastSeenAt,
            LastSeenAt = lastSeenAt,
            ProvidersJson = JsonSerializer.Serialize(providers),
        };
        _db.RunnerPresences.Add(presence);
        await _db.SaveChangesAsync();
        return presence;
    }
}
