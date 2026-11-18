namespace Orkka.Tests.Services;

using Orkka.Modules.Agents.Domain;
using Orkka.Modules.Agents.Infrastructure;
using Orkka.Modules.Agents.Infrastructure.Services;
using Orkka.Modules.Agents.Contracts;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

public sealed class AgentAuditLogTests
{
    private static ServiceProvider BuildProvider(string dbName)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AgentsDbContext>(o => o.UseInMemoryDatabase(dbName));
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task RecordAsync_PersistsEventWithRedactedSummary()
    {
        var provider = BuildProvider(nameof(RecordAsync_PersistsEventWithRedactedSummary));
        var audit = new AgentAuditLog(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AgentAuditLog>.Instance);

        var orgId = Ulid.NewUlid();
        var projectId = Ulid.NewUlid();
        var taskId = Ulid.NewUlid();

        await audit.RecordAsync(new AgentAuditRecord(
            orgId, projectId, AgentAuditEventType.ToolUse,
            "Bash: git push https://x-access-token:ghs_secret123@github.com/acme/app.git",
            TaskId: taskId, Tool: "Bash", Actor: "Atlas"));

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
        var evt = await db.AgentAuditEvents.SingleAsync();

        evt.OrgId.Should().Be(orgId);
        evt.ProjectId.Should().Be(projectId);
        evt.TaskId.Should().Be(taskId);
        evt.EventType.Should().Be(AgentAuditEventType.ToolUse);
        evt.Actor.Should().Be("Atlas");
        evt.Summary.Should().NotContain("ghs_secret123");
        evt.Summary.Should().Contain("***");
    }

    [Fact]
    public async Task AgentAuditEvent_IsAppendOnly_ModificationRejected()
    {
        var options = new DbContextOptionsBuilder<AgentsDbContext>()
            .UseInMemoryDatabase(nameof(AgentAuditEvent_IsAppendOnly_ModificationRejected))
            .Options;

        await using var db = new AgentsDbContext(options);
        var evt = new AgentAuditEvent
        {
            OrgId = Ulid.NewUlid(),
            ProjectId = Ulid.NewUlid(),
            EventType = AgentAuditEventType.RunStarted,
            Summary = "run started",
        };
        db.AgentAuditEvents.Add(evt);
        await db.SaveChangesAsync();

        // Tampering with an existing audit row must be rejected.
        evt.Summary = "tampered";
        var act = async () => await db.SaveChangesAsync();
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task AgentAuditEvent_IsAppendOnly_DeletionRejected()
    {
        var options = new DbContextOptionsBuilder<AgentsDbContext>()
            .UseInMemoryDatabase(nameof(AgentAuditEvent_IsAppendOnly_DeletionRejected))
            .Options;

        await using var db = new AgentsDbContext(options);
        var evt = new AgentAuditEvent
        {
            OrgId = Ulid.NewUlid(),
            ProjectId = Ulid.NewUlid(),
            EventType = AgentAuditEventType.RunStarted,
            Summary = "run started",
        };
        db.AgentAuditEvents.Add(evt);
        await db.SaveChangesAsync();

        db.AgentAuditEvents.Remove(evt);
        var act = async () => await db.SaveChangesAsync();
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task RecordAsync_ChainsEntriesPerOrg_AndSerializesMetadata()
    {
        var provider = BuildProvider(nameof(RecordAsync_ChainsEntriesPerOrg_AndSerializesMetadata));
        var audit = new AgentAuditLog(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AgentAuditLog>.Instance);

        var orgA = Ulid.NewUlid();
        var orgB = Ulid.NewUlid();

        await audit.RecordAsync(AgentAuditRecord.ByUser(orgA, "user_1", AgentAuditEventType.SkillChanged, "first",
            metadata: new { email = "a@b.c", token = "https://x-access-token:ghs_secret@github.com/x/y.git" }));
        await audit.RecordAsync(AgentAuditRecord.ByUser(orgA, "user_1", AgentAuditEventType.ScheduleChanged, "second"));
        await audit.RecordAsync(AgentAuditRecord.BySystem(orgB, AgentAuditEventType.AccountProvisioned, "other org"));

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
        var a = await db.AgentAuditEvents.Where(e => e.OrgId == orgA).OrderBy(e => e.CreatedAt).ThenBy(e => e.Id).ToListAsync();
        var b = await db.AgentAuditEvents.SingleAsync(e => e.OrgId == orgB);

        a.Should().HaveCount(2);
        a[0].PreviousHash.Should().BeNull("the first row of an org is the chain checkpoint");
        a[0].EntryHash.Should().NotBeNullOrEmpty().And.HaveLength(64);
        a[0].EntryHash.Should().Be(AuditHash.Compute(a[0]), "the stored hash is recomputable from the row");
        a[1].PreviousHash.Should().Be(a[0].EntryHash, "each row links to its predecessor in the same org");
        a[1].EntryHash.Should().Be(AuditHash.Compute(a[1]));
        b.PreviousHash.Should().BeNull("chains are per org — org B never links to org A");

        a[0].ActorKind.Should().Be(AuditActorKind.User);
        a[0].ActorUserId.Should().Be("user_1");
        a[0].MetadataJson.Should().Contain("a@b.c").And.NotContain("ghs_secret", "metadata is redacted like summaries");
        b.ActorKind.Should().Be(AuditActorKind.System);
    }

    [Fact]
    public void AuditHash_ChangesWhenAnyCoveredFieldChanges()
    {
        var evt = new AgentAuditEvent
        {
            Id = Ulid.NewUlid(),
            OrgId = Ulid.NewUlid(),
            EventType = AgentAuditEventType.ToolUse,
            Summary = "hello",
            Actor = "Atlas",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var baseline = AuditHash.Compute(evt);
        AuditHash.Compute(evt).Should().Be(baseline, "deterministic");

        evt.Summary = "hello!";
        AuditHash.Compute(evt).Should().NotBe(baseline);
        evt.Summary = "hello";
        evt.PreviousHash = new string('a', 64);
        AuditHash.Compute(evt).Should().NotBe(baseline, "the link is part of the hash");
    }
}
