namespace Orkka.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orkka.IntegrationTests.Fixtures;
using Orkka.IntegrationTests.Helpers;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Features.Agents;
using Orkka.Modules.Agents.Features.Workspace;
using Orkka.Modules.Agents.Infrastructure;
using Orkka.Modules.Organizations.Domain;
using Orkka.Modules.Organizations.Infrastructure;
using Orkka.Modules.Projects.Features.Projects;
using Orkka.Modules.Runners.Infrastructure.Services;
using Orkka.SharedKernel.Security;

/// <summary>
/// Settings → Workspace → AI provider: "my company pays for Copilot". Every case runs in its own
/// throwaway workspace (the suite shares the test org, whose new projects must stay on Claude).
/// </summary>
[Collection("Integration")]
public sealed class WorkspaceAiProviderEndpointTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task InWorkspace(Func<Ulid, Task> body)
    {
        var orgId = Ulid.NewUlid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrganizationsDbContext>();
            db.Organizations.Add(new Organization { Id = orgId, Name = "Acme", Slug = $"acme-{orgId}".ToLowerInvariant() });
            await db.SaveChangesAsync();
        }

        TestCurrentUser.CurrentOrgId = orgId;
        try
        {
            await body(orgId);
        }
        finally
        {
            TestCurrentUser.Reset();
        }
    }

    /// <summary>What the developer's runner says it has signed in, as its Hello would.</summary>
    private async Task RunnerReportsAsync(Ulid orgId, params string[] ready)
    {
        using var scope = factory.Services.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<RunnerPresenceWriter>();
        var providers = new[] { RunnerProviders.Claude, RunnerProviders.Codex, RunnerProviders.Copilot }
            .Select(p => new RunnerProviderStatus(p, CliFound: true, Authenticated: ready.Contains(p),
                Detail: ready.Contains(p) ? null : $"{p} is not logged in"))
            .ToArray();
        await writer.UpsertAsync(orgId, TestCurrentUser.TestUserId, Ulid.NewUlid(), "orkka-worker-0", "conn-ws",
            new RunnerHello(5, "ruan-laptop", true, ready.Contains(RunnerProviders.Claude), [], providers));
    }

    private async Task<Ulid> CreateProjectAsync()
    {
        var resp = await _client.PostAsJsonAsync("/api/projects", new CreateProjectRequest("AI provider project", null));
        resp.StatusCode.Should().Be(HttpStatusCode.Created, await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default))!.Id;
    }

    /// <summary>Seeding is asynchronous (event bus); poll until the pod is there.</summary>
    private async Task<List<AgentResponse>> AgentsAsync(Ulid projectId)
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

    private async Task<UpdateWorkspaceAiProviderResponse> SetAsync(string provider, bool apply = true)
    {
        var resp = await _client.PutAsJsonAsync("/api/org/ai-provider",
            new UpdateWorkspaceAiProviderRequest(provider, apply));
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<UpdateWorkspaceAiProviderResponse>(TestJsonOptions.Default))!;
    }

    private async Task<WorkspaceAiProviderResponse> GetAsync()
        => (await _client.GetFromJsonAsync<WorkspaceAiProviderResponse>("/api/org/ai-provider", TestJsonOptions.Default))!;

    [Fact]
    public async Task ANewWorkspace_IsOnAutomatic() => await InWorkspace(async _ =>
    {
        var setting = await GetAsync();
        setting.Provider.Should().Be("automatic");
        setting.Choices.Should().Equal("automatic", "claude", "codex", "copilot", "gemini", "grok");
        setting.EffectiveProvider.Should().Be("claude");
        setting.Blocker.Should().BeNull();
    });

    [Fact]
    public async Task ChoosingCopilot_SeedsNewProjectsOnCopilotAuto_ExceptQa() => await InWorkspace(async orgId =>
    {
        await RunnerReportsAsync(orgId, RunnerProviders.Claude, RunnerProviders.Copilot);
        var set = await SetAsync("copilot", apply: false);
        set.Provider.Should().Be("copilot");
        set.EffectiveProvider.Should().Be("copilot");
        set.Applied.Should().BeFalse();

        var agents = await AgentsAsync(await CreateProjectAsync());

        agents.Where(a => a.Role != "QA").Should().AllSatisfy(a => a.Model.Should().Be("copilot/auto"));
        agents.Single(a => a.Role == "QA").Model.Should().Be(ProviderModelDefaults.QaModel,
            "browser QA needs Claude, and Claude can run here");
    });

    [Fact]
    public async Task CopilotChosenButSignedOut_SeedsClaude_AndTheSettingSaysWhy() => await InWorkspace(async orgId =>
    {
        await RunnerReportsAsync(orgId, RunnerProviders.Claude);
        await SetAsync("copilot", apply: false);

        var setting = await GetAsync();
        setting.Provider.Should().Be("copilot");
        setting.EffectiveProvider.Should().Be("claude");
        setting.Blocker.Should().Be("copilot is not logged in");

        var agents = await AgentsAsync(await CreateProjectAsync());
        agents.Where(a => a.Role != "PM").Should().AllSatisfy(a => a.Model.Should().StartWith("claude-"));
    });

    [Fact]
    public async Task ChangingIt_SwitchesEveryExistingProject_AndReportsCounts() => await InWorkspace(async orgId =>
    {
        await RunnerReportsAsync(orgId, RunnerProviders.Claude, RunnerProviders.Copilot);
        var first = await CreateProjectAsync();
        var second = await CreateProjectAsync();
        var pod = (await AgentsAsync(first)).Count;
        (await AgentsAsync(second)).Should().HaveCount(pod);

        var result = await SetAsync("copilot");

        result.Applied.Should().BeTrue();
        result.ProjectCount.Should().Be(2);
        result.SkippedQaCount.Should().Be(2, "one QA agent per project stays on Claude");
        result.UpdatedCount.Should().Be(2 * (pod - 1));
        foreach (var project in new[] { first, second })
        {
            var agents = await AgentsAsync(project);
            agents.Where(a => a.Role != "QA").Should().AllSatisfy(a => a.Model.Should().Be("copilot/auto"));
            agents.Single(a => a.Role == "QA").Model.Should().Be(ProviderModelDefaults.QaModel);
        }

        // Each changed agent is audited, and so is the setting itself.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
        var events = await db.AgentAuditEvents.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.OrgId == orgId).ToListAsync();
        events.Count(e => e.EventType == AgentAuditEventType.AgentConfigChanged).Should().Be(2 * (pod - 1));
        events.Should().ContainSingle(e => e.EventType == AgentAuditEventType.WorkspaceSettingsChanged)
            .Which.MetadataJson.Should().Contain("copilot");

        // Back to Claude: the tier models again, QA included.
        var back = await SetAsync("claude");
        back.SkippedQaCount.Should().Be(0);
        (await AgentsAsync(first)).Single(a => a.Role == "PM").Model
            .Should().Be(ProviderModelDefaults.FastModel(AiProvider.Anthropic));
    });

    [Fact]
    public async Task Switching_NeverTouchesAnotherWorkspace() => await InWorkspace(async orgId =>
    {
        // A project in the suite's shared workspace...
        TestCurrentUser.Reset();
        var elsewhere = await CreateProjectAsync();
        var before = (await AgentsAsync(elsewhere)).Select(a => (a.Id, a.Model)).ToList();

        // ...is left alone when this workspace moves to Copilot.
        TestCurrentUser.CurrentOrgId = orgId;
        await CreateProjectAsync();
        var result = await SetAsync("copilot");
        result.ProjectCount.Should().Be(1);

        TestCurrentUser.Reset();
        (await AgentsAsync(elsewhere)).Select(a => (a.Id, a.Model)).Should().BeEquivalentTo(before);
        (await GetAsync()).Provider.Should().Be("automatic", "the setting is per workspace");
        TestCurrentUser.CurrentOrgId = orgId;
    });

    [Theory]
    [InlineData("ollama")]
    [InlineData("nope")]
    public async Task AnUnknownChoice_IsRejected(string provider) => await InWorkspace(async _ =>
    {
        var resp = await _client.PutAsJsonAsync("/api/org/ai-provider", new UpdateWorkspaceAiProviderRequest(provider));
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await GetAsync()).Provider.Should().Be("automatic");
    });

    [Fact]
    public async Task OnlyTheOwner_ChangesIt() => await InWorkspace(async _ =>
    {
        TestCurrentUser.CurrentRole = OrgRole.Member;
        TestCurrentUser.CurrentPermissions = new HashSet<Permission> { Permission.AgentsConfigure };
        var resp = await _client.PutAsJsonAsync("/api/org/ai-provider", new UpdateWorkspaceAiProviderRequest("copilot"));
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    });
}
