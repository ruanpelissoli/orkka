namespace Orkka.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using Orkka.IntegrationTests.Fixtures;
using Orkka.IntegrationTests.Helpers;
using Orkka.Modules.Projects.Features.ProjectRepos;
using Orkka.Modules.Projects.Features.Projects;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Features.Autopilot;
using FluentAssertions;

/// <summary>
/// The toggle. Its job is to make "autopilot is on" a single fact, and to make running without
/// a spend ceiling something a user has to mean.
/// </summary>
[Collection("Integration")]
public sealed class AutopilotEndpointTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<Ulid> SeedProjectAsync(string name)
    {
        var projectResp = await _client.PostAsJsonAsync("/api/projects",
            new CreateProjectRequest(name, "desc"));
        var project = await projectResp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);

        await _client.PostAsJsonAsync($"/api/projects/{project!.Id}/repos",
            new AddProjectRepoRequest("ap-repo", "https://github.com/test/ap.git", "main", null, null));

        return project.Id;
    }

    [Fact]
    public async Task Start_WithACap_OpensASession()
    {
        var projectId = await SeedProjectAsync("Autopilot Start");

        var resp = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/autopilot", new StartAutopilotRequest(CapUsd: 20m));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var status = await resp.Content.ReadFromJsonAsync<AutopilotStatus>(TestJsonOptions.Default);

        status!.Running.Should().BeTrue();
        status.CapUsd.Should().Be(20m);
        status.SessionId.Should().NotBeNull();
        status.StopReason.Should().BeNull("a running session has not stopped for any reason");
    }

    [Fact]
    public async Task Start_WithNoCapAndNoExplicitOptOut_IsRejected()
    {
        var projectId = await SeedProjectAsync("Autopilot No Cap");

        // A missing cap must never be read as "unlimited". The two are one keystroke apart and
        // only one of them can empty a wallet overnight.
        var resp = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/autopilot", new StartAutopilotRequest());

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var status = await _client.GetFromJsonAsync<AutopilotStatus>(
            $"/api/projects/{projectId}/autopilot", TestJsonOptions.Default);
        status!.Running.Should().BeFalse("a rejected start must not have opened anything");
    }

    [Fact]
    public async Task Start_WithANonPositiveCap_IsRejected()
    {
        var projectId = await SeedProjectAsync("Autopilot Zero Cap");

        var resp = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/autopilot", new StartAutopilotRequest(CapUsd: 0m));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Start_Uncapped_IsAllowedForAnOwner()
    {
        var projectId = await SeedProjectAsync("Autopilot Uncapped");

        // The test client authenticates as the org's original root, who holds BillingManage.
        var resp = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/autopilot", new StartAutopilotRequest(Uncapped: true));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var status = await resp.Content.ReadFromJsonAsync<AutopilotStatus>(TestJsonOptions.Default);

        status!.Running.Should().BeTrue();
        status.CapUsd.Should().BeNull("uncapped is the explicit no-ceiling case");
    }

    [Fact]
    public async Task Start_IsIdempotent()
    {
        var projectId = await SeedProjectAsync("Autopilot Twice");

        var first = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/autopilot", new StartAutopilotRequest(CapUsd: 5m));
        var second = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/autopilot", new StartAutopilotRequest(CapUsd: 999m));

        second.StatusCode.Should().Be(HttpStatusCode.OK);

        var firstStatus = await first.Content.ReadFromJsonAsync<AutopilotStatus>(TestJsonOptions.Default);
        var secondStatus = await second.Content.ReadFromJsonAsync<AutopilotStatus>(TestJsonOptions.Default);

        // A double-click must not open a second session, and must not silently raise the cap
        // the first one was started with.
        secondStatus!.SessionId.Should().Be(firstStatus!.SessionId);
        secondStatus.CapUsd.Should().Be(5m);
    }

    [Fact]
    public async Task Stop_ClosesTheSession_AndIsIdempotent()
    {
        var projectId = await SeedProjectAsync("Autopilot Stop");

        await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/autopilot", new StartAutopilotRequest(CapUsd: 10m));

        var stopped = await _client.DeleteAsync($"/api/projects/{projectId}/autopilot");
        stopped.StatusCode.Should().Be(HttpStatusCode.OK);

        var status = await stopped.Content.ReadFromJsonAsync<AutopilotStatus>(TestJsonOptions.Default);
        status!.Running.Should().BeFalse();
        status.StopReason.Should().Be(nameof(AutopilotStopReason.Manual));

        // Stopping an already-stopped session is a state the user asked for, not a transition
        // they raced — two people hitting stop should both see "it's off".
        var again = await _client.DeleteAsync($"/api/projects/{projectId}/autopilot");
        again.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Restart_AfterStop_OpensAFreshSessionWithAFreshCap()
    {
        var projectId = await SeedProjectAsync("Autopilot Restart");

        var first = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/autopilot", new StartAutopilotRequest(CapUsd: 10m));
        var firstStatus = await first.Content.ReadFromJsonAsync<AutopilotStatus>(TestJsonOptions.Default);

        await _client.DeleteAsync($"/api/projects/{projectId}/autopilot");

        var second = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/autopilot", new StartAutopilotRequest(CapUsd: 30m));
        var secondStatus = await second.Content.ReadFromJsonAsync<AutopilotStatus>(TestJsonOptions.Default);

        // Per-session semantics: untoggle resets. The cap is a session budget, not a ceiling.
        secondStatus!.SessionId.Should().NotBe(firstStatus!.SessionId);
        secondStatus.CapUsd.Should().Be(30m);
        secondStatus.SpentUsd.Should().Be(0m);
        secondStatus.TasksSubmitted.Should().Be(0);
    }

    [Fact]
    public async Task Status_OnAProjectThatNeverRanAutopilot_IsOffAndEmpty()
    {
        var projectId = await SeedProjectAsync("Autopilot Never");

        var status = await _client.GetFromJsonAsync<AutopilotStatus>(
            $"/api/projects/{projectId}/autopilot", TestJsonOptions.Default);

        status!.Running.Should().BeFalse();
        status.SessionId.Should().BeNull();
        status.StopReason.Should().BeNull();
    }

    [Fact]
    public async Task Status_ReportsSubmittableDraftingWork()
    {
        var projectId = await SeedProjectAsync("Autopilot Drafting Count");

        var status = await _client.GetFromJsonAsync<AutopilotStatus>(
            $"/api/projects/{projectId}/autopilot", TestJsonOptions.Default);

        // Zero here with work in flight is normal mid-wave; the board needs the number so it
        // can say "waiting on 2 backend tasks" instead of looking stalled.
        status!.DraftingRemaining.Should().Be(0);
    }
}
