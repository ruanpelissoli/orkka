namespace Orkka.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using Orkka.IntegrationTests.Fixtures;
using Orkka.IntegrationTests.Helpers;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Domain;
using Orkka.Modules.Agents.Features.Agents;
using Orkka.Modules.Projects.Features.Projects;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Every available agent is seeded with the project — there is no add/enable/toggle flow.
/// The team page only lists, configures, and chats with the seeded roster.
/// </summary>
[Collection("Integration")]
public sealed class AgentEndpointTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<Ulid> CreateProjectAsync()
    {
        var resp = await _client.PostAsJsonAsync("/api/projects",
            new CreateProjectRequest("Agent Roster Project", null));
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var project = await resp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);
        return project!.Id;
    }

    /// <summary>
    /// Seeding is an event-bus handler (AgentSeedOnProjectCreated) and the bus dispatches
    /// asynchronously — POST /api/projects returns before the roster is committed, so reading
    /// immediately is a race that a loaded CI runner loses. The handler adds the whole pod in a
    /// single SaveChanges, so any non-empty result is already the complete roster.
    /// Polls instead of sleeping, so an unloaded machine still returns in milliseconds.
    /// </summary>
    private async Task<List<AgentResponse>> ListAgentsAsync(Ulid projectId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);

        while (true)
        {
            var agents = (await _client.GetFromJsonAsync<List<AgentResponse>>(
                $"/api/projects/{projectId}/agents", TestJsonOptions.Default))!;

            if (agents.Count > 0 || DateTime.UtcNow >= deadline)
                return agents;

            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task ProjectCreation_SeedsTheFullRoster_IncludingSpecialists()
    {
        var projectId = await CreateProjectAsync();

        var agents = await ListAgentsAsync(projectId);

        // API-created projects are Unmanaged → full dev bench + all specialists.
        agents.Select(a => a.Role).Should().BeEquivalentTo(
        [
            "PM", "Designer", "FullStackDev", "SolutionArchitect",
            "FrontendDev", "BackendDev", "DevOps", "CodeReviewer", "QA",
            "GameDev", "GameDesigner", "DBA", "Legal", "MobileDev",
        ]);
        agents.Should().AllSatisfy(a => a.IsDefault.Should().BeTrue("seeded agents are permanent"));
    }

    [Fact]
    public async Task LegalAgent_IsSeededWithCuratedDefaults()
    {
        var projectId = await CreateProjectAsync();

        var lex = (await ListAgentsAsync(projectId)).Single(a => a.Role == "Legal");

        lex.Name.Should().Be("Lex");
        lex.Avatar.Should().Be("lex");
        lex.SystemPrompt.Should().Be(DefaultPrompts.ForRole(AgentRole.Legal));
    }

    [Fact]
    public async Task MobileDevAgent_IsSeededWithCuratedDefaults()
    {
        var projectId = await CreateProjectAsync();

        var nomad = (await ListAgentsAsync(projectId)).Single(a => a.Role == "MobileDev");

        nomad.Name.Should().Be("Nomad");
        nomad.Avatar.Should().Be("nomad");
        nomad.SystemPrompt.Should().Be(DefaultPrompts.ForRole(AgentRole.MobileDev));
    }

    [Fact]
    public async Task DeleteAgent_RejectsSeededAgents()
    {
        var projectId = await CreateProjectAsync();
        var agent = (await ListAgentsAsync(projectId)).First();

        var resp = await _client.DeleteAsync($"/api/projects/{projectId}/agents/{agent.Id}");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("cannot be deleted");
    }

    [Fact]
    public async Task RemovedAgentLifecycleEndpoints_NoLongerExist()
    {
        var projectId = await CreateProjectAsync();
        var agent = (await ListAgentsAsync(projectId)).First();

        // Create, toggle, and enable-optional were removed with the always-seeded roster.
        (await _client.PostAsJsonAsync($"/api/projects/{projectId}/agents", new { name = "X", role = "QA" }))
            .StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
        (await _client.PatchAsync($"/api/projects/{projectId}/agents/{agent.Id}/toggle", null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.PostAsync($"/api/projects/{projectId}/agents/optional/DBA/enable", null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The team page prints a token figure next to the agent's cost. It used to read the stored
    /// TotalTokensIn/Out counters, which have no cache buckets — so an agent whose spend was
    /// almost entirely cached prefix showed a token count near zero beside a real dollar amount.
    /// The response now carries the billing-derived split.
    /// </summary>
    [Fact]
    public async Task ListAgents_ReportsBilledTokensIncludingCache()
    {
        var projectId = await CreateProjectAsync();
        var agent = (await ListAgentsAsync(projectId)).First();

        using (var scope = factory.Services.CreateScope())
        {
            var usage = scope.ServiceProvider
                .GetRequiredService<Orkka.Modules.Usage.Contracts.IUsageRecorder>();
            await usage.RecordUsageAsync(new Orkka.Modules.Usage.Contracts.RecordUsageCommand(
                TestCurrentUser.TestOrgId, projectId, null, agent.Id, null,
                "claude-sonnet-5", InputTokens: 800, OutputTokens: 70_000,
                CacheReadTokens: 5_900_000, CacheWriteTokens: 100_000));
        }

        var refreshed = (await ListAgentsAsync(projectId)).Single(a => a.Id == agent.Id);

        refreshed.Billed.Should().NotBeNull();
        refreshed.Billed!.CacheReadTokens.Should().Be(5_900_000);
        refreshed.Billed.CacheWriteTokens.Should().Be(100_000);
        refreshed.Billed.TotalTokens.Should().Be(6_070_800);

        // The stored counters are untouched by usage recording, which is exactly why they
        // cannot be the number displayed.
        refreshed.Billed.TotalTokens.Should()
            .BeGreaterThan(refreshed.TotalTokensIn + refreshed.TotalTokensOut);
    }

    /// <summary>An agent that has never run has no usage rows, and must report null rather than
    /// a zeroed breakdown — the client falls back to the stored counters on null.</summary>
    [Fact]
    public async Task ListAgents_ReportsNullBilled_ForAnAgentThatNeverRan()
    {
        var projectId = await CreateProjectAsync();

        var agents = await ListAgentsAsync(projectId);

        agents.Should().NotBeEmpty();
        agents.Should().OnlyContain(a => a.Billed == null);
    }
}
