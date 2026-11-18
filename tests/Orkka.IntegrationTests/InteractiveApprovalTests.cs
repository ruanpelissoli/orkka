namespace Orkka.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Orkka.IntegrationTests.Fixtures;
using Orkka.IntegrationTests.Helpers;
using Orkka.Modules.Projects.Features.ProjectRepos;
using Orkka.Modules.Projects.Features.Projects;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Features.InteractiveSessions;
using Orkka.Modules.Tasks.Features.Tasks;

/// <summary>
/// The approval round-trip an interactive session pauses on: a command is raised, a human rules,
/// and the agent's hook — blocked in another call entirely — is released with that answer.
///
/// The properties worth protecting are the ones that fail silently. A prompt must settle exactly
/// once no matter how many people click; every path that is not an explicit human "yes" must come
/// back a refusal; and a prompt whose run has died must not sit on screen forever.
/// </summary>
[Collection("Integration")]
public sealed class InteractiveApprovalTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private IServiceScope CreateScope() => factory.Services.CreateScope();

    private async Task<(Ulid TaskId, Ulid OrgId)> OpenSessionAsync(string projectName)
    {
        var projectResp = await _client.PostAsJsonAsync("/api/projects",
            new CreateProjectRequest(projectName, "desc"));
        var project = await projectResp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);

        var repoResp = await _client.PostAsJsonAsync($"/api/projects/{project!.Id}/repos",
            new AddProjectRepoRequest(
                "approval-repo", "https://github.com/test/approval.git", "main", null, null));
        var repo = await repoResp.Content.ReadFromJsonAsync<ProjectRepoResponse>(TestJsonOptions.Default);

        var sessionResp = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/interactive-sessions",
            new StartInteractiveSessionRequest(repo!.Id, "Approval session"));
        var task = await sessionResp.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default);

        using var scope = CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var snapshot = await api.GetTaskSnapshotAsync(task!.Id);

        return (task.Id, snapshot!.OrgId);
    }

    /// <summary>Raises a prompt the way the runner's hook does — as a call that blocks — and hands
    /// back the still-running wait plus the id the browser would be given.</summary>
    private async Task<(Task<ApprovalOutcome> Wait, Ulid ApprovalId)> RaiseAsync(
        Ulid orgId, Ulid taskId, string command, string key, TimeSpan? timeout = null)
    {
        var coordinator = factory.Services.GetRequiredService<IInteractiveApprovalCoordinator>();

        var toolUseId = $"toolu_{Ulid.NewUlid()}";
        var wait = coordinator.RequestAsync(
            orgId, taskId, executionId: null, toolUseId, "Bash", command, key,
            timeout ?? TimeSpan.FromSeconds(30), CancellationToken.None);

        // The row exists before the browser is told about it, so polling for it is how a test
        // learns the id without racing the push.
        Ulid approvalId = default;
        for (var attempt = 0; attempt < 50 && approvalId == default; attempt++)
        {
            var pending = await coordinator.ListPendingAsync(orgId, taskId, CancellationToken.None);
            var match = pending.FirstOrDefault(p => p.ToolUseId == toolUseId);
            if (match is not null) approvalId = match.Id;
            else await Task.Delay(20);
        }

        approvalId.Should().NotBe(default, "the prompt should be persisted before anyone can answer it");
        return (wait, approvalId);
    }

    [Fact]
    public async Task Allow_ReleasesTheBlockedAgent()
    {
        var (taskId, orgId) = await OpenSessionAsync("Approval Allow");
        var (wait, approvalId) = await RaiseAsync(orgId, taskId, "git push origin main", "git push");

        var response = await _client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/approvals/{approvalId}", new DecideApprovalRequest("allow"));
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await wait).Should().Be(ApprovalOutcome.Allowed);
    }

    [Fact]
    public async Task AllowForSession_IsDistinctFromASingleAllow()
    {
        // The guard uses the distinction to stop asking about this key for the rest of the run,
        // so collapsing the two here would quietly turn a session grant into a one-shot.
        var (taskId, orgId) = await OpenSessionAsync("Approval Session Grant");
        var (wait, approvalId) = await RaiseAsync(orgId, taskId, "npm install zod", "npm install");

        await _client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/approvals/{approvalId}", new DecideApprovalRequest("allow_session"));

        (await wait).Should().Be(ApprovalOutcome.AllowedForSession);
    }

    [Fact]
    public async Task Deny_ReleasesTheAgentWithARefusal()
    {
        var (taskId, orgId) = await OpenSessionAsync("Approval Deny");
        var (wait, approvalId) = await RaiseAsync(orgId, taskId, "rm -rf build", "rm");

        await _client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/approvals/{approvalId}", new DecideApprovalRequest("deny"));

        (await wait).Should().Be(ApprovalOutcome.Denied);
    }

    [Fact]
    public async Task SecondDecision_Is409_NotASilentOverwrite()
    {
        // Two people can watch the same session. The loser must be told their click did nothing,
        // rather than believing they allowed something they did not.
        var (taskId, orgId) = await OpenSessionAsync("Approval Double Click");
        var (wait, approvalId) = await RaiseAsync(orgId, taskId, "git push", "git push");

        var first = await _client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/approvals/{approvalId}", new DecideApprovalRequest("allow"));
        first.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var second = await _client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/approvals/{approvalId}", new DecideApprovalRequest("deny"));
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);

        (await wait).Should().Be(ApprovalOutcome.Allowed, "the first ruling stands");
    }

    [Fact]
    public async Task Timeout_Expires_AndDeniesRatherThanHanging()
    {
        var (taskId, orgId) = await OpenSessionAsync("Approval Timeout");
        var (wait, approvalId) = await RaiseAsync(
            orgId, taskId, "curl https://example.com", "curl", TimeSpan.FromMilliseconds(300));

        (await wait).Should().Be(ApprovalOutcome.Expired,
            "an unanswered prompt must never resolve to permission");

        using var scope = CreateScope();
        var coordinator = scope.ServiceProvider.GetRequiredService<IInteractiveApprovalCoordinator>();
        var pending = await coordinator.ListPendingAsync(orgId, taskId, CancellationToken.None);
        pending.Should().NotContain(p => p.Id == approvalId,
            "an expired prompt must stop being offered to the human");
    }

    [Fact]
    public async Task DecidingAnExpiredPrompt_Fails()
    {
        var (taskId, orgId) = await OpenSessionAsync("Approval Late Click");
        var (wait, approvalId) = await RaiseAsync(
            orgId, taskId, "docker compose up", "docker compose", TimeSpan.FromMilliseconds(300));

        await wait;

        var late = await _client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/approvals/{approvalId}", new DecideApprovalRequest("allow"));
        late.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "answering after expiry must not resurrect a command the agent was already refused");
    }

    [Fact]
    public async Task ExpirePending_ReleasesEverythingWaitingOnADeadRun()
    {
        var (taskId, orgId) = await OpenSessionAsync("Approval Session Ended");
        var (wait, _) = await RaiseAsync(orgId, taskId, "git push", "git push", TimeSpan.FromMinutes(5));

        var coordinator = factory.Services.GetRequiredService<IInteractiveApprovalCoordinator>();
        await coordinator.ExpirePendingAsync(taskId, CancellationToken.None);

        (await wait).Should().Be(ApprovalOutcome.Expired);
        (await coordinator.ListPendingAsync(orgId, taskId, CancellationToken.None))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task RaisingTheSameToolCallTwice_ReusesOnePrompt()
    {
        // The runner retries its hub invoke across a reconnect. A second row would mean a second
        // card for a command the human may have already ruled on.
        var (taskId, orgId) = await OpenSessionAsync("Approval Retry");
        var coordinator = factory.Services.GetRequiredService<IInteractiveApprovalCoordinator>();

        var toolUseId = $"toolu_{Ulid.NewUlid()}";
        var first = coordinator.RequestAsync(
            orgId, taskId, null, toolUseId, "Bash", "git push", "git push",
            TimeSpan.FromSeconds(5), CancellationToken.None);
        var second = coordinator.RequestAsync(
            orgId, taskId, null, toolUseId, "Bash", "git push", "git push",
            TimeSpan.FromSeconds(5), CancellationToken.None);

        // Both calls persist asynchronously, so poll for the row rather than assuming it has
        // landed — asserting immediately raced the insert and made this test flaky.
        IReadOnlyList<PendingApproval> pending = [];
        for (var attempt = 0; attempt < 50; attempt++)
        {
            pending = await coordinator.ListPendingAsync(orgId, taskId, CancellationToken.None);
            if (pending.Any(p => p.ToolUseId == toolUseId)) break;
            await Task.Delay(20);
        }

        pending.Should().ContainSingle(p => p.ToolUseId == toolUseId);

        await coordinator.ExpirePendingAsync(taskId, CancellationToken.None);
        await Task.WhenAll(first, second);
    }
}
