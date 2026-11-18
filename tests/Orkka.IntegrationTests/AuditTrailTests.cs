namespace Orkka.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orkka.IntegrationTests.Fixtures;
using Orkka.IntegrationTests.Helpers;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Domain;
using Orkka.Modules.Agents.Features.Audit;
using Orkka.Modules.Agents.Infrastructure;
using Orkka.Modules.Organizations.Domain;
using Orkka.SharedKernel.Security;

/// <summary>
/// The hardened audit trail: management endpoints emit actor-attributed events, the org's
/// rows form a hash chain, and the list filters.
/// </summary>
[Collection("Integration")]
public sealed class AuditTrailTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<T> InScopeAsync<T>(Func<AgentsDbContext, Task<T>> f)
    {
        using var scope = factory.Services.CreateScope();
        return await f(scope.ServiceProvider.GetRequiredService<AgentsDbContext>());
    }

    [Fact]
    public async Task ManagementActions_EmitActorAttributedEvents_ThatChain()
    {
        // Two writes through the app: API key creates (Organizations).
        foreach (var name in new[] { "audit-test-1", "audit-test-2" })
        {
            var key = await _client.PostAsJsonAsync("/api/me/api-keys",
                new { name, permissions = new[] { "TasksCreate" } });
            key.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var page = await _client.GetFromJsonAsync<AuditPage>(
            "/api/audit?eventType=ApiKeyCreated&limit=50", TestJsonOptions.Default);
        var keyEvent = page!.Items.First(e => e.EventType == "ApiKeyCreated");
        keyEvent.ActorKind.Should().Be("User");
        keyEvent.ActorUserId.Should().Be(TestCurrentUser.TestUserId);
        keyEvent.EntryHash.Should().HaveLength(64);

        // Newest row links to the row before it (chain is per org, newest first in the API).
        var recent = await _client.GetFromJsonAsync<AuditPage>("/api/audit?limit=2", TestJsonOptions.Default);
        recent!.Items.Should().HaveCount(2);
        recent.Items[0].PreviousHash.Should().Be(recent.Items[1].EntryHash);
    }

    [Fact]
    public async Task Rows_ChainPerOrg()
    {
        // A throwaway org, so the chain starts empty.
        var orgId = Ulid.NewUlid();
        using (var scope = factory.Services.CreateScope())
        {
            var audit = scope.ServiceProvider.GetRequiredService<IAgentAuditLog>();
            await audit.RecordAsync(AgentAuditRecord.ByUser(orgId, "user_x", AgentAuditEventType.SkillChanged, "one"));
            await audit.RecordAsync(AgentAuditRecord.ByUser(orgId, "user_x", AgentAuditEventType.SkillChanged, "two"));
            await audit.RecordAsync(AgentAuditRecord.ByUser(orgId, "user_x", AgentAuditEventType.SkillChanged, "three"));
        }

        var rows = await InScopeAsync(db => db.AgentAuditEvents.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.OrgId == orgId).OrderBy(e => e.CreatedAt).ThenBy(e => e.Id).ToListAsync());
        rows.Should().HaveCount(3);
        rows[1].PreviousHash.Should().Be(rows[0].EntryHash);
        rows[2].PreviousHash.Should().Be(rows[1].EntryHash);
    }

    [Fact]
    public async Task List_FiltersByTypeActorAndTime_AndPages()
    {
        var orgId = Ulid.NewUlid();
        using (var scope = factory.Services.CreateScope())
        {
            var audit = scope.ServiceProvider.GetRequiredService<IAgentAuditLog>();
            for (var i = 0; i < 5; i++)
                await audit.RecordAsync(AgentAuditRecord.ByUser(orgId, "alice", AgentAuditEventType.SkillChanged, $"skill {i}"));
            await audit.RecordAsync(AgentAuditRecord.ByUser(orgId, "bob", AgentAuditEventType.ScheduleChanged, "schedule"));
            await audit.RecordAsync(AgentAuditRecord.BySystem(orgId, AgentAuditEventType.AccountProvisioned, "org"));
        }

        try
        {
            TestCurrentUser.CurrentOrgId = orgId;

            var all = await _client.GetFromJsonAsync<AuditPage>("/api/audit", TestJsonOptions.Default);
            all!.Items.Should().HaveCount(7);
            all.NextCursor.Should().BeNull();

            var byType = await _client.GetFromJsonAsync<AuditPage>("/api/audit?eventType=SkillChanged", TestJsonOptions.Default);
            byType!.Items.Should().HaveCount(5).And.OnlyContain(e => e.EventType == "SkillChanged");

            var byActor = await _client.GetFromJsonAsync<AuditPage>("/api/audit?actor=bob", TestJsonOptions.Default);
            byActor!.Items.Should().ContainSingle().Which.ActorUserId.Should().Be("bob");

            var future = await _client.GetFromJsonAsync<AuditPage>(
                $"/api/audit?from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(1).ToString("O"))}", TestJsonOptions.Default);
            future!.Items.Should().BeEmpty();

            // Cursor paging: 3 + 3 + 1.
            var p1 = await _client.GetFromJsonAsync<AuditPage>("/api/audit?limit=3", TestJsonOptions.Default);
            p1!.Items.Should().HaveCount(3);
            p1.NextCursor.Should().NotBeNull();
            var p2 = await _client.GetFromJsonAsync<AuditPage>($"/api/audit?limit=3&cursor={p1.NextCursor}", TestJsonOptions.Default);
            p2!.Items.Should().HaveCount(3);
            var p3 = await _client.GetFromJsonAsync<AuditPage>($"/api/audit?limit=3&cursor={p2.NextCursor}", TestJsonOptions.Default);
            p3!.Items.Should().HaveCount(1);
            p3.NextCursor.Should().BeNull();
            p1.Items.Concat(p2.Items).Concat(p3.Items).Select(e => e.Id).Should().OnlyHaveUniqueItems();

            var bad = await _client.GetAsync("/api/audit?eventType=NotAThing");
            bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        finally
        {
            TestCurrentUser.Reset();
        }
    }


    [Fact]
    public async Task Audit_RequiresAuditViewPermission()
    {
        try
        {
            TestCurrentUser.CurrentRole = OrgRole.Member;
            TestCurrentUser.CurrentPermissions = new HashSet<Permission> { Permission.TasksCreate };

            (await _client.GetAsync("/api/audit")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

            TestCurrentUser.CurrentPermissions = new HashSet<Permission> { Permission.AuditView };
            (await _client.GetAsync("/api/audit")).StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            TestCurrentUser.Reset();
        }
    }
}
