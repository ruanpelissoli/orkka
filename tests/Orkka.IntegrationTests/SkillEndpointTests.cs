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
using Orkka.Modules.Agents.Features.AgentChat;
using Orkka.Modules.Agents.Features.Agents;
using Orkka.Modules.Agents.Features.Skills;
using Orkka.Modules.Agents.Infrastructure;
using Orkka.Modules.Projects.Features.Projects;

/// <summary>
/// The skills library: CRUD + slug rules, agent assignment (both routes), built-in
/// permanence, and the two ways skills reach an agent — the prompt section for assigned
/// skills and the <c>/slug</c> invocation block on the agent's copy of the message.
/// </summary>
[Collection("Integration")]
public sealed class SkillEndpointTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private FakeChatAgentInvoker Fake =>
        (FakeChatAgentInvoker)factory.Services.GetRequiredService<IChatAgentInvoker>();

    private async Task<Ulid> CreateProjectAsync(string name)
    {
        var resp = await _client.PostAsJsonAsync("/api/projects", new CreateProjectRequest(name, "desc"));
        var project = await resp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);
        return project!.Id;
    }

    /// <summary>Waits for the ProjectCreated handler to seed the agents + starter skills.</summary>
    private async Task<List<AgentResponse>> WaitForAgentsAsync(Ulid projectId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var agents = await _client.GetFromJsonAsync<List<AgentResponse>>(
                $"/api/projects/{projectId}/agents", TestJsonOptions.Default);
            if (agents is { Count: > 0 }) return agents;
            await Task.Delay(100);
        }
        throw new TimeoutException("agents were not seeded");
    }

    private async Task<List<SkillResponse>> WaitForSkillsAsync(Ulid projectId, int atLeast = 1)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var skills = await _client.GetFromJsonAsync<List<SkillResponse>>(
                $"/api/projects/{projectId}/skills", TestJsonOptions.Default);
            if (skills is not null && skills.Count >= atLeast) return skills;
            await Task.Delay(100);
        }
        throw new TimeoutException("skills were not seeded");
    }

    [Fact]
    public async Task NewProject_GetsStarterSkills_AssignedToDefaultRoles()
    {
        var projectId = await CreateProjectAsync("Skills Seed");
        // Skills (and their role assignments) are seeded right after the agents by the same
        // handler — read the agents only once the skills exist.
        var skills = await WaitForSkillsAsync(projectId, atLeast: 4);
        var agents = await WaitForAgentsAsync(projectId);

        skills.Select(s => s.Slug).Should().Contain(["groom-backlog", "release-notes", "security-audit", "dependency-update"]);
        skills.Should().OnlyContain(s => s.IsBuiltIn && s.IsEnabled);

        var pm = agents.Single(a => a.Role == nameof(AgentRole.PM));
        var groom = skills.Single(s => s.Slug == "groom-backlog");
        groom.AssignedAgentIds.Should().Contain(pm.Id);
        pm.SkillIds.Should().Contain(groom.Id);
    }

    [Fact]
    public async Task Create_DerivesSlug_RejectsDuplicates_AndAssignsOnCreate()
    {
        var projectId = await CreateProjectAsync("Skills Create");
        var agents = await WaitForAgentsAsync(projectId);
        var reviewer = agents.Single(a => a.Role == nameof(AgentRole.CodeReviewer));

        var create = await _client.PostAsJsonAsync($"/api/projects/{projectId}/skills",
            new CreateSkillRequest("Write ADRs!", "Draft an architecture decision record", "# ADR\n\nSteps…",
                AgentIds: [reviewer.Id.ToString()]));
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var skill = await create.Content.ReadFromJsonAsync<SkillResponse>(TestJsonOptions.Default);

        skill!.Slug.Should().Be("write-adrs");
        skill.IsBuiltIn.Should().BeFalse();
        skill.AssignedAgentIds.Should().Equal(reviewer.Id);

        var dup = await _client.PostAsJsonAsync($"/api/projects/{projectId}/skills",
            new CreateSkillRequest("Other", "d", "c", Slug: "write-adrs"));
        dup.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var badSlug = await _client.PostAsJsonAsync($"/api/projects/{projectId}/skills",
            new CreateSkillRequest("Bad", "d", "c", Slug: "Not Valid!"));
        badSlug.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task BuiltIn_CanBeDisabledAndEdited_ButNotDeleted()
    {
        var projectId = await CreateProjectAsync("Skills BuiltIn");
        await WaitForAgentsAsync(projectId);
        var skills = await WaitForSkillsAsync(projectId, atLeast: 4);
        var groom = skills.Single(s => s.Slug == "groom-backlog");

        var update = await _client.PutAsJsonAsync($"/api/projects/{projectId}/skills/{groom.Id}",
            new UpdateSkillRequest(Description: "custom description", IsEnabled: false));
        update.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await update.Content.ReadFromJsonAsync<SkillResponse>(TestJsonOptions.Default);
        updated!.IsEnabled.Should().BeFalse();
        updated.Description.Should().Be("custom description");

        var delete = await _client.DeleteAsync($"/api/projects/{projectId}/skills/{groom.Id}");
        delete.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task DeleteCustom_ScrubsAssignmentsFromAgents()
    {
        var projectId = await CreateProjectAsync("Skills Delete");
        var agents = await WaitForAgentsAsync(projectId);
        var pm = agents.Single(a => a.Role == nameof(AgentRole.PM));

        var create = await _client.PostAsJsonAsync($"/api/projects/{projectId}/skills",
            new CreateSkillRequest("Temp", "d", "c", AgentIds: [pm.Id.ToString()]));
        var skill = await create.Content.ReadFromJsonAsync<SkillResponse>(TestJsonOptions.Default);

        var before = await _client.GetFromJsonAsync<AgentResponse>(
            $"/api/projects/{projectId}/agents/{pm.Id}", TestJsonOptions.Default);
        before!.SkillIds.Should().Contain(skill!.Id);

        var delete = await _client.DeleteAsync($"/api/projects/{projectId}/skills/{skill.Id}");
        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var after = await _client.GetFromJsonAsync<AgentResponse>(
            $"/api/projects/{projectId}/agents/{pm.Id}", TestJsonOptions.Default);
        after!.SkillIds.Should().NotContain(skill.Id);
    }

    [Fact]
    public async Task UpdateAgent_SkillIds_ReplacesTheSet_AndRejectsForeignSkills()
    {
        var projectId = await CreateProjectAsync("Skills Assign");
        var otherProject = await CreateProjectAsync("Skills Assign Other");
        var agents = await WaitForAgentsAsync(projectId);
        await WaitForAgentsAsync(otherProject);
        var skills = await WaitForSkillsAsync(projectId, atLeast: 4);
        var otherSkills = await WaitForSkillsAsync(otherProject, atLeast: 4);
        var qa = agents.Single(a => a.Role == nameof(AgentRole.QA));
        var release = skills.Single(s => s.Slug == "release-notes");

        var ok = await _client.PutAsJsonAsync($"/api/projects/{projectId}/agents/{qa.Id}",
            new UpdateAgentRequest(null, null, null, null, SkillIds: [release.Id.ToString()]));
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        var saved = await ok.Content.ReadFromJsonAsync<AgentResponse>(TestJsonOptions.Default);
        saved!.SkillIds.Should().Equal(release.Id);

        var foreign = await _client.PutAsJsonAsync($"/api/projects/{projectId}/agents/{qa.Id}",
            new UpdateAgentRequest(null, null, null, null, SkillIds: [otherSkills[0].Id.ToString()]));
        foreign.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // The dedicated route clears with [].
        var clear = await _client.PutAsJsonAsync($"/api/projects/{projectId}/agents/{qa.Id}/skills",
            new SetAgentSkillsRequest([]));
        clear.StatusCode.Should().Be(HttpStatusCode.OK);
        var cleared = await _client.GetFromJsonAsync<AgentResponse>(
            $"/api/projects/{projectId}/agents/{qa.Id}", TestJsonOptions.Default);
        cleared!.SkillIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Chat_AssignedSkillsAppearInPrompt_AndSlashInvokesTheBody()
    {
        var projectId = await CreateProjectAsync("Skills Chat");
        var agents = await WaitForAgentsAsync(projectId);
        var skills = await WaitForSkillsAsync(projectId, atLeast: 4);
        var pm = agents.Single(a => a.Role == nameof(AgentRole.PM));
        var groom = skills.Single(s => s.Slug == "groom-backlog");
        var audit = skills.Single(s => s.Slug == "security-audit"); // NOT assigned to the PM

        Ulid conversationId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
            var conversation = new AgentConversation
            {
                Id = Ulid.NewUlid(),
                OrgId = TestCurrentUser.TestOrgId,
                ProjectId = projectId,
                AgentId = pm.Id,
            };
            db.AgentConversations.Add(conversation);
            await db.SaveChangesAsync();
            conversationId = conversation.Id;
        }

        var url = $"/api/projects/{projectId}/agents/{pm.Id}/conversations/{conversationId}/messages";
        var invocationsBefore = Fake.Invocations.Count;

        // Plain message: assigned skills are in the system prompt; nothing is "invoked".
        var plain = await _client.PostAsJsonAsync(url, new SendMessageRequest("hello there"));
        plain.StatusCode.Should().Be(HttpStatusCode.OK);
        var first = await WaitForInvocationAsync(invocationsBefore + 1);
        first.SystemPrompt.Should().Contain("## Skills");
        first.SystemPrompt.Should().Contain("/groom-backlog");
        first.SystemPrompt.Should().NotContain("/security-audit", "only assigned skills are listed");
        first.UserMessage.Should().NotContain("<skill name=");

        await WaitForIdleAsync(conversationId);

        // Explicit invocation of an unassigned-but-enabled project skill.
        var invoked = await _client.PostAsJsonAsync(url, new SendMessageRequest("/security-audit focus on auth"));
        invoked.StatusCode.Should().Be(HttpStatusCode.OK);
        var second = await WaitForInvocationAsync(invocationsBefore + 2);
        second.UserMessage.Should().Contain("<skill name=\"security-audit\">");
        second.UserMessage.Should().Contain(audit.Content.Trim()[..40]);
        second.UserMessage.Should().Contain("focus on auth");
        // The invocation block precedes the user's own message.
        second.UserMessage.IndexOf("<skill name=", StringComparison.Ordinal)
            .Should().BeLessThan(second.UserMessage.IndexOf("focus on auth", StringComparison.Ordinal));

        // The durable record keeps the raw command.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
            var stored = await db.AgentMessages.IgnoreQueryFilters()
                .Where(m => m.ConversationId == conversationId && m.Role == "user")
                .OrderByDescending(m => m.CreatedAt)
                .Select(m => m.Content)
                .FirstAsync();
            stored.Should().Be("/security-audit focus on auth");
        }
    }

    private async Task<FakeChatAgentInvoker.Invocation> WaitForInvocationAsync(int count)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (Fake.Invocations.Count >= count)
                return Fake.Invocations.ElementAt(count - 1);
            await Task.Delay(100);
        }
        throw new TimeoutException("agent was not invoked");
    }

    private async Task WaitForIdleAsync(Ulid conversationId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
            var active = await db.AgentConversations.IgnoreQueryFilters()
                .Where(c => c.Id == conversationId)
                .Select(c => c.ActiveRunStartedAt)
                .FirstAsync();
            if (active is null) return;
            await Task.Delay(100);
        }
        throw new TimeoutException("conversation did not go idle");
    }
}
