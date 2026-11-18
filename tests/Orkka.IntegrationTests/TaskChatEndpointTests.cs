namespace Orkka.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orkka.IntegrationTests.Fixtures;
using Orkka.IntegrationTests.Helpers;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Features.AgentChat;
using Orkka.Modules.Agents.Features.TaskChat;
using Orkka.Modules.Agents.Infrastructure;
using Orkka.Modules.Projects.Features.ProjectRepos;
using Orkka.Modules.Projects.Features.Projects;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Features.Tasks;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.Modules.Usage.Infrastructure;
using Orkka.SharedKernel.Ai;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>
/// A task's Discuss chat end to end (P8): GET creates the (task, agent) conversation lazily,
/// POST streams a turn through the agent-chat machinery with the task context and the task's
/// own workspace, and apply posts into the thread and does what the lane means. Own factory so
/// the runner utility (the summary) can be faked; joins the Integration collection.
/// </summary>
[Collection("Integration")]
public sealed class TaskChatEndpointTests : IAsyncLifetime
{
    private readonly OrkkaApiFactory _factory = new();
    private readonly FakeUtility _utility = new();
    private HttpClient _client = null!;

    private FakeChatAgentInvoker Fake => _factory.Services.GetRequiredService<FakeChatAgentInvoker>();

    public async Task InitializeAsync()
    {
        _factory.ConfigureTestServices = services =>
        {
            foreach (var d in services.Where(d => d.ServiceType == typeof(IUserRunnerUtility)).ToList())
                services.Remove(d);
            services.AddSingleton<IUserRunnerUtility>(_utility);
        };
        await _factory.InitializeAsync();
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private async Task<(Ulid ProjectId, Ulid TaskId)> SeedTaskAsync(
        Lane lane, TaskStatus status, Lane? questionSource = null, string? branch = null)
    {
        var projectResp = await _client.PostAsJsonAsync("/api/projects",
            new CreateProjectRequest($"Discuss {Ulid.NewUlid()}", null));
        var project = (await projectResp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default))!;
        var repoResp = await _client.PostAsJsonAsync($"/api/projects/{project.Id}/repos",
            new AddProjectRepoRequest("shop-api", "https://github.com/test/shop-api.git", "main", null, null));
        var repo = (await repoResp.Content.ReadFromJsonAsync<ProjectRepoResponse>(TestJsonOptions.Default))!;
        var taskResp = await _client.PostAsJsonAsync($"/api/projects/{project.Id}/tasks",
            new CreateTaskRequest(repo.Id, "Add invoice export", "Export invoices as CSV."));
        var task = (await taskResp.Content.ReadFromJsonAsync<TaskResponse>(TestJsonOptions.Default))!;

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var entity = await db.Tasks.IgnoreQueryFilters().FirstAsync(t => t.Id == task.Id);
        entity.CurrentLane = lane;
        entity.Status = status;
        entity.QuestionSourceLane = questionSource;
        entity.BranchName = branch;
        entity.ExternalKey = "SHOP-12";
        entity.ExternalUrl = "https://acme.atlassian.net/browse/SHOP-12";
        await db.SaveChangesAsync();
        return (project.Id, task.Id);
    }

