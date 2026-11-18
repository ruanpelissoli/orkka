namespace Orkka.IntegrationTests;

using System.Net;
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

[Collection("Integration")]
public sealed class TaskEndpointTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<(Ulid ProjectId, Ulid RepoId)> SeedProjectAndRepo()
    {
        var projectResp = await _client.PostAsJsonAsync("/api/projects",
            new CreateProjectRequest("Task Test Project", "desc"));
        var project = await projectResp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);

        var repoResp = await _client.PostAsJsonAsync($"/api/projects/{project!.Id}/repos",
            new AddProjectRepoRequest("test-repo", "https://github.com/test/repo.git", "main", null, null));
        var repo = await repoResp.Content.ReadFromJsonAsync<ProjectRepoResponse>(TestJsonOptions.Default);

        return (project.Id, repo!.Id);
    }

    private async Task<TaskResponse> CreateTask(Ulid projectId, Ulid repoId, string title = "Test Task")
    {
        var request = new CreateTaskRequest(repoId, title, "Description", "Medium");
        var response = await _client.PostAsJsonAsync($"/api/projects/{projectId}/tasks", request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default))!;
    }

    private async Task<List<TaskResponse>> ListTasks(Ulid projectId, string query)
    {
        var response = await _client.GetAsync($"/api/projects/{projectId}/tasks?{query}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<List<TaskResponse>>(TestJsonOptions.Default))!;
    }

    [Fact]
    public async Task CreateTask_ReturnsCreatedInDraftingLane()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();

        var task = await CreateTask(projectId, repoId);

        task.Title.Should().Be("Test Task");
        task.CurrentLane.Should().Be("Drafting");
        task.Status.Should().Be("Draft");
        task.Priority.Should().Be("Medium");
        task.RepoId.Should().Be(repoId);
        task.IsArchived.Should().BeFalse();
    }

    [Fact]
    public async Task CreateTask_InvalidRepo_ReturnsBadRequest()
    {
        var (projectId, _) = await SeedProjectAndRepo();

        var request = new CreateTaskRequest(Ulid.NewUlid(), "Task", "Desc");
        var response = await _client.PostAsJsonAsync($"/api/projects/{projectId}/tasks", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetTask_ReturnsTask()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        var created = await CreateTask(projectId, repoId);

        var response = await _client.GetAsync($"/api/tasks/{created.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var task = await response.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default);
        task!.Id.Should().Be(created.Id);
        task.Title.Should().Be("Test Task");
    }

    [Fact]
    public async Task GetTask_NonExistent_ReturnsNotFound()
    {
        var response = await _client.GetAsync($"/api/tasks/{Ulid.NewUlid()}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateTask_UpdatesTitle()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        var created = await CreateTask(projectId, repoId);

        var updateReq = new UpdateTaskRequest("Updated Title", null, null, null, null, null);
        var response = await _client.PatchAsJsonAsync($"/api/tasks/{created.Id}", updateReq);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await response.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default);
        updated!.Title.Should().Be("Updated Title");
    }

    [Fact]
    public async Task CreateTask_WithType_SetsType()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();

        var request = new CreateTaskRequest(repoId, "Bug task", "Fix it", "High", "BugFix");
        var response = await _client.PostAsJsonAsync($"/api/projects/{projectId}/tasks", request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var task = await response.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default);
        task!.Type.Should().Be("BugFix");
    }

    [Fact]
    public async Task ListTasks_Search_EscapesLikeWildcards()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        await CreateTask(projectId, repoId, "Ordinary task");
        var match = await CreateTask(projectId, repoId, "Progress at 50% done");

        // sanity: the task is findable by a plain word
        var plain = await ListTasks(projectId, "search=Progress");
        plain.Should().ContainSingle(t => t.Id == match.Id);

        // "%" must match literally, not as a wildcard swallowing everything
        var results = await ListTasks(projectId, "search=50%25");

        results.Should().ContainSingle(t => t.Id == match.Id);
    }

    [Fact]
    public async Task CreateTask_DefaultType_IsFeature()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();

        var task = await CreateTask(projectId, repoId);
        task.Type.Should().Be("Feature");
    }

    [Fact]
    public async Task UpdateTask_UpdatesType()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        var created = await CreateTask(projectId, repoId);

        var updateReq = new UpdateTaskRequest(null, null, null, "Refactor", null, null);
        var response = await _client.PatchAsJsonAsync($"/api/tasks/{created.Id}", updateReq);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await response.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default);
        updated!.Type.Should().Be("Refactor");
    }

    [Fact]
    public async Task DeleteTask_DraftTask_ReturnsNoContent()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        var created = await CreateTask(projectId, repoId);

        var response = await _client.DeleteAsync($"/api/tasks/{created.Id}");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var getResp = await _client.GetAsync($"/api/tasks/{created.Id}");
        getResp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SubmitTask_MovesToReadyAndInQueue()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        var created = await CreateTask(projectId, repoId);

        var response = await _client.PostAsync($"/api/tasks/{created.Id}/submit", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var submitted = await response.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default);
        submitted!.CurrentLane.Should().Be("Ready");
        submitted.Status.Should().Be("InQueue");
        submitted.QueuePosition.Should().Be(1);
    }

    [Fact]
    public async Task SubmitTask_AlreadySubmitted_ReturnsBadRequest()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        var created = await CreateTask(projectId, repoId);

        await _client.PostAsync($"/api/tasks/{created.Id}/submit", null);
        var response = await _client.PostAsync($"/api/tasks/{created.Id}/submit", null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ListTasks_ReturnsProjectTasks()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        await CreateTask(projectId, repoId, "Task 1");
        await CreateTask(projectId, repoId, "Task 2");

        var response = await _client.GetAsync($"/api/projects/{projectId}/tasks");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var tasks = await response.Content.ReadFromJsonAsync<List<TaskResponse>>(TestJsonOptions.Default);
        tasks!.Count.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task GetBoard_ReturnsLanesWithTasks()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        await CreateTask(projectId, repoId, "Board Task");

        var response = await _client.GetAsync($"/api/projects/{projectId}/board");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var board = await response.Content.ReadFromJsonAsync<TaskBoardResponse>(TestJsonOptions.Default);
        board!.Lanes.Should().NotBeEmpty();

        var draftingLane = board.Lanes.FirstOrDefault(l => l.Key == "drafting");
        draftingLane.Should().NotBeNull();
        draftingLane!.Tasks.Should().Contain(t => t.Title == "Board Task");
    }

    [Fact]
    public async Task ArchiveAndUnarchive_WorksCorrectly()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        var created = await CreateTask(projectId, repoId);

        // Archive
        var archiveResp = await _client.PostAsync($"/api/tasks/{created.Id}/archive", null);
        archiveResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var archived = await archiveResp.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default);
        archived!.IsArchived.Should().BeTrue();
        archived.Status.Should().Be("Archived");
        archived.ArchivedAt.Should().NotBeNull();

        // Board should not include archived task
        var boardResp = await _client.GetAsync($"/api/projects/{projectId}/board");
        var board = await boardResp.Content.ReadFromJsonAsync<TaskBoardResponse>(TestJsonOptions.Default);
        board!.Lanes.SelectMany(l => l.Tasks).Should().NotContain(t => t.Id == created.Id);

        // Unarchive
        var unarchiveResp = await _client.PostAsync($"/api/tasks/{created.Id}/unarchive", null);
        unarchiveResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var unarchived = await unarchiveResp.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default);
        unarchived!.IsArchived.Should().BeFalse();
        unarchived.Status.Should().Be("Draft");
    }

    [Fact]
    public async Task BlockAndUnblock_WorksCorrectly()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        var created = await CreateTask(projectId, repoId);

        // Submit first (can't block a draft)
        await _client.PostAsync($"/api/tasks/{created.Id}/submit", null);

        // Block
        var blockResp = await _client.PostAsync($"/api/tasks/{created.Id}/block", null);
        blockResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var blocked = await blockResp.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default);
        blocked!.Status.Should().Be("Blocked");

        // Unblock
        var unblockResp = await _client.PostAsync($"/api/tasks/{created.Id}/unblock", null);
        unblockResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var unblocked = await unblockResp.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default);
        unblocked!.Status.Should().Be("InQueue");
    }

    [Fact]
    public async Task MoveTaskLane_ForwardMove_Succeeds()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        var created = await CreateTask(projectId, repoId);
        await _client.PostAsync($"/api/tasks/{created.Id}/submit", null);

        var moveReq = new MoveTaskLaneRequest("Planning");
        var response = await _client.PatchAsJsonAsync($"/api/tasks/{created.Id}/lane", moveReq);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var moved = await response.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default);
        moved!.CurrentLane.Should().Be("Planning");
        moved.Status.Should().Be("Active");
    }

    [Fact]
    public async Task MoveTaskLane_BackToDrafting_Fails()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        var created = await CreateTask(projectId, repoId);
        await _client.PostAsync($"/api/tasks/{created.Id}/submit", null);

        var moveReq = new MoveTaskLaneRequest("Drafting");
        var response = await _client.PatchAsJsonAsync($"/api/tasks/{created.Id}/lane", moveReq);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task MoveTaskLane_ToDeveloping_ReQueuesForAgent()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        var created = await CreateTask(projectId, repoId);
        await _client.PostAsync($"/api/tasks/{created.Id}/submit", null);

        var response = await _client.PatchAsJsonAsync($"/api/tasks/{created.Id}/lane",
            new MoveTaskLaneRequest("Developing"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var moved = await response.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default);
        moved!.CurrentLane.Should().Be("Developing");
        moved.Status.Should().Be("InQueue");
    }

    [Fact]
    public async Task MoveTaskLane_OutOfDone_UnarchivesAndReQueues()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        var created = await CreateTask(projectId, repoId);
        await _client.PostAsync($"/api/tasks/{created.Id}/submit", null);

        var doneResp = await _client.PatchAsJsonAsync($"/api/tasks/{created.Id}/lane",
            new MoveTaskLaneRequest("Done"));
        var done = await doneResp.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default);
        done!.IsArchived.Should().BeTrue();

        var response = await _client.PatchAsJsonAsync($"/api/tasks/{created.Id}/lane",
            new MoveTaskLaneRequest("Developing"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var moved = await response.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default);
        moved!.CurrentLane.Should().Be("Developing");
        moved.Status.Should().Be("InQueue");
        moved.IsArchived.Should().BeFalse();
    }

    [Fact]
    public async Task MoveTaskLane_OutOfDone_ClearsTheSyntheticMergedFlag()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        var created = await CreateTask(projectId, repoId);
        await _client.PostAsync($"/api/tasks/{created.Id}/submit", null);

        // A no-PR task moved to Done by hand is stamped IsMerged so dependents unblock.
        var doneResp = await _client.PatchAsJsonAsync($"/api/tasks/{created.Id}/lane",
            new MoveTaskLaneRequest("Done"));
        (await doneResp.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default))!
            .IsMerged.Should().BeTrue();

        var response = await _client.PatchAsJsonAsync($"/api/tasks/{created.Id}/lane",
            new MoveTaskLaneRequest("Developing"));

        // Reopened, the stamp comes off — otherwise a PR it earns later is never landed, because
        // the merge sweep only considers tasks that are NOT yet merged.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var moved = await response.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default);
        moved!.IsMerged.Should().BeFalse();
    }

    [Fact]
    public async Task MoveTaskLane_IntoCodeReview_QueuesTheReviewer()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        var created = await CreateTask(projectId, repoId);
        await _client.PostAsync($"/api/tasks/{created.Id}/submit", null);

        var response = await _client.PatchAsJsonAsync($"/api/tasks/{created.Id}/lane",
            new MoveTaskLaneRequest("CodeReview"));

        // A drag into Code Review is a request for a review. WaitingCodeReview here meant
        // the dispatcher (which only claims InQueue) never ran the reviewer — the literal
        // "automatic code review doesn't work" report.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var moved = await response.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default);
        moved!.CurrentLane.Should().Be("CodeReview");
        moved.Status.Should().Be("InQueue");
    }

    [Fact]
    public async Task FindTaskIdByBranch_IsScopedToTheDeliveringRepos()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        var created = await CreateTask(projectId, repoId);
        var friendlyBranch = "orkka/SCOPE-1";
        var ulidBranch = $"orkka/task-{created.Id}";

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var entity = await db.Tasks.FirstAsync(t => t.Id == created.Id);
        entity.BranchName = friendlyBranch;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var tasksApi = scope.ServiceProvider.GetRequiredService<ITasksApi>();
        var unrelatedRepo = Ulid.NewUlid();

        // Friendly names are not globally unique and both conventions are repo-author-chosen
        // content — a webhook match outside the delivering repo could complete another org's
        // task, so a foreign repo set must resolve to nothing.
        (await tasksApi.FindTaskIdByBranchAsync(friendlyBranch, [repoId])).Should().Be(created.Id);
        (await tasksApi.FindTaskIdByBranchAsync(friendlyBranch, [unrelatedRepo])).Should().BeNull();
        (await tasksApi.FindTaskIdByBranchAsync(ulidBranch, [repoId])).Should().Be(created.Id);
        (await tasksApi.FindTaskIdByBranchAsync(ulidBranch, [unrelatedRepo])).Should().BeNull();
        (await tasksApi.FindTaskIdByBranchAsync(friendlyBranch, [])).Should().BeNull();
    }

    [Fact]
    public async Task CountAutoAnsweredMessages_TranslatesOnTheRealDatabase()
    {
        // Once a JSON containment query that only a real database could reject; it reached
        // production and Blocked every task entering PM review because nothing exercised it.
        // Now a match on the JSON text — this pins that it translates and counts only true.
        var (projectId, repoId) = await SeedProjectAndRepo();
        var created = await CreateTask(projectId, repoId);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var task = await db.Tasks.FirstAsync(t => t.Id == created.Id);
        db.TaskMessages.AddRange(
            new Orkka.Modules.Tasks.Domain.TaskMessage
            {
                Id = Ulid.NewUlid(), TaskId = task.Id, OrgId = task.OrgId, Role = "user", Content = "auto",
                MessageType = MessageType.Answer, Metadata = """{"autoAnswered":true}""",
            },
            new Orkka.Modules.Tasks.Domain.TaskMessage
            {
                Id = Ulid.NewUlid(), TaskId = task.Id, OrgId = task.OrgId, Role = "user", Content = "manual",
                MessageType = MessageType.Answer, Metadata = """{"autoAnswered":false}""",
            },
            new Orkka.Modules.Tasks.Domain.TaskMessage
            {
                Id = Ulid.NewUlid(), TaskId = task.Id, OrgId = task.OrgId, Role = "user", Content = "plain",
                MessageType = MessageType.Text,
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var count = await scope.ServiceProvider.GetRequiredService<ITasksApi>()
            .CountAutoAnsweredMessagesAsync(created.Id);

        count.Should().Be(1);
    }

    [Fact]
    public async Task MoveTaskLane_BackwardOutOfDone_IsRefusedOnceThePrHasMerged()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        var created = await CreateTask(projectId, repoId);
        await _client.PostAsync($"/api/tasks/{created.Id}/submit", null);

        // Put it where the merge poller leaves a task: PR merged, Done, Completed.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
            var entity = await db.Tasks.FirstAsync(t => t.Id == created.Id);
            entity.CurrentLane = Lane.CodeReview;
            entity.BranchName = "orkka/DEN-1";
            entity.PrUrl = "https://github.com/test/x/pull/2";
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            await scope.ServiceProvider.GetRequiredService<ITasksApi>()
                .MarkTaskMergedAsync(created.Id, "merge-sha");
        }

        foreach (var lane in new[] { "CodeReview", "Developing" })
        {
            var response = await _client.PatchAsJsonAsync($"/api/tasks/{created.Id}/lane",
                new MoveTaskLaneRequest(lane));

            // The code is on the base branch and the PR is closed. Moving the task back cannot
            // change that — it can only strand it: no approve button (PR not open), skipped by
            // the merge sweep (already merged), reviewed in a loop with nowhere to go. Same
            // rule Reset already applies to merged tasks.
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
                $"a merged task must not be movable back to {lane}");
            var body = await response.Content.ReadAsStringAsync();
            body.Should().Contain("already merged");
        }

        var task = await _client.GetFromJsonAsync<TaskResponse>(
            $"/api/tasks/{created.Id}", TestJsonOptions.Default);
        task!.CurrentLane.Should().Be("Done");
        task.IsMerged.Should().BeTrue();
    }

    [Fact]
    public async Task ListTasks_FilterByArchived_Works()
    {
        var (projectId, repoId) = await SeedProjectAndRepo();
        var task = await CreateTask(projectId, repoId, "Archived Task");
        await _client.PostAsync($"/api/tasks/{task.Id}/archive", null);

        // Default: non-archived
        var defaultResp = await _client.GetAsync($"/api/projects/{projectId}/tasks");
        var defaultTasks = await defaultResp.Content.ReadFromJsonAsync<List<TaskResponse>>(TestJsonOptions.Default);
        defaultTasks!.Should().NotContain(t => t.Id == task.Id);

        // Archived filter
        var archivedResp = await _client.GetAsync($"/api/projects/{projectId}/tasks?IsArchived=true");
        var archivedTasks = await archivedResp.Content.ReadFromJsonAsync<List<TaskResponse>>(TestJsonOptions.Default);
        archivedTasks!.Should().Contain(t => t.Id == task.Id);
    }
}
