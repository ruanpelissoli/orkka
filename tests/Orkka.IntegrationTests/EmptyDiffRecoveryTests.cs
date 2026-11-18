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
/// An empty diff means the developer never committed — a mechanical miss, not a verdict.
///
/// Recovery retries like a rejected review, bounded by the same cycle cap. Escalation parks
/// the task as Developing/WaitingUserVerification — NOT WaitingCodeReview, which has no
/// resume path and whose "review required" affordances point at a PR that does not exist.
/// WaitingUserVerification is the status a chat reply resumes, so the human's answer
/// actually restarts the developer instead of landing in a parked conversation.
/// </summary>
[Collection("Integration")]
public sealed class EmptyDiffRecoveryTests(OrkkaApiFactory factory)
{
    // Must match TaskOrchestrator.MaxReviewCycles.
    private const int MaxReviewCycles = 2;

    private readonly HttpClient _client = factory.CreateClient();

    private IServiceScope CreateScope() => factory.Services.CreateScope();

    private async Task<Ulid> SeedTaskInReviewAsync()
    {
        var projectResp = await _client.PostAsJsonAsync("/api/projects",
            new CreateProjectRequest("Empty Diff Project", "desc"));
        var project = await projectResp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);

        var repoResp = await _client.PostAsJsonAsync($"/api/projects/{project!.Id}/repos",
            new AddProjectRepoRequest("ed-repo", "https://github.com/test/ed.git", "main", null, null));
        var repo = await repoResp.Content.ReadFromJsonAsync<ProjectRepoResponse>(TestJsonOptions.Default);

        var taskResp = await _client.PostAsJsonAsync($"/api/projects/{project.Id}/tasks",
            new CreateTaskRequest(repo!.Id, "Auth endpoints", "desc", "High"));
        var task = await taskResp.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default);

        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        await db.Tasks.Where(t => t.Id == task!.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.CurrentLane, Lane.CodeReview)
                .SetProperty(t => t.Status, TaskStatus.Active)
                .SetProperty(t => t.BranchName, "orkka/ED-1-r1"));

        return task!.Id;
    }

    [Fact]
    public async Task NoCommits_SendsTheTaskBackToDeveloping()
    {
        var taskId = await SeedTaskInReviewAsync();

        using var scope = CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();

        var transition = await api.ApplyNoCommitOutcomeAsync(taskId, MaxReviewCycles);

        transition.Should().NotBeNull();
        transition!.EscalatedToHuman.Should().BeFalse();
        transition.NewLane.Should().Be(Lane.Developing);

        var task = await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId);
        task.CurrentLane.Should().Be(Lane.Developing);
        task.Status.Should().Be(TaskStatus.InQueue,
            "the queue has to claim it again for the developer to get another run");
        task.HasAgentWorking.Should().BeFalse();
    }

    [Fact]
    public async Task RepeatedNoCommitRuns_EscalateToAStatusAHumanCanResume()
    {
        var taskId = await SeedTaskInReviewAsync();

        using var scope = CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();

        CodeReviewTransition? last = null;
        for (var i = 0; i < MaxReviewCycles; i++)
            last = await api.ApplyNoCommitOutcomeAsync(taskId, MaxReviewCycles);

        // A developer that cannot commit will not start committing on the fourth attempt —
        // the cap is what stops this becoming an unbounded spend loop.
        last!.EscalatedToHuman.Should().BeTrue();

        var task = await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId);
        task.CurrentLane.Should().Be(Lane.Developing,
            "the task parks in the phase that produced nothing, not in a review it never reached");
        task.Status.Should().Be(TaskStatus.WaitingUserVerification,
            "this is the only parked status a chat reply resumes — WaitingCodeReview is a dead end here");
        task.ReviewCycleCount.Should().Be(MaxReviewCycles);
    }

    [Fact]
    public async Task AReplyOnAnEscalatedTask_ResumesTheDeveloperWithFreshBudgets()
    {
        var taskId = await SeedTaskInReviewAsync();

        using (var scope = CreateScope())
        {
            var api = scope.ServiceProvider.GetRequiredService<ITasksApi>();
            for (var i = 0; i < MaxReviewCycles; i++)
                await api.ApplyNoCommitOutcomeAsync(taskId, MaxReviewCycles);
        }

        // This is the whole point of the escalation status: the human answers the agent's
        // question in the task chat, and the developer runs again with that answer.
        var resp = await _client.PostAsJsonAsync($"/api/tasks/{taskId}/messages",
            new Orkka.Modules.Tasks.Features.TaskMessages.SendMessageRequest("continue as planned"));
        resp.EnsureSuccessStatusCode();

        using var verifyScope = CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var task = await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId);

        task.CurrentLane.Should().Be(Lane.Developing);
        task.Status.Should().Be(TaskStatus.InQueue, "the reply must requeue the run, not just be stored");
        task.ReviewCycleCount.Should().Be(0,
            "the human's answer pays for a fresh retry budget — without the reset, the next "
            + "empty run would re-escalate immediately instead of retrying");
        task.QaCycleCount.Should().Be(0);
    }
}