    private async Task AddThreadMessageAsync(Ulid taskId, string role, MessageType type, string content)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        db.TaskMessages.Add(new TaskMessage
        {
            OrgId = TestCurrentUser.TestOrgId, TaskId = taskId, Role = role, MessageType = type, Content = content,
        });
        await db.SaveChangesAsync();
    }

    private async Task<Ulid> AddPendingPlanAsync(Ulid taskId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TasksDbContext>();
        var plan = new TaskPlan
        {
            TaskId = taskId, OrgId = TestCurrentUser.TestOrgId, Version = 1, Status = PlanStatus.PendingReview,
            Content = new PlanContent { Summary = "CSV endpoint", Approach = "Add an endpoint" },
        };
        db.TaskPlans.Add(plan);
        await db.SaveChangesAsync();
        return plan.Id;
    }

    private async Task<string> SendAsync(Ulid taskId, string content, string? agentId = null)
    {
        var response = await _client.PostAsJsonAsync($"/api/tasks/{taskId}/chat",
            new SendTaskChatMessageRequest(content, agentId));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadAsStringAsync();
    }

    private async Task<TaskChatResponse> GetChatAsync(Ulid taskId, string? agentId = null)
    {
        var response = await _client.GetAsync($"/api/tasks/{taskId}/chat{(agentId is null ? "" : $"?agentId={agentId}")}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<TaskChatResponse>(TestJsonOptions.Default))!;
    }

    private async Task<TaskEntity> ReadTaskAsync(Ulid taskId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TasksDbContext>()
            .Tasks.IgnoreQueryFilters().AsNoTracking().FirstAsync(t => t.Id == taskId);
    }

    private async Task<List<TaskMessage>> ReadThreadAsync(Ulid taskId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TasksDbContext>()
            .TaskMessages.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.TaskId == taskId).OrderBy(m => m.CreatedAt).ToListAsync();
    }

    [Fact]
    public async Task Get_CreatesTheConversationLazily_WithTheLaneAgent_AndHidesItFromTheTeamPage()
    {
        var (projectId, taskId) = await SeedTaskAsync(Lane.Ready, TaskStatus.Active);

        var first = await GetChatAsync(taskId);
        var second = await GetChatAsync(taskId);

        first.ConversationId.Should().Be(second.ConversationId);
        first.AgentRole.Should().Be("PM");
        first.DefaultAgentId.Should().Be(first.AgentId);
        first.Lane.Should().Be("Ready");
        first.Messages.Should().BeEmpty();
        first.Agents.Should().Contain(a => a.Role == "FullStackDev");

        // Picking another agent opens that agent's own conversation.
        var dev = first.Agents.First(a => a.Role == "FullStackDev");
        var devChat = await GetChatAsync(taskId, dev.Id);
        devChat.AgentId.Should().Be(dev.Id);
        devChat.ConversationId.Should().NotBe(first.ConversationId);
        devChat.DefaultAgentId.Should().Be(first.AgentId);

        var team = await _client.GetFromJsonAsync<List<ConversationResponse>>(
            $"/api/projects/{projectId}/agents/{first.AgentId}/conversations", TestJsonOptions.Default);
        team!.Should().NotContain(c => c.Id == first.ConversationId);

        (await _client.GetAsync($"/api/tasks/{Ulid.NewUlid()}/chat")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Post_StreamsATurn_WithTheTaskContext_OnTheTaskBranch_AndRecordsUsageAgainstTheTask()
    {
        var (_, taskId) = await SeedTaskAsync(Lane.Developing, TaskStatus.Active, branch: "feature/SHOP-12-export");
        await AddThreadMessageAsync(taskId, "tracker", MessageType.Text, "Customer needs it by Friday");
        Fake.EnqueueChunks(
            new ChatChunk(ChatChunkType.Text, "Read the export code.", ServedByRunner: true),
            new ChatChunk(ChatChunkType.Done, "", PromptTokens: 100, CompletionTokens: 20, ServedByRunner: true));
        var invocationsBefore = Fake.Invocations.Count;

        var sse = await SendAsync(taskId, "What is left to do?");

        sse.Should().Contain("Read the export code.").And.Contain("\"done\":true");
        var invocation = Fake.Invocations.Skip(invocationsBefore).Single();
        invocation.UserMessage.Should().Contain("What is left to do?");
        invocation.SystemPrompt.Should().Contain("## The task you are discussing")
            .And.Contain("[BEGIN UNTRUSTED TRACKER-TICKET]")
            .And.Contain("Customer needs it by Friday");
        invocation.WorkspacePath.Should().Be($"fake-task-chat-workspace/{taskId}");

        // The task's own workspace (the repo itself is left out here: nothing clones in tests).
        Fake.WorkspaceRequests.Should().Contain(w => w.TaskId == taskId);

        var chat = await GetChatAsync(taskId);
        chat.Messages.Select(m => m.Role).Should().Equal("user", "assistant");

        using var scope = _factory.Services.CreateScope();
        var usage = await scope.ServiceProvider.GetRequiredService<UsageDbContext>()
            .UsageRecords.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.TaskId == taskId).ToListAsync();
        usage.Should().ContainSingle().Which.CostTotal.Should().Be(0m);
    }

    [Fact]
    public async Task Post_WithTheRunnerOffline_ReturnsAnErrorChunkNamingOrkkaLocalStart_AndSavesNothing()
    {
        var (_, taskId) = await SeedTaskAsync(Lane.Developing, TaskStatus.Active);
        Fake.RouteBlocker = "Your runner is offline — chat runs on your machine with your own AI subscription. Start Orkka with `./orkka start` and try again.";
        try
        {
            var sse = await SendAsync(taskId, "Hello?");

            sse.Should().Contain("\"error\"").And.Contain("./orkka start").And.Contain("\"done\":true");
            (await GetChatAsync(taskId)).Messages.Should().BeEmpty();
        }
        finally
        {
            Fake.RouteBlocker = null;
        }
    }

    [Fact]
    public async Task Apply_InPendingQuestions_AnswersThem_AndTheTaskResumes()
    {
        var (_, taskId) = await SeedTaskAsync(Lane.PendingQuestions, TaskStatus.WaitingAnswer, questionSource: Lane.Planning);
        await AddThreadMessageAsync(taskId, "dev", MessageType.Question, "Which currency?");
        Fake.EnqueueChunks(
            new ChatChunk(ChatChunkType.Text, "Use EUR everywhere."),
            new ChatChunk(ChatChunkType.Done, ""));
        await SendAsync(taskId, "What currency should we use?");
        var reply = (await GetChatAsync(taskId)).Messages.Last(m => m.Role == "assistant");

        var response = await _client.PostAsJsonAsync($"/api/tasks/{taskId}/chat/apply",
            new ApplyTaskChatRequest(MessageId: reply.Id));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = (await response.Content.ReadFromJsonAsync<ApplyTaskChatResponse>(TestJsonOptions.Default))!;
        result.Action.Should().Be("answered");
        result.Lane.Should().Be("Planning");
        result.Status.Should().Be("InQueue");

        var answer = (await ReadThreadAsync(taskId)).Last();
        answer.Role.Should().Be("user");
        answer.MessageType.Should().Be(MessageType.Answer);
        answer.SenderId.Should().Be(TestCurrentUser.TestUserId);
        answer.Content.Should().StartWith("From discussion:").And.Contain("Use EUR everywhere.");
    }

    [Fact]
    public async Task Apply_InPlanReady_RequestsPlanChanges_AndTheTaskRePlans()
    {
        var (_, taskId) = await SeedTaskAsync(Lane.PlanReady, TaskStatus.WaitingPlanApproval);
        await AddPendingPlanAsync(taskId);

        var response = await _client.PostAsJsonAsync($"/api/tasks/{taskId}/chat/apply",
            new ApplyTaskChatRequest(Text: "Stream the CSV instead of buffering it."));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = (await response.Content.ReadFromJsonAsync<ApplyTaskChatResponse>(TestJsonOptions.Default))!;
        result.Action.Should().Be("planChangesRequested");
        result.Lane.Should().Be("Planning");

        var task = await ReadTaskAsync(taskId);
        task.CurrentLane.Should().Be(Lane.Planning);
        task.Status.Should().Be(TaskStatus.InQueue);
        var thread = await ReadThreadAsync(taskId);
        thread.Should().Contain(m => m.Role == "user" && m.Content.StartsWith("From discussion:")
            && m.Content.Contains("Stream the CSV"));
        thread.Should().Contain(m => m.Role == "reviewer" && m.Content.Contains("REJECTED"));
        using var scope = _factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<TasksDbContext>().TaskPlans.IgnoreQueryFilters()
            .AnyAsync(p => p.TaskId == taskId)).Should().BeFalse();
    }

    [Fact]
    public async Task Apply_InAnyOtherLane_PostsAThreadMessage_AndLeavesTheLane()
    {
        var (_, taskId) = await SeedTaskAsync(Lane.Developing, TaskStatus.Active);

        var response = await _client.PostAsJsonAsync($"/api/tasks/{taskId}/chat/apply",
            new ApplyTaskChatRequest(Text: "Remember the totals row."));

        var result = (await response.Content.ReadFromJsonAsync<ApplyTaskChatResponse>(TestJsonOptions.Default))!;
        result.Action.Should().Be("posted");
        result.Lane.Should().Be("Developing");
        (await ReadThreadAsync(taskId)).Last().Content.Should().Be("From discussion:\n\nRemember the totals row.");
    }

    [Fact]
    public async Task Apply_Summarize_PostsTheUtilitySummary_AndRecordsItAtZeroCost()
    {
        var (_, taskId) = await SeedTaskAsync(Lane.Developing, TaskStatus.Active);
        await SendAsync(taskId, "Should totals include tax?");
        _utility.NextText = "- Totals include tax.\n- Still open: rounding.";

        var response = await _client.PostAsJsonAsync($"/api/tasks/{taskId}/chat/apply",
            new ApplyTaskChatRequest(Summarize: true));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = (await response.Content.ReadFromJsonAsync<ApplyTaskChatResponse>(TestJsonOptions.Default))!;
        result.Content.Should().Be("From discussion:\n\n- Totals include tax.\n- Still open: rounding.");
        _utility.LastRequest!.Messages.Single().Content.OfType<AnthropicTextContent>().Single().Text.Should().Contain("Should totals include tax?");
    }

    [Fact]
    public async Task Apply_Summarize_WithTheRunnerOffline_Is503_NamingOrkkaLocalStart()
    {
        var (_, taskId) = await SeedTaskAsync(Lane.Developing, TaskStatus.Active);
        await SendAsync(taskId, "Anything?");
        _utility.Offline = true;
        try
        {
            var response = await _client.PostAsJsonAsync($"/api/tasks/{taskId}/chat/apply",
                new ApplyTaskChatRequest(Summarize: true));

            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            (await response.Content.ReadAsStringAsync()).Should().Contain("./orkka start");
        }
        finally
        {
            _utility.Offline = false;
        }
    }

    [Fact]
    public async Task Apply_RejectsAMessageFromAnotherDiscussion_AndAnEmptyRequest()
    {
        var (_, taskId) = await SeedTaskAsync(Lane.Developing, TaskStatus.Active);

        (await _client.PostAsJsonAsync($"/api/tasks/{taskId}/chat/apply",
            new ApplyTaskChatRequest(MessageId: Ulid.NewUlid().ToString()))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _client.PostAsJsonAsync($"/api/tasks/{taskId}/chat/apply",
            new ApplyTaskChatRequest())).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private sealed class FakeUtility : IUserRunnerUtility
    {
        public string NextText { get; set; } = "- Summary.";
        public bool Offline { get; set; }
        public AnthropicRequest? LastRequest { get; private set; }

        public Task<AnthropicResponse?> TryCompleteAsync(
            Ulid orgId, string? userId, AnthropicRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(Offline ? null : new AnthropicResponse { Text = NextText, InputTokens = 50, OutputTokens = 10 });
        }
    }
}
