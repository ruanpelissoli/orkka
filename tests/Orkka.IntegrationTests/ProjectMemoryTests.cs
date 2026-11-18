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
using Orkka.Modules.Agents.Features.AgentChat;
using Orkka.Modules.Agents.Features.Memory;
using Orkka.Modules.Agents.Infrastructure;
using Orkka.Modules.Agents.Infrastructure.Services;
using Orkka.Modules.Projects.Features.Projects;
using Orkka.SharedKernel.Security;

/// <summary>
/// Shared project memory: relevance retrieval tiers (pinned/rules → full-text → recency,
/// team + agent scope, archived hidden), the Memory endpoints (list/search/create/update/
/// delete/preview + permission), the chat remember/recall tools, and the outcome memory dedupe.
/// </summary>
[Collection("Integration")]
public sealed class ProjectMemoryTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private FakeChatAgentInvoker Fake => (FakeChatAgentInvoker)factory.Services.GetRequiredService<IChatAgentInvoker>();

    private async Task<Ulid> CreateProjectAsync(string name)
    {
        var resp = await _client.PostAsJsonAsync("/api/projects", new CreateProjectRequest(name, "desc"));
        return (await resp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default))!.Id;
    }

    private async Task<(Ulid AgentId, Ulid ConversationId)> SeedAgentAsync(Ulid projectId, string name = "Memo")
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
        var agent = new ProjectAgent { Id = Ulid.NewUlid(), OrgId = TestCurrentUser.TestOrgId, ProjectId = projectId, Name = name, Role = AgentRole.SolutionArchitect, Model = "claude-sonnet-4-6" };
        var conversation = new AgentConversation { Id = Ulid.NewUlid(), OrgId = TestCurrentUser.TestOrgId, ProjectId = projectId, AgentId = agent.Id };
        db.ProjectAgents.Add(agent);
        db.AgentConversations.Add(conversation);
        await db.SaveChangesAsync();
        return (agent.Id, conversation.Id);
    }

    private async Task<Ulid> AddAsync(Ulid projectId, MemoryType type, string title, string content, Ulid? agentId = null, bool pinned = false, string? dedupe = null)
    {
        using var scope = factory.Services.CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<IAgentsApi>();
        return await api.AddMemoryAsync(new NewMemory(TestCurrentUser.TestOrgId, projectId, agentId, type, title, content, MemorySource.System, IsPinned: pinned, DedupeKey: dedupe));
    }

    private async Task<IReadOnlyList<MemoryItem>> RetrieveAsync(Ulid projectId, Ulid? agentId, string? query, int take = 20)
    {
        using var scope = factory.Services.CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<IAgentsApi>();
        return await api.RetrieveMemoriesAsync(new MemoryQuery(TestCurrentUser.TestOrgId, projectId, agentId, query, take));
    }

    [Fact]
    public async Task Retrieval_RulesFirst_ThenRelevance_ThenRecency_RespectsScopeAndArchive()
    {
        var projectId = await CreateProjectAsync("Memory Retrieval");
        var (agentA, _) = await SeedAgentAsync(projectId, "A");
        var (agentB, _) = await SeedAgentAsync(projectId, "B");

        var convention = await AddAsync(projectId, MemoryType.Convention, "Use pnpm", "The repo uses pnpm workspaces; never run npm install.");
        var decision = await AddAsync(projectId, MemoryType.Decision, "Postgres over Mongo", "We chose Postgres for relational integrity.");
        var pinnedLearning = await AddAsync(projectId, MemoryType.Learning, "Flaky e2e", "The checkout e2e test is flaky on CI; retry once.", pinned: true);
        var oauth = await AddAsync(projectId, MemoryType.Learning, "OAuth callback gotcha", "The OAuth login callback needs the exact redirect URI registered in the provider console.");
        var caching = await AddAsync(projectId, MemoryType.Learning, "Redis cache TTL", "Cache TTL is 5 minutes for product listings.");
        var privateA = await AddAsync(projectId, MemoryType.Mistake, "Forgot migrations", "I once shipped a model change without an EF migration.", agentId: agentA);
        var privateB = await AddAsync(projectId, MemoryType.Learning, "B's own note", "Only B should ever see this.", agentId: agentB);
        var archived = await AddAsync(projectId, MemoryType.Learning, "Old OAuth note", "OAuth login was once done with implicit flow.");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
            await db.ProjectMemories.Where(m => m.Id == archived).ExecuteUpdateAsync(s => s.SetProperty(m => m.IsArchived, true));
        }

        var items = await RetrieveAsync(projectId, agentA, "Fix the OAuth login callback redirect", take: 6);
        var ids = items.Select(i => i.Id).ToList();

        // Rules tier leads (order among rules: pinned first).
        ids.Take(3).Should().BeEquivalentTo([pinnedLearning, convention, decision]);
        ids[0].Should().Be(pinnedLearning, "pinned rows sort before the rest of the rules tier");
        // Relevance: the OAuth learning outranks the unrelated cache note.
        ids.Should().Contain(oauth);
        ids.IndexOf(oauth).Should().BeLessThan(ids.Contains(caching) ? ids.IndexOf(caching) : int.MaxValue);
        // Scope: A sees its own private note, never B's; archived rows never surface.
        ids.Should().NotContain(privateB);
        ids.Should().NotContain(archived);
        items.Should().HaveCount(6, "take is honoured");

        // With no agent, only team rows are eligible.
        var teamOnly = await RetrieveAsync(projectId, null, null, take: 50);
        teamOnly.Select(i => i.Id).Should().NotContain(privateA).And.NotContain(privateB);
        teamOnly.Should().OnlyContain(i => i.AgentId == null);

        // Use counters were bumped for retrieved rows.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
            (await db.ProjectMemories.AsNoTracking().SingleAsync(m => m.Id == oauth)).UseCount.Should().BeGreaterThanOrEqualTo(1);
        }

        // Rendered section: rules under the "follow these" heading, learnings under the other, budget respected.
        var text = MemoryPromptSection.Render(items, 4_000);
        text.Should().Contain(MemoryPromptSection.Heading)
            .And.Contain("Decisions, conventions & preferences (follow these)")
            .And.Contain("[Convention] Use pnpm:")
            .And.Contain("Relevant learnings & past mistakes")
            .And.Contain("OAuth callback gotcha");
        MemoryPromptSection.Render(items, 120).Length.Should().BeLessThan(400, "a tiny budget cuts the list");
    }

    [Fact]
    public async Task AddMemory_WithDedupeKey_ReplacesInsteadOfStacking()
    {
        var projectId = await CreateProjectAsync("Memory Dedupe");
        var (agentId, _) = await SeedAgentAsync(projectId);
        var key = $"outcome:{Ulid.NewUlid()}:Developing:{agentId}";
        var first = await AddAsync(projectId, MemoryType.Mistake, "Developing failed: X", "boom", agentId, dedupe: key);
        var second = await AddAsync(projectId, MemoryType.Learning, "Developing done: X", "shipped", agentId, dedupe: key);
        second.Should().Be(first, "same key → same row");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
        var rows = await db.ProjectMemories.AsNoTracking().Where(m => m.ProjectId == projectId).ToListAsync();
        rows.Should().ContainSingle().Which.Type.Should().Be(MemoryType.Learning);
    }

    [Fact]
    public async Task Endpoints_CrudSearchPreview_AndPermission()
    {
        var projectId = await CreateProjectAsync("Memory Endpoints");
        var (agentId, _) = await SeedAgentAsync(projectId, "Luna");

        var create = await _client.PostAsJsonAsync($"/api/projects/{projectId}/memories",
            new UpsertMemoryRequest("Tailwind only", "Styling is Tailwind utility classes; no CSS modules.", "Convention", null, IsPinned: true));
        create.StatusCode.Should().Be(HttpStatusCode.Created, await create.Content.ReadAsStringAsync());
        var created = await create.Content.ReadFromJsonAsync<MemoryResponse>(TestJsonOptions.Default);
        created!.Source.Should().Be("User");
        created.IsPinned.Should().BeTrue();
        created.CreatedByUserId.Should().Be(TestCurrentUser.TestUserId);

        var priv = await _client.PostAsJsonAsync($"/api/projects/{projectId}/memories",
            new UpsertMemoryRequest("Luna's note", "Ask about acceptance criteria early.", "Preference", agentId.ToString()));
        priv.StatusCode.Should().Be(HttpStatusCode.Created);
        (await priv.Content.ReadFromJsonAsync<MemoryResponse>(TestJsonOptions.Default))!.AgentName.Should().Be("Luna");

        (await _client.PostAsJsonAsync($"/api/projects/{projectId}/memories", new UpsertMemoryRequest("", "x"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _client.PostAsJsonAsync($"/api/projects/{projectId}/memories", new UpsertMemoryRequest("t", "x", "NotAType"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _client.PostAsJsonAsync($"/api/projects/{projectId}/memories", new UpsertMemoryRequest("t", "x", "Learning", Ulid.NewUlid().ToString()))).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var page = await _client.GetFromJsonAsync<MemoryPage>($"/api/projects/{projectId}/memories", TestJsonOptions.Default);
        page!.Total.Should().Be(2);
        page.Items[0].Id.Should().Be(created.Id, "pinned first");
        (await _client.GetFromJsonAsync<MemoryPage>($"/api/projects/{projectId}/memories?scope=team", TestJsonOptions.Default))!.Items.Should().ContainSingle();
        (await _client.GetFromJsonAsync<MemoryPage>($"/api/projects/{projectId}/memories?scope={agentId}", TestJsonOptions.Default))!.Items.Should().ContainSingle().Which.Title.Should().Be("Luna's note");
        (await _client.GetFromJsonAsync<MemoryPage>($"/api/projects/{projectId}/memories?type=Convention", TestJsonOptions.Default))!.Items.Should().ContainSingle();
        var searched = await _client.GetFromJsonAsync<MemoryPage>($"/api/projects/{projectId}/memories?q=tailwind%20styling", TestJsonOptions.Default);
        searched!.Items.Should().ContainSingle().Which.Id.Should().Be(created.Id);

        // Preview shows what an agent would get and does not inflate use counts.
        var preview = await _client.GetFromJsonAsync<List<MemoryResponse>>($"/api/projects/{projectId}/memories/retrieve?q=css&agentId={agentId}", TestJsonOptions.Default);
        preview!.Select(p => p.Id).Should().Contain(created.Id);
        (await _client.GetFromJsonAsync<MemoryPage>($"/api/projects/{projectId}/memories?type=Convention", TestJsonOptions.Default))!.Items[0].UseCount.Should().Be(0);

        var update = await _client.PutAsJsonAsync($"/api/projects/{projectId}/memories/{created.Id}",
            new UpsertMemoryRequest("Tailwind only (v2)", "Tailwind utilities; shared tokens live in packages/ui.", "Convention", null, IsPinned: false, IsArchived: true));
        update.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await update.Content.ReadFromJsonAsync<MemoryResponse>(TestJsonOptions.Default);
        updated!.IsArchived.Should().BeTrue();
        (await _client.GetFromJsonAsync<MemoryPage>($"/api/projects/{projectId}/memories", TestJsonOptions.Default))!.Total.Should().Be(1, "archived rows are hidden by default");
        (await _client.GetFromJsonAsync<MemoryPage>($"/api/projects/{projectId}/memories?archived=true", TestJsonOptions.Default))!.Items.Should().ContainSingle();
        (await RetrieveAsync(projectId, agentId, "tailwind")).Select(i => i.Id).Should().NotContain(created.Id, "archived rows are never retrieved");

        (await _client.DeleteAsync($"/api/projects/{projectId}/memories/{created.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _client.DeleteAsync($"/api/projects/{projectId}/memories/{created.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var audit = await _client.GetFromJsonAsync<Orkka.Modules.Agents.Features.Audit.AuditPage>($"/api/audit?eventType=MemoryChanged&projectId={projectId}", TestJsonOptions.Default);
        audit!.Items.Should().HaveCountGreaterThanOrEqualTo(4);

        try
        {
            TestCurrentUser.CurrentRole = OrgRole.Member;
            TestCurrentUser.CurrentPermissions = new HashSet<Permission> { Permission.TasksCreate };
            (await _client.GetAsync($"/api/projects/{projectId}/memories")).StatusCode.Should().Be(HttpStatusCode.OK, "reads are open");
            (await _client.PostAsJsonAsync($"/api/projects/{projectId}/memories", new UpsertMemoryRequest("t", "c"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally { TestCurrentUser.Reset(); }
    }

    [Fact]
    public async Task ChatTools_RememberThenRecall_AndPromptCarriesMemory()
    {
        var projectId = await CreateProjectAsync("Memory Chat");
        var (agentId, conversationId) = await SeedAgentAsync(projectId, "Archie");
        await AddAsync(projectId, MemoryType.Decision, "Monorepo", "One repo for web + api; shared types in packages/core.");

        ChatToolResult? rememberResult = null;
        ChatToolResult? recallResult = null;
        async IAsyncEnumerable<ChatChunk> Script(IChatToolServer? tools)
        {
            tools.Should().NotBeNull();
            rememberResult = await tools!.CallToolAsync("remember", JsonSerializer.SerializeToElement(new
            {
                title = "Event bus is in-process only",
                content = "Cross-process work goes through DB rows + sweeps, never the event bus.",
                type = "Convention",
            }));
            recallResult = await tools.CallToolAsync("recall", JsonSerializer.SerializeToElement(new { query = "event bus cross process" }));
            yield return new ChatChunk(ChatChunkType.Text, "Noted.");
            yield return new ChatChunk(ChatChunkType.Done, "", PromptTokens: 5, CompletionTokens: 2);
        }
        Fake.EnqueueToolScript((tools, _) => Script(tools));

        var url = $"/api/projects/{projectId}/agents/{agentId}/conversations/{conversationId}/messages";
        var resp = await _client.PostAsJsonAsync(url, new SendMessageRequest("How do we do cross-process work in the monorepo?"));
        resp.EnsureSuccessStatusCode();

        rememberResult.Should().NotBeNull();
        rememberResult!.IsError.Should().BeFalse(rememberResult.Text);
        recallResult!.IsError.Should().BeFalse();
        recallResult.Text.Should().Contain("Event bus is in-process only");

        // The turn's system prompt carried the project memory section with the standing decision.
        Fake.Invocations.Should().NotBeEmpty();
        var invocation = Fake.Invocations.Last();
        invocation.SystemPrompt.Should().Contain(MemoryPromptSection.Heading).And.Contain("[Decision] Monorepo:");
        invocation.SystemPrompt.Should().Contain("remember", "tools on → the block explains how to add to memory");

        // The remembered row is team-wide, agent-sourced, and visible on the page.
        var page = await _client.GetFromJsonAsync<MemoryPage>($"/api/projects/{projectId}/memories?type=Convention", TestJsonOptions.Default);
        var row = page!.Items.Should().ContainSingle().Subject;
        row.Source.Should().Be("Agent");
        row.AgentId.Should().BeNull();
    }

    /// <summary>
    /// The per-project ceiling has to hold on the shared write path, not just in the callers
    /// that happened to check: the pipeline `remember` tool and the post-run outcome extractor
    /// both go straight through AddMemoryAsync, so a cap enforced only in the chat tool and the
    /// HTTP endpoint left the agent-side writers unbounded.
    /// </summary>
    [Fact]
    public async Task AddMemory_AtProjectCap_RefusesNewRowsButStillReplacesAndReopens()
    {
        var projectId = await CreateProjectAsync("Memory Cap");
        await FillToCapAsync(projectId);

        // A brand-new memory is refused, whatever writes it — no exception, an empty id.
        (await AddAsync(projectId, MemoryType.Learning, "One too many", "Should not land."))
            .Should().Be(Ulid.Empty);

        // The HTTP endpoint turns that refusal into a 409 rather than a 500 on the read-back.
        var resp = await _client.PostAsJsonAsync($"/api/projects/{projectId}/memories", new UpsertMemoryRequest("Nope", "Full."));
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);

        // Replacing an existing row through its dedupe key is not growth, so it still works —
        // otherwise a full project would stop recording the outcome of every retried task.
        var replaced = await AddAsync(projectId, MemoryType.Mistake, "Outcome", "Second attempt.", dedupe: "outcome:0");
        replaced.Should().NotBe(Ulid.Empty);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
            var stored = await db.ProjectMemories.AsNoTracking().FirstAsync(m => m.Id == replaced);
            stored.Content.Should().Be("Second attempt.");
            (await db.ProjectMemories.CountAsync(m => m.ProjectId == projectId && !m.IsArchived))
                .Should().Be(MemoryRules.MaxPerProject, "a replace must not add a row");
        }

        // Archiving is the way back under the ceiling — that is what the message tells humans.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
            var victim = await db.ProjectMemories.FirstAsync(m => m.ProjectId == projectId && !m.IsArchived);
            victim.IsArchived = true;
            await db.SaveChangesAsync();
        }
        (await AddAsync(projectId, MemoryType.Learning, "Room again", "Lands now."))
            .Should().NotBe(Ulid.Empty);
    }

    /// <summary>Bulk-inserts the ceiling in one round trip — going through AddMemoryAsync
    /// would mean 2,000 count queries to set up a test about the 2,001st.</summary>
    private async Task FillToCapAsync(Ulid projectId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
        db.ProjectMemories.AddRange(Enumerable.Range(0, MemoryRules.MaxPerProject).Select(i => new ProjectMemory
        {
            Id = Ulid.NewUlid(),
            OrgId = TestCurrentUser.TestOrgId,
            ProjectId = projectId,
            Type = MemoryType.Learning,
            Title = $"Filler {i}",
            Content = $"Filler content {i}.",
            Source = MemorySource.System,
            // One row carries a dedupe key so the replace path has something to hit.
            DedupeKey = i == 0 ? "outcome:0" : null,
        }));
        await db.SaveChangesAsync();
    }
}
