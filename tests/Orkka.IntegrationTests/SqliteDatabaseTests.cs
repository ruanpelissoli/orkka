namespace Orkka.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orkka.IntegrationTests.Fixtures;
using Orkka.IntegrationTests.Helpers;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Domain;
using Orkka.Modules.Agents.Infrastructure;
using Orkka.Modules.Agents.Infrastructure.Services;
using Orkka.Modules.Organizations.Domain;
using Orkka.Modules.Organizations.Infrastructure;
using Orkka.Modules.Projects.Features.ProjectRepos;
using Orkka.Modules.Projects.Features.Projects;
using Orkka.Modules.Tasks.Features.Tasks;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.Modules.Usage.Domain;
using Orkka.Modules.Usage.Infrastructure;

/// <summary>
/// What moving the platform database to one SQLite file had to keep true: date ordering and
/// lease comparisons over the UTC-ticks columns, ULID order in the stored ids (cursors depend on
/// it), FTS5 memory ranking, task numbers under concurrent creation (one writer), the org-wide
/// permission uniqueness SQLite does not give for NULLs, exact decimal sums, and a linear audit
/// hash chain under concurrent writers.
/// </summary>
[Collection("Integration")]
public sealed class SqliteDatabaseTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<(Ulid ProjectId, Ulid RepoId)> SeedProjectAndRepo(string name = "Sqlite Test Project")
    {
        var projectResp = await _client.PostAsJsonAsync("/api/projects", new CreateProjectRequest(name, "desc"));
        var project = await projectResp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);
        var repoResp = await _client.PostAsJsonAsync($"/api/projects/{project!.Id}/repos",
            new AddProjectRepoRequest("sqlite-repo", "https://github.com/test/sqlite.git", "main", null, null));
        var repo = await repoResp.Content.ReadFromJsonAsync<ProjectRepoResponse>(TestJsonOptions.Default);
        return (project.Id, repo!.Id);
    }

    // ── Task numbers: one writer, no deadlock ────────────────────────────────────────

    [Fact]
    public async Task TwentyParallelTaskCreations_GetUniqueContiguousNumbers_WithoutBusyErrors()
    {
        var (projectId, repoId) = await SeedProjectAndRepo("Parallel Numbers");

        var responses = await Task.WhenAll(Enumerable.Range(1, 20).Select(i =>
            _client.PostAsJsonAsync($"/api/projects/{projectId}/tasks",
                new CreateTaskRequest(repoId, $"Parallel {i}", "Description", "Medium"))));

        responses.Select(r => r.StatusCode).Should().AllBeEquivalentTo(HttpStatusCode.Created,
            "a reservation inside another context's open write transaction would wait on it until SQLITE_BUSY");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var numbers = await db.Tasks.AsNoTracking()
            .Where(t => t.ProjectId == projectId)
            .Select(t => t.Number)
            .ToListAsync();
        numbers.Should().BeEquivalentTo(Enumerable.Range(1, 20));
    }

    // ── DateTimeOffset as UTC ticks ──────────────────────────────────────────────────

    [Fact]
    public async Task DateTimeOffsets_OrderAndCompareByInstant_WhateverTheirOffset()
    {
        var (projectId, repoId) = await SeedProjectAndRepo("Dates");
        var ids = new List<Ulid>();
        for (var i = 0; i < 3; i++)
        {
            var response = await _client.PostAsJsonAsync($"/api/projects/{projectId}/tasks",
                new CreateTaskRequest(repoId, $"Dated {i}", "d", "Medium"));
            ids.Add((await response.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default))!.Id);
        }

        // Instants 10:00Z, 11:00Z, 12:00Z written with offsets that reverse their wall-clock order:
        // as text, "13:00+05:00" < "08:00-03:00" < "12:00+00:00" sorts wrong.
        var earliest = new DateTimeOffset(2026, 11, 1, 15, 0, 0, TimeSpan.FromHours(5));   // 10:00Z
        var middle = new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.FromHours(-3));     // 11:00Z
        var latest = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);             // 12:00Z

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        await db.Tasks.Where(t => t.Id == ids[0]).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.StartedAt, latest).SetProperty(t => t.LeaseExpiresAt, latest));
        await db.Tasks.Where(t => t.Id == ids[1]).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.StartedAt, earliest).SetProperty(t => t.LeaseExpiresAt, earliest));
        await db.Tasks.Where(t => t.Id == ids[2]).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.StartedAt, middle).SetProperty(t => t.LeaseExpiresAt, middle));

        var ordered = await db.Tasks.AsNoTracking()
            .Where(t => ids.Contains(t.Id))
            .OrderBy(t => t.StartedAt)
            .Select(t => new { t.Id, t.StartedAt })
            .ToListAsync();
        ordered.Select(t => t.Id).Should().Equal(ids[1], ids[2], ids[0]);
        ordered[0].StartedAt.Should().Be(earliest, "the instant survives the round trip");
        ordered[0].StartedAt!.Value.Offset.Should().Be(TimeSpan.Zero, "values come back in UTC");

        // A lease claim's comparison, with the "now" in yet another offset: 11:30Z.
        var now = new DateTimeOffset(2026, 11, 1, 20, 30, 0, TimeSpan.FromHours(9));
        var expired = await db.Tasks.AsNoTracking()
            .Where(t => ids.Contains(t.Id) && t.LeaseExpiresAt != null && t.LeaseExpiresAt <= now)
            .Select(t => t.Id)
            .ToListAsync();
        expired.Should().BeEquivalentTo([ids[1], ids[2]]);

        var newest = await db.Tasks.AsNoTracking().Where(t => ids.Contains(t.Id)).MaxAsync(t => t.StartedAt);
        newest.Should().Be(latest);

        // DateTimeOffset.UtcNow inside a query is this process's clock, converted like any value.
        var startedBeforeNow = await db.Tasks.AsNoTracking()
            .CountAsync(t => ids.Contains(t.Id) && t.StartedAt < DateTimeOffset.UtcNow);
        startedBeforeNow.Should().Be(3);
    }

    // ── ULID order is stored order (cursors) ─────────────────────────────────────────

    [Fact]
    public async Task UlidIds_SortInTheDatabaseExactlyAsUlidsSort_SoCursorsPageCorrectly()
    {
        var projectId = Ulid.NewUlid();
        var random = new Random(42);
        var baseTime = DateTimeOffset.UtcNow.AddDays(-1);
        // Many share a millisecond, so the random half of the ULID decides those.
        var ids = Enumerable.Range(0, 200)
            .Select(i => Ulid.NewUlid(baseTime.AddMilliseconds(random.Next(0, 50))))
            .ToList();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
        foreach (var id in ids.OrderBy(_ => random.Next()))
        {
            db.ProjectMemories.Add(new ProjectMemory
            {
                Id = id, OrgId = TestCurrentUser.TestOrgId, ProjectId = projectId,
                Type = MemoryType.Learning, Title = "t", Content = "c",
            });
        }
        await db.SaveChangesAsync();

        var stored = await db.ProjectMemories.AsNoTracking()
            .Where(m => m.ProjectId == projectId)
            .OrderBy(m => m.Id)
            .Select(m => m.Id)
            .ToListAsync();
        stored.Should().Equal(ids.Order());

        var cursor = ids.Order().ElementAt(120);
        var page = await db.ProjectMemories.AsNoTracking()
            .Where(m => m.ProjectId == projectId && m.Id.CompareTo(cursor) < 0)
            .Select(m => m.Id)
            .ToListAsync();
        page.Should().BeEquivalentTo(ids.Order().Take(120));
    }

    // ── FTS5 memory retrieval ────────────────────────────────────────────────────────

    [Fact]
    public async Task MemoryRetrieval_RulesFirst_ThenTheRelevantMemory_AheadOfNewerIrrelevantOnes()
    {
        var projectId = Ulid.NewUlid();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();

        ProjectMemory Memory(MemoryType type, string title, string content, bool pinned = false) => new()
        {
            Id = Ulid.NewUlid(), OrgId = TestCurrentUser.TestOrgId, ProjectId = projectId,
            Type = type, Title = title, Content = content, IsPinned = pinned,
        };

        var pinned = Memory(MemoryType.Learning, "Deploy freeze", "No deploys on Fridays.", pinned: true);
        var rule = Memory(MemoryType.Convention, "Naming", "Services end in Service.");
        var strong = Memory(MemoryType.Learning, "Login button", "The login button must redirect through the OAuth callback.");
        db.ProjectMemories.AddRange(pinned, rule, strong);
        await db.SaveChangesAsync();
        // Newer than the strong match, so recency alone would put them first.
        var weak = Memory(MemoryType.Learning, "Session cookies",
            "Cookies expire after a day; the login page shows a banner, among many other unrelated details about caching and headers.");
        db.ProjectMemories.Add(weak);
        await db.SaveChangesAsync();
        var noise = Enumerable.Range(0, 5)
            .Select(i => Memory(MemoryType.Learning, $"Unrelated {i}", "Database vacuum schedule and backups."))
            .ToList();
        db.ProjectMemories.AddRange(noise);
        await db.SaveChangesAsync();

        var retrieved = await new MemoryRetriever(db).RetrieveAsync(
            new MemoryQuery(TestCurrentUser.TestOrgId, projectId, null, "Fix the login button redirects", Take: 4));

        retrieved.Select(m => m.Id).Should().Equal(
            [pinned.Id, rule.Id, strong.Id, weak.Id],
            "standing rules first, then by bm25 relevance — the better match ahead of the newer, weaker one — and noise never");
    }

    [Fact]
    public async Task MemorySearch_FollowsEditsAndDeletes_FoldsAccents_AndSurvivesGarbage()
    {
        var projectId = Ulid.NewUlid();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
        var memory = new ProjectMemory
        {
            Id = Ulid.NewUlid(), OrgId = TestCurrentUser.TestOrgId, ProjectId = projectId,
            Type = MemoryType.Learning, Title = "Café menu", Content = "Prices are in euros.",
        };
        db.ProjectMemories.Add(memory);
        await db.SaveChangesAsync();

        Task<List<ProjectMemory>> Search(string text) => MemorySearch.RankedAsync(
            db, db.ProjectMemories.AsNoTracking(), TestCurrentUser.TestOrgId, projectId,
            MemoryRules.BuildSearchQuery(text)!, 10, CancellationToken.None);

        (await Search("cafe")).Should().ContainSingle("diacritics are folded");
        (await Search("price")).Should().ContainSingle("porter stemming matches prices");

        memory.Content = "Prices are in dollars now.";
        await db.SaveChangesAsync();
        (await Search("euros")).Should().BeEmpty("the update trigger replaced the indexed text");
        (await Search("dollars")).Should().ContainSingle();

        // User text is never FTS5 syntax: operators, quotes and column filters are just words.
        var garbage = MemoryRules.BuildSearchQuery("\"dollars\" NEAR( title: -cafe* AND OR ^now");
        garbage.Should().NotBeNull();
        var act = () => MemorySearch.RankedAsync(db, db.ProjectMemories.AsNoTracking(),
            TestCurrentUser.TestOrgId, projectId, garbage!, 10, CancellationToken.None);
        (await act.Should().NotThrowAsync()).Which.Should().ContainSingle();

        db.ProjectMemories.Remove(memory);
        await db.SaveChangesAsync();
        (await Search("dollars")).Should().BeEmpty("the delete trigger removed it from the index");
    }

    // ── Org-wide permission uniqueness (NULL ProjectId) ──────────────────────────────

    [Fact]
    public async Task MemberPermission_DuplicateGrant_IsRejected_ForOrgWideAndProjectScopedAlike()
    {
        var memberId = Ulid.NewUlid();
        var project = Ulid.NewUlid();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrganizationsDbContext>();
        db.OrgMembers.Add(new OrgMember
        {
            Id = memberId, OrgId = TestCurrentUser.TestOrgId, UserId = "user_perm_" + memberId, Role = OrgRole.Member,
        });
        db.MemberPermissions.AddRange(
            Grant(memberId, null),
            Grant(memberId, project),
            Grant(memberId, Ulid.NewUlid()));
        await db.SaveChangesAsync();

        foreach (var duplicate in new[] { (Ulid?)null, project })
        {
            db.ChangeTracker.Clear();
            db.MemberPermissions.Add(Grant(memberId, duplicate));
            var save = () => db.SaveChangesAsync();
            await save.Should().ThrowAsync<DbUpdateException>(
                duplicate is null ? "SQLite treats NULLs as distinct, so the org-wide grant has its own index" : "same project");
        }

        static MemberPermission Grant(Ulid member, Ulid? projectId) => new()
        {
            Id = Ulid.NewUlid(), MemberId = member, OrgId = TestCurrentUser.TestOrgId,
            Permission = Orkka.SharedKernel.Security.Permission.TasksCreate, ProjectId = projectId,
        };
    }

    // ── Decimal sums ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DecimalSums_AreExact()
    {
        var model = "sum-test-" + Ulid.NewUlid();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
        foreach (var cost in new[] { 0.1m, 0.2m, 0.0001m, 1234.5678m })
        {
            db.UsageRecords.Add(new UsageRecord
            {
                Id = Ulid.NewUlid(), OrgId = TestCurrentUser.TestOrgId, ProjectId = Ulid.NewUlid(), Model = model,
                CostTotal = cost, VendorReportedCostUsd = cost * 2,
            });
        }
        await db.SaveChangesAsync();

        (await db.UsageRecords.Where(u => u.Model == model).SumAsync(u => u.CostTotal))
            .Should().Be(1234.8679m, "a double sum would read 1234.8679000000001");
        var grouped = await db.UsageRecords.Where(u => u.Model == model)
            .GroupBy(u => u.Model)
            .Select(g => new { Cost = g.Sum(u => u.CostTotal), Vendor = g.Sum(u => u.VendorReportedCostUsd ?? 0m) })
            .SingleAsync();
        grouped.Cost.Should().Be(1234.8679m);
        grouped.Vendor.Should().Be(2469.7358m);
        (await db.UsageRecords.Where(u => u.Model == model && u.CostTotal > 0.15m).CountAsync())
            .Should().Be(2, "decimal comparisons are numeric, not textual");
    }

    // ── Audit hash chain under concurrent writers ────────────────────────────────────

    [Fact]
    public async Task AuditChain_UnderConcurrentWriters_StaysOneLinearChain()
    {
        var orgId = Ulid.NewUlid();
        var audit = factory.Services.GetRequiredService<IAgentAuditLog>();

        await Task.WhenAll(Enumerable.Range(0, 30).Select(i => Task.Run(() => audit.RecordAsync(
            new AgentAuditRecord(orgId, null, AgentAuditEventType.RunStarted, $"event {i}")))));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
        var rows = await db.AgentAuditEvents.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.OrgId == orgId)
            .OrderBy(e => e.CreatedAt).ThenBy(e => e.Id)
            .Select(e => new { e.PreviousHash, e.EntryHash })
            .ToListAsync();

        rows.Should().HaveCount(30, "no write may be lost to a busy database");
        rows[0].PreviousHash.Should().BeNull();
        for (var i = 1; i < rows.Count; i++)
            rows[i].PreviousHash.Should().Be(rows[i - 1].EntryHash, $"row {i} links to the row before it");
    }
}
