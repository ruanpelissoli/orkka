namespace Orkka.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using Orkka.IntegrationTests.Fixtures;
using Orkka.IntegrationTests.Helpers;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Runners.Contracts;
using Orkka.Modules.Runners.Features.Presence;
using Orkka.Modules.Runners.Infrastructure;
using Orkka.Modules.Runners.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The runner's last Hello must outlive its connection. Presence is deleted on disconnect
/// (its WorkerInstance/ConnectionId drive claim affinity), so the routing decision needs a
/// durable twin — otherwise every task created during a reconnect would be routed as if the
/// user had no subscriptions at all.
/// </summary>
[Collection("Integration")]
public sealed class RunnerProviderSnapshotTests(OrkkaApiFactory factory) : IAsyncLifetime
{
    private const string Machine = "ruan-laptop";

    private static RunnerHello Hello(params RunnerProviderStatus[] providers) => new(
        ProtocolVersion: 1, MachineName: Machine,
        ClaudeCliFound: true, ClaudeAuthenticated: true,
        ActiveRunIds: [], Providers: providers, DaemonVersion: "1.2.3");

    private static readonly RunnerProviderStatus[] ClaudeReadyCodexSignedOut =
    [
        new(RunnerProviders.Claude, CliFound: true, Authenticated: true),
        new(RunnerProviders.Codex, CliFound: true, Authenticated: false, Models: null, Detail: "codex is not logged in"),
    ];

    public Task InitializeAsync() => ClearSharedUserRowsAsync();

    public Task DisposeAsync() => ClearSharedUserRowsAsync();

