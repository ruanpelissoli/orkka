namespace Orkka.IntegrationTests;

using System.Net.Http.Json;
using Orkka.IntegrationTests.Fixtures;
using Orkka.IntegrationTests.Helpers;
using Orkka.Modules.Projects.Features.ProjectRepos;
using Orkka.Modules.Projects.Features.Projects;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Features.Tasks;
using Orkka.Modules.Tasks.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>
/// Multi-worker safety: the orchestration lease is what prevents two worker replicas
/// from running the same task. These tests exercise the atomic acquire/extend/release
/// semantics against the real database (ExecuteUpdate paths).
/// </summary>
[Collection("Integration")]
public sealed class TaskLeaseTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<Ulid> SeedTaskAsync()
    {
        var projectResp = await _client.PostAsJsonAsync("/api/projects",
            new CreateProjectRequest("Lease Test Project", "desc"));
        var project = await projectResp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);

        var repoResp = await _client.PostAsJsonAsync($"/api/projects/{project!.Id}/repos",
            new AddProjectRepoRequest("lease-repo", "https://github.com/test/lease.git", "main", null, null));
        var repo = await repoResp.Content.ReadFromJsonAsync<ProjectRepoResponse>(TestJsonOptions.Default);

        var taskResp = await _client.PostAsJsonAsync($"/api/projects/{project.Id}/tasks",
            new CreateTaskRequest(repo!.Id, "Lease task", "desc", "Medium"));
        var task = await taskResp.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default);
        return task!.Id;
    }

    private IServiceScope CreateScope() => factory.Services.CreateScope();

    [Fact]
    public async Task AcquireLease_UnleasedTask_Succeeds_ThenBlocksOtherOwners()
    {
        var taskId = await SeedTaskAsync();
        using var scope = CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();

        (await api.TryAcquireTaskLeaseAsync(taskId, "worker-a", TimeSpan.FromMinutes(5)))
            .Should().BeTrue("an unleased task must be acquirable");

        (await api.TryAcquireTaskLeaseAsync(taskId, "worker-b", TimeSpan.FromMinutes(5)))
            .Should().BeFalse("a live lease held by worker-a must block worker-b");

        (await api.TryAcquireTaskLeaseAsync(taskId, "worker-a", TimeSpan.FromMinutes(5)))
            .Should().BeTrue("the holder must be able to re-acquire its own lease");
    }

    [Fact]
    public async Task AcquireLease_ExpiredLease_OtherOwnerTakesOver()
    {
        var taskId = await SeedTaskAsync();
        using var scope = CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();

        // Negative duration = lease already expired the moment it was taken.
        (await api.TryAcquireTaskLeaseAsync(taskId, "worker-a", TimeSpan.FromMinutes(-1)))
            .Should().BeTrue();

        (await api.TryAcquireTaskLeaseAsync(taskId, "worker-b", TimeSpan.FromMinutes(5)))
            .Should().BeTrue("an expired lease means the owner died — takeover must succeed");
    }

    [Fact]
    public async Task ReleaseLease_OnlyTheOwnerReleases()
    {
        var taskId = await SeedTaskAsync();
        using var scope = CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();

        await api.TryAcquireTaskLeaseAsync(taskId, "worker-a", TimeSpan.FromMinutes(5));

        await api.ReleaseTaskLeaseAsync(taskId, "worker-b");
        (await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId)).LeaseOwner
            .Should().Be("worker-a", "a non-owner release must be a no-op");

        await api.ReleaseTaskLeaseAsync(taskId, "worker-a");
        (await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId)).LeaseOwner
            .Should().BeNull("the owner's release must clear the lease");
    }

    [Fact]
    public async Task TryClaimTask_TakesLease_AndSecondClaimerLoses()
    {
        var taskId = await SeedTaskAsync();
        using var scope = CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();

        await db.Tasks.Where(t => t.Id == taskId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, TaskStatus.InQueue));

        (await api.TryClaimTaskAsync(taskId, "worker-a", TimeSpan.FromMinutes(5)))
            .Should().BeTrue();
        (await api.TryClaimTaskAsync(taskId, "worker-b", TimeSpan.FromMinutes(5)))
            .Should().BeFalse("the claim moved the task out of InQueue");

        var task = await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId);
        task.LeaseOwner.Should().Be("worker-a");
        task.LeaseExpiresAt.Should().NotBeNull();
        task.Status.Should().Be(TaskStatus.Active);
        task.HasAgentWorking.Should().BeTrue();
    }

    [Fact]
    public async Task ListResumableTasks_ExcludesLiveLease_IncludesExpiredLease()
    {
        var taskId = await SeedTaskAsync();
        using var scope = CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();

        await db.Tasks.Where(t => t.Id == taskId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, TaskStatus.Active)
                .SetProperty(t => t.HasAgentWorking, true)
                .SetProperty(t => t.CurrentLane, Lane.Developing));

        await api.TryAcquireTaskLeaseAsync(taskId, "worker-a", TimeSpan.FromMinutes(5));
        (await api.ListResumableTasksAsync([Lane.Developing]))
            .Should().NotContain(t => t.Id == taskId, "a live lease means the task is being worked");

        await api.TryAcquireTaskLeaseAsync(taskId, "worker-a", TimeSpan.FromMinutes(-1));
        (await api.ListResumableTasksAsync([Lane.Developing]))
            .Should().Contain(t => t.Id == taskId, "an expired lease makes the task resumable");
    }

    [Fact]
    public async Task ExtendLeases_OnlyExtendsOwnLeases()
    {
        var taskId = await SeedTaskAsync();
        using var scope = CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();

        await api.TryAcquireTaskLeaseAsync(taskId, "worker-a", TimeSpan.FromMinutes(5));
        var before = (await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId)).LeaseExpiresAt;

        await api.ExtendTaskLeasesAsync([taskId], "worker-b", TimeSpan.FromMinutes(60));
        (await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId)).LeaseExpiresAt
            .Should().Be(before, "a non-owner heartbeat must not touch the lease");

        await api.ExtendTaskLeasesAsync([taskId], "worker-a", TimeSpan.FromMinutes(60));
        (await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId)).LeaseExpiresAt
            .Should().BeAfter(before!.Value, "the owner's heartbeat must extend the lease");
    }
}
