namespace Orkka.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Contracts;
using Orkka.IntegrationTests.Fixtures;
using Orkka.IntegrationTests.Helpers;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.Modules.Projects.Features.ProjectRepos;
using Orkka.Modules.Projects.Features.Projects;
using Orkka.Modules.Tasks.Features.TaskPlans;
using Orkka.Modules.Tasks.Features.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

[Collection("Integration")]
public sealed class TaskPlanEndpointTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<(TaskResponse Task, Ulid PlanId)> SeedTaskWithPlan()
    {
        var projectResp = await _client.PostAsJsonAsync("/api/projects",
            new CreateProjectRequest("Plan Test Project", null));
        var project = await projectResp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);

        var repoResp = await _client.PostAsJsonAsync($"/api/projects/{project!.Id}/repos",
            new AddProjectRepoRequest("plan-repo", "https://github.com/test/plan-repo.git", "main", null, null));
        var repo = await repoResp.Content.ReadFromJsonAsync<ProjectRepoResponse>(TestJsonOptions.Default);

        var taskReq = new CreateTaskRequest(repo!.Id, "Plan Task", "desc");
        var taskResp = await _client.PostAsJsonAsync($"/api/projects/{project.Id}/tasks", taskReq);
        var task = (await taskResp.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default))!;

        // Submit task and move to PlanReady
        await _client.PostAsync($"/api/tasks/{task.Id}/submit", null);
        await _client.PatchAsJsonAsync($"/api/tasks/{task.Id}/lane", new MoveTaskLaneRequest("PlanReady"));

        // Seed a plan directly via DB
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var plan = new TaskPlan
        {
            TaskId = task.Id,
            OrgId = TestCurrentUser.TestOrgId,
            Version = 1,
            Content = new PlanContent
            {
                Summary = "Fix the bug in the login flow",
                Approach = "1. Analyze code\n2. Implement fix\n3. Add tests"
            },
            Status = PlanStatus.PendingReview
        };
        db.TaskPlans.Add(plan);
        await db.SaveChangesAsync();

        return (task, plan.Id);
    }

    [Fact]
    public async Task ListPlans_ReturnsPlans()
    {
        var (task, _) = await SeedTaskWithPlan();

        var response = await _client.GetAsync($"/api/tasks/{task.Id}/plans");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var plans = await response.Content.ReadFromJsonAsync<List<TaskPlanResponse>>(TestJsonOptions.Default);
        plans!.Should().HaveCountGreaterThanOrEqualTo(1);
        plans[0].Status.Should().Be("PendingReview");
    }

    [Fact]
    public async Task GetLatestPlan_ReturnsMostRecent()
    {
        var (task, _) = await SeedTaskWithPlan();

        var response = await _client.GetAsync($"/api/tasks/{task.Id}/plans/latest");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var plan = await response.Content.ReadFromJsonAsync<TaskPlanResponse>(TestJsonOptions.Default);
        plan!.Version.Should().Be(1);
    }

    [Fact]
    public async Task ReviewPlan_Approve_MoveToNextLane()
    {
        var (task, planId) = await SeedTaskWithPlan();

        var reviewReq = new ReviewPlanRequest("approve", "Looks good!");
        var response = await _client.PostAsJsonAsync(
            $"/api/tasks/{task.Id}/plans/{planId}/review", reviewReq);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var reviewed = await response.Content.ReadFromJsonAsync<TaskPlanResponse>(TestJsonOptions.Default);
        reviewed!.Status.Should().Be("Approved");
        reviewed.Feedback.Should().Be("Looks good!");
        reviewed.ApprovedAt.Should().NotBeNull();

        // Verify task moved forward
        var taskResp = await _client.GetAsync($"/api/tasks/{task.Id}");
        var updatedTask = await taskResp.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default);
        updatedTask!.CurrentLane.Should().NotBe("PlanReady");
    }

    [Fact]
    public async Task ReviewPlan_Reject_MovesBackToPlanning()
    {
        var (task, planId) = await SeedTaskWithPlan();

        var reviewReq = new ReviewPlanRequest("reject", "Needs more detail");
        var response = await _client.PostAsJsonAsync(
            $"/api/tasks/{task.Id}/plans/{planId}/review", reviewReq);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Verify task moved back to Planning
        var taskResp = await _client.GetAsync($"/api/tasks/{task.Id}");
        var updatedTask = await taskResp.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default);
        updatedTask!.CurrentLane.Should().Be("Planning");
        updatedTask.Status.Should().Be("InQueue");
    }
}