    /// <summary>The /api/me/runner cases write rows for the suite's shared user.</summary>
    private async Task ClearSharedUserRowsAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RunnersDbContext>();
        var orgId = TestCurrentUser.TestOrgId;
        var userId = TestCurrentUser.TestUserId;
        await db.RunnerProviderSnapshots.IgnoreQueryFilters()
            .Where(s => s.OrgId == orgId && s.UserId == userId).ExecuteDeleteAsync();
        await db.RunnerPresences.IgnoreQueryFilters()
            .Where(p => p.OrgId == orgId && p.UserId == userId).ExecuteDeleteAsync();
    }

    private static async Task UpsertAsync(
        IServiceProvider services, Ulid orgId, string userId, string connectionId, RunnerHello hello)
    {
        using var scope = services.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<RunnerPresenceWriter>();
        await writer.UpsertAsync(orgId, userId, Ulid.NewUlid(), "orkka-worker-0", connectionId, hello);
    }

    private static async Task RemoveAsync(IServiceProvider services, Ulid orgId, string userId, string connectionId)
    {
        using var scope = services.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<RunnerPresenceWriter>();
        await writer.RemoveAsync(orgId, userId, connectionId);
    }

    [Fact]
    public async Task Upsert_WritesTheSnapshot_AndRemove_KeepsIt()
    {
        var orgId = Ulid.NewUlid();
        var userId = $"user_{Ulid.NewUlid()}";

        await UpsertAsync(factory.Services, orgId, userId, "conn-1", Hello(ClaudeReadyCodexSignedOut));

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RunnersDbContext>();
            (await db.RunnerPresences.IgnoreQueryFilters().AnyAsync(p => p.OrgId == orgId && p.UserId == userId))
                .Should().BeTrue();
            var snapshot = await db.RunnerProviderSnapshots.IgnoreQueryFilters()
                .SingleAsync(s => s.OrgId == orgId && s.UserId == userId);
            snapshot.MachineName.Should().Be(Machine);
            snapshot.DaemonVersion.Should().Be("1.2.3");
            snapshot.CapturedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        }

        await RemoveAsync(factory.Services, orgId, userId, "conn-1");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RunnersDbContext>();
            (await db.RunnerPresences.IgnoreQueryFilters().AnyAsync(p => p.OrgId == orgId && p.UserId == userId))
                .Should().BeFalse("presence is torn down on disconnect");

            var fleet = scope.ServiceProvider.GetRequiredService<IRunnerFleet>();
            var coverage = await fleet.GetProviderCoverageAsync(orgId, userId);
            coverage.Should().NotBeNull("the last Hello survives the disconnect");
            coverage!.Online.Should().BeFalse();
            coverage.MachineName.Should().Be(Machine);
            coverage.IsReady(RunnerProviders.Claude).Should().BeTrue();
            coverage.IsReady(RunnerProviders.Codex).Should().BeFalse("installed but signed out");
            coverage.Find(RunnerProviders.Codex)!.Detail.Should().Be("codex is not logged in");
        }
    }

    [Fact]
    public async Task WhileOnline_CoverageIsTheLivePresence()
    {
        var orgId = Ulid.NewUlid();
        var userId = $"user_{Ulid.NewUlid()}";

        await UpsertAsync(factory.Services, orgId, userId, "conn-1", Hello(ClaudeReadyCodexSignedOut));

        using var scope = factory.Services.CreateScope();
        var coverage = await scope.ServiceProvider.GetRequiredService<IRunnerFleet>()
            .GetProviderCoverageAsync(orgId, userId);

        coverage!.Online.Should().BeTrue();
        coverage.IsReady(RunnerProviders.Claude).Should().BeTrue();
    }

    [Fact]
    public async Task EveryHello_OverwritesTheSnapshot()
    {
        var orgId = Ulid.NewUlid();
        var userId = $"user_{Ulid.NewUlid()}";

        await UpsertAsync(factory.Services, orgId, userId, "conn-1", Hello(ClaudeReadyCodexSignedOut));
        DateTimeOffset first;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RunnersDbContext>();
            first = (await db.RunnerProviderSnapshots.IgnoreQueryFilters()
                .SingleAsync(s => s.OrgId == orgId && s.UserId == userId)).CapturedAt;
        }

        // The user signs in to Codex and the next re-Hello reports it.
        await UpsertAsync(factory.Services, orgId, userId, "conn-1", Hello(
            new RunnerProviderStatus(RunnerProviders.Claude, true, true),
            new RunnerProviderStatus(RunnerProviders.Codex, true, true)));
        await RemoveAsync(factory.Services, orgId, userId, "conn-1");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RunnersDbContext>();
            (await db.RunnerProviderSnapshots.IgnoreQueryFilters().CountAsync(s => s.OrgId == orgId && s.UserId == userId))
                .Should().Be(1, "one memory per user");
            var snapshot = await db.RunnerProviderSnapshots.IgnoreQueryFilters()
                .SingleAsync(s => s.OrgId == orgId && s.UserId == userId);
            snapshot.CapturedAt.Should().BeOnOrAfter(first);

            var coverage = await scope.ServiceProvider.GetRequiredService<IRunnerFleet>()
                .GetProviderCoverageAsync(orgId, userId);
            coverage!.IsReady(RunnerProviders.Codex).Should().BeTrue();
        }
    }

    [Fact]
    public async Task AStaleRemove_FromAReplacedConnection_KeepsBothRows()
    {
        var orgId = Ulid.NewUlid();
        var userId = $"user_{Ulid.NewUlid()}";

        await UpsertAsync(factory.Services, orgId, userId, "conn-1", Hello(ClaudeReadyCodexSignedOut));
        await UpsertAsync(factory.Services, orgId, userId, "conn-2", Hello(ClaudeReadyCodexSignedOut));
        await RemoveAsync(factory.Services, orgId, userId, "conn-1");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RunnersDbContext>();
        (await db.RunnerPresences.IgnoreQueryFilters().AnyAsync(p => p.OrgId == orgId && p.UserId == userId))
            .Should().BeTrue("the live connection's row must survive the old connection's teardown");
        (await db.RunnerProviderSnapshots.IgnoreQueryFilters().AnyAsync(s => s.OrgId == orgId && s.UserId == userId))
            .Should().BeTrue();
    }

    [Fact]
    public async Task ForgetProviders_DeletesTheSnapshot_SoCoverageIsUnknownAgain()
    {
        var orgId = Ulid.NewUlid();
        var userId = $"user_{Ulid.NewUlid()}";

        await UpsertAsync(factory.Services, orgId, userId, "conn-1", Hello(ClaudeReadyCodexSignedOut));
        await RemoveAsync(factory.Services, orgId, userId, "conn-1");

        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<RunnerPresenceWriter>().ForgetProvidersAsync(orgId, userId);

        (await scope.ServiceProvider.GetRequiredService<IRunnerFleet>().GetProviderCoverageAsync(orgId, userId))
            .Should().BeNull("a forgotten machine must not steer where tasks run");
    }

    // ── GET /api/me/runner ──────────────────────────────────────────────────

    [Fact]
    public async Task MyRunner_Offline_ReturnsTheLastKnownProviders()
    {
        var orgId = TestCurrentUser.TestOrgId;
        var userId = TestCurrentUser.TestUserId;
        await UpsertAsync(factory.Services, orgId, userId, "conn-1", Hello(ClaudeReadyCodexSignedOut));
        await RemoveAsync(factory.Services, orgId, userId, "conn-1");

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/me/runner");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var me = (await response.Content.ReadFromJsonAsync<MyRunnerResponse>(TestJsonOptions.Default))!;
        me.Online.Should().BeFalse();
        me.MachineName.Should().BeNull("there is no live presence");
        me.Providers.Should().BeNull();
        me.LastKnownMachineName.Should().Be(Machine);
        me.LastKnownAt.Should().NotBeNull();
        me.LastKnownAt!.Value.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        me.LastKnownProviders.Should().NotBeNull();
        me.LastKnownProviders!.Should().ContainSingle(p => p.Provider == RunnerProviders.Claude)
            .Which.Authenticated.Should().BeTrue();
        me.LastKnownProviders.Should().ContainSingle(p => p.Provider == RunnerProviders.Codex)
            .Which.Should().Match<RunnerProviderDto>(p => p.CliFound && !p.Authenticated && p.Detail == "codex is not logged in");
    }

    [Fact]
    public async Task MyRunner_Online_MirrorsTheLiveProvidersIntoLastKnown()
    {
        var orgId = TestCurrentUser.TestOrgId;
        var userId = TestCurrentUser.TestUserId;
        await UpsertAsync(factory.Services, orgId, userId, "conn-1", Hello(ClaudeReadyCodexSignedOut));

        using var client = factory.CreateClient();
        var me = (await client.GetFromJsonAsync<MyRunnerResponse>("/api/me/runner", TestJsonOptions.Default))!;

        me.Online.Should().BeTrue();
        me.MachineName.Should().Be(Machine);
        me.LastKnownMachineName.Should().Be(Machine);
        me.LastKnownAt.Should().Be(me.LastSeenAt);
        me.LastKnownProviders.Should().BeEquivalentTo(me.Providers);
    }

    [Fact]
    public async Task MyRunner_NeverConnected_HasNoLastKnown()
    {
        using var client = factory.CreateClient();
        var me = (await client.GetFromJsonAsync<MyRunnerResponse>("/api/me/runner", TestJsonOptions.Default))!;

        me.Online.Should().BeFalse();
        me.LastKnownProviders.Should().BeNull();
        me.LastKnownAt.Should().BeNull();
        me.LastKnownMachineName.Should().BeNull();
    }

    [Fact]
    public async Task MeRunner_ListsTheCommandsTheMachineRefused()
    {
        var orgId = TestCurrentUser.TestOrgId;
        var userId = TestCurrentUser.TestUserId;
        await UpsertAsync(factory.Services, orgId, userId, "conn-1", Hello(ClaudeReadyCodexSignedOut) with
        {
            PendingCommandApprovals =
            [
                new CommandApproval("c0ffee12", "github.com/acme/shop", "npm run seed", CommandKind.Setup, Machine),
            ],
        });

        using var client = factory.CreateClient();
        var me = (await client.GetFromJsonAsync<MyRunnerResponse>("/api/me/runner", TestJsonOptions.Default))!;

        me.PendingCommandApprovals.Should().ContainSingle().Which.Should()
            .Be(new PendingCommandApprovalDto("c0ffee12", "github.com/acme/shop", "npm run seed", "Setup", Machine));

        // A runner that reports none clears the list.
        await UpsertAsync(factory.Services, orgId, userId, "conn-1", Hello(ClaudeReadyCodexSignedOut) with { PendingCommandApprovals = [] });
        (await client.GetFromJsonAsync<MyRunnerResponse>("/api/me/runner", TestJsonOptions.Default))!
            .PendingCommandApprovals.Should().BeEmpty();
    }
}
