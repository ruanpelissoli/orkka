namespace Orkka.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orkka.IntegrationTests.Fixtures;
using Orkka.IntegrationTests.Helpers;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Domain;
using Orkka.Modules.Agents.Features.AgentChat;
using Orkka.Modules.Agents.Features.Agents;
using Orkka.Modules.Agents.Features.Schedules;
using Orkka.Modules.Agents.Features.Skills;
using Orkka.Modules.Agents.Infrastructure;
using Orkka.Modules.Projects.Features.ProjectRepos;
using Orkka.Modules.Projects.Features.Projects;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Features.Autopilot;
using Orkka.Modules.Tasks.Infrastructure;

/// <summary>
/// Schedules: validation, the runner firing a playbook through the (fake) chat invoker,
/// autopilot windows and agent self-reminders.
/// </summary>
[Collection("Integration")]
public sealed class ScheduleTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private FakeChatAgentInvoker Fake =>
        (FakeChatAgentInvoker)factory.Services.GetRequiredService<IChatAgentInvoker>();

    private async Task<(Ulid ProjectId, string Slug)> CreateProjectAsync(string name, bool withRepo = false)
    {
        var resp = await _client.PostAsJsonAsync("/api/projects", new CreateProjectRequest(name, "desc"));
        var project = await resp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);
        if (withRepo)
            await _client.PostAsJsonAsync($"/api/projects/{project!.Id}/repos",
                new AddProjectRepoRequest("trig-repo", $"https://github.com/acme/{project.Slug}.git", "main", null, null));
        return (project!.Id, project.Slug);
    }

    private async Task<List<AgentResponse>> WaitForAgentsAsync(Ulid projectId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var agents = await _client.GetFromJsonAsync<List<AgentResponse>>($"/api/projects/{projectId}/agents", TestJsonOptions.Default);
            if (agents is { Count: > 0 }) return agents;
            await Task.Delay(100);
        }
        throw new TimeoutException("agents were not seeded");
    }

    private async Task<List<SkillResponse>> WaitForSkillsAsync(Ulid projectId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var skills = await _client.GetFromJsonAsync<List<SkillResponse>>($"/api/projects/{projectId}/skills", TestJsonOptions.Default);
            if (skills is { Count: >= 4 }) return skills;
            await Task.Delay(100);
        }
        throw new TimeoutException("skills were not seeded");
    }

    [Fact]
    public async Task Create_ValidatesTiming_Kind_AndPlaybookShape()
    {
        var (projectId, _) = await CreateProjectAsync("Sched Validate");
        var agents = await WaitForAgentsAsync(projectId);
        var pm = agents.Single(a => a.Role == nameof(AgentRole.PM));

        // Both cron and fireAt → reject.
        var both = await _client.PostAsJsonAsync($"/api/projects/{projectId}/schedules",
            new UpsertScheduleRequest("x", "Playbook", CronExpression: "0 9 * * *", FireAt: DateTimeOffset.UtcNow.AddHours(1), AgentId: pm.Id.ToString(), Message: "hi"));
        both.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Too frequent playbook.
        var often = await _client.PostAsJsonAsync($"/api/projects/{projectId}/schedules",
            new UpsertScheduleRequest("x", "Playbook", CronExpression: "*/5 * * * *", AgentId: pm.Id.ToString(), Message: "hi"));
        often.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Playbook without skill or message.
        var empty = await _client.PostAsJsonAsync($"/api/projects/{projectId}/schedules",
            new UpsertScheduleRequest("x", "Playbook", CronExpression: "0 9 * * 1", AgentId: pm.Id.ToString()));
        empty.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Reminders can't be created through the API.
        var reminder = await _client.PostAsJsonAsync($"/api/projects/{projectId}/schedules",
            new UpsertScheduleRequest("x", "Reminder", FireAt: DateTimeOffset.UtcNow.AddHours(1)));
        reminder.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Autopilot start with neither cap nor explicit uncapped.
        var noCap = await _client.PostAsJsonAsync($"/api/projects/{projectId}/schedules",
            new UpsertScheduleRequest("x", "AutopilotStart", CronExpression: "0 22 * * *"));
        noCap.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Valid weekly playbook: NextRunAt computed in the given zone.
        var ok = await _client.PostAsJsonAsync($"/api/projects/{projectId}/schedules",
            new UpsertScheduleRequest("Groom", "Playbook", CronExpression: "0 9 * * 1", TimeZoneId: "America/Sao_Paulo",
                AgentId: pm.Id.ToString(), Message: "Groom the backlog"));
        ok.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await ok.Content.ReadFromJsonAsync<ScheduleResponse>(TestJsonOptions.Default);
        created!.NextRunAt.Should().NotBeNull();
        created.NextRunAt!.Value.Should().BeAfter(DateTimeOffset.UtcNow);
        created.Kind.Should().Be("Playbook");
        created.IsEnabled.Should().BeTrue();

        // Toggle off clears NextRunAt; on recomputes it.
        var off = await _client.PatchAsJsonAsync($"/api/projects/{projectId}/schedules/{created.Id}/enabled", new ToggleScheduleRequest(false));
        (await off.Content.ReadFromJsonAsync<ScheduleResponse>(TestJsonOptions.Default))!.NextRunAt.Should().BeNull();
        var on = await _client.PatchAsJsonAsync($"/api/projects/{projectId}/schedules/{created.Id}/enabled", new ToggleScheduleRequest(true));
        (await on.Content.ReadFromJsonAsync<ScheduleResponse>(TestJsonOptions.Default))!.NextRunAt.Should().NotBeNull();
    }

    [Fact]
    public async Task RunNow_Playbook_RunsATurnWithTheSkill_AndRecordsTheRun()
    {
        var (projectId, _) = await CreateProjectAsync("Sched Playbook");
        var agents = await WaitForAgentsAsync(projectId);
        var skills = await WaitForSkillsAsync(projectId);
        var pm = agents.Single(a => a.Role == nameof(AgentRole.PM));
        var groom = skills.Single(s => s.Slug == "groom-backlog");

        var create = await _client.PostAsJsonAsync($"/api/projects/{projectId}/schedules",
            new UpsertScheduleRequest("Weekly grooming", "Playbook", CronExpression: "0 9 * * 1",
                AgentId: pm.Id.ToString(), SkillId: groom.Id.ToString(), Message: "Focus on stale drafts."));
        var schedule = await create.Content.ReadFromJsonAsync<ScheduleResponse>(TestJsonOptions.Default);

        var before = Fake.Invocations.Count;
        Fake.EnqueueChunks(
            new ChatChunk(ChatChunkType.Text, "Backlog groomed: 3 tasks tidied."),
            new ChatChunk(ChatChunkType.Done, "", PromptTokens: 10, CompletionTokens: 5));

        var runResp = await _client.PostAsync($"/api/projects/{projectId}/schedules/{schedule!.Id}/run", null);
        runResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var run = await runResp.Content.ReadFromJsonAsync<ScheduleRunResponse>(TestJsonOptions.Default);

        run!.Status.Should().Be("Succeeded");
        run.Trigger.Should().Be("manual");
        run.Summary.Should().Contain("Backlog groomed");
        run.ConversationId.Should().NotBeNull();

        Fake.Invocations.Count.Should().Be(before + 1);
        var invocation = Fake.Invocations.Last();
        invocation.UserMessage.Should().Contain("<skill name=\"groom-backlog\">");
        invocation.UserMessage.Should().Contain("Focus on stale drafts.");

        // The conversation reads as a real exchange, titled after the schedule.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
            var conversation = await db.AgentConversations.IgnoreQueryFilters().FirstAsync(c => c.Id == run.ConversationId);
            conversation.Title.Should().Contain("Weekly grooming");
            var messages = await db.AgentMessages.IgnoreQueryFilters().Where(m => m.ConversationId == conversation.Id).OrderBy(m => m.CreatedAt).ToListAsync();
            messages.Select(m => m.Role).Should().Equal("user", "assistant");
            messages[0].Content.Should().StartWith("/groom-backlog");
        }

        var runs = await _client.GetFromJsonAsync<List<ScheduleRunResponse>>($"/api/projects/{projectId}/schedules/{schedule.Id}/runs", TestJsonOptions.Default);
        runs.Should().ContainSingle(r => r.Id == run.Id);

        var refreshed = await _client.GetFromJsonAsync<ScheduleResponse>($"/api/projects/{projectId}/schedules/{schedule.Id}", TestJsonOptions.Default);
        refreshed!.RunCount.Should().Be(1);
        refreshed.LastRunStatus.Should().Be("Succeeded");
        refreshed.ConversationId.Should().Be(run.ConversationId);
        refreshed.NextRunAt.Should().NotBeNull("recurring schedules advance after a run");
    }

    [Fact]
    public async Task RunNow_AutopilotWindow_StartsAndStops()
    {
        var (projectId, _) = await CreateProjectAsync("Sched Autopilot", withRepo: true);
        await WaitForAgentsAsync(projectId);

        var start = await _client.PostAsJsonAsync($"/api/projects/{projectId}/schedules",
            new UpsertScheduleRequest("Night shift on", "AutopilotStart", CronExpression: "0 22 * * *", AutopilotCapUsd: 15m));
        start.StatusCode.Should().Be(HttpStatusCode.Created);
        var startSchedule = await start.Content.ReadFromJsonAsync<ScheduleResponse>(TestJsonOptions.Default);
        var stop = await _client.PostAsJsonAsync($"/api/projects/{projectId}/schedules",
            new UpsertScheduleRequest("Night shift off", "AutopilotStop", CronExpression: "0 6 * * *"));
        var stopSchedule = await stop.Content.ReadFromJsonAsync<ScheduleResponse>(TestJsonOptions.Default);

        var r1 = await (await _client.PostAsync($"/api/projects/{projectId}/schedules/{startSchedule!.Id}/run", null))
            .Content.ReadFromJsonAsync<ScheduleRunResponse>(TestJsonOptions.Default);
        r1!.Status.Should().Be("Succeeded");
        var status = await _client.GetFromJsonAsync<AutopilotStatus>($"/api/projects/{projectId}/autopilot", TestJsonOptions.Default);
        status!.Running.Should().BeTrue();
        status.CapUsd.Should().Be(15m);

        // Second start while running is a skip, not a failure.
        var r1b = await (await _client.PostAsync($"/api/projects/{projectId}/schedules/{startSchedule.Id}/run", null))
            .Content.ReadFromJsonAsync<ScheduleRunResponse>(TestJsonOptions.Default);
        r1b!.Status.Should().Be("Skipped");

        var r2 = await (await _client.PostAsync($"/api/projects/{projectId}/schedules/{stopSchedule!.Id}/run", null))
            .Content.ReadFromJsonAsync<ScheduleRunResponse>(TestJsonOptions.Default);
        r2!.Status.Should().Be("Succeeded");
        status = await _client.GetFromJsonAsync<AutopilotStatus>($"/api/projects/{projectId}/autopilot", TestJsonOptions.Default);
        status!.Running.Should().BeFalse();
    }

    [Fact]
    public async Task Processor_ClaimsDueSchedule_AndRunsIt()
    {
        var (projectId, _) = await CreateProjectAsync("Sched Sweep");
        var agents = await WaitForAgentsAsync(projectId);
        var pm = agents.Single(a => a.Role == nameof(AgentRole.PM));

        var create = await _client.PostAsJsonAsync($"/api/projects/{projectId}/schedules",
            new UpsertScheduleRequest("Due now", "Playbook", CronExpression: "0 9 * * *", AgentId: pm.Id.ToString(), Message: "ping"));
        var schedule = await create.Content.ReadFromJsonAsync<ScheduleResponse>(TestJsonOptions.Default);

        // Backdate NextRunAt so the (running) processor picks it up on its next tick.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
            await db.AgentSchedules.IgnoreQueryFilters().Where(s => s.Id == schedule!.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.NextRunAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }
        Fake.EnqueueChunks(
            new ChatChunk(ChatChunkType.Text, "pong"),
            new ChatChunk(ChatChunkType.Done, "", PromptTokens: 1, CompletionTokens: 1));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        ScheduleResponse? refreshed = null;
        while (DateTime.UtcNow < deadline)
        {
            refreshed = await _client.GetFromJsonAsync<ScheduleResponse>($"/api/projects/{projectId}/schedules/{schedule!.Id}", TestJsonOptions.Default);
            if (refreshed!.RunCount > 0 && refreshed.LastRunStatus != "Running") break;
            await Task.Delay(500);
        }

        refreshed!.RunCount.Should().Be(1, "the sweep must have claimed and run the due schedule");
        refreshed.LastRunStatus.Should().Be("Succeeded");
        refreshed.NextRunAt.Should().BeAfter(DateTimeOffset.UtcNow);
        var runs = await _client.GetFromJsonAsync<List<ScheduleRunResponse>>($"/api/projects/{projectId}/schedules/{schedule!.Id}/runs", TestJsonOptions.Default);
        runs.Should().ContainSingle(r => r.Trigger == "cron");
    }

    [Fact]
    public async Task ReminderTool_CreatesOneShot_ListedOnAutomationPage()
    {
        var (projectId, _) = await CreateProjectAsync("Sched Reminder");
        var agents = await WaitForAgentsAsync(projectId);
        var pm = agents.Single(a => a.Role == nameof(AgentRole.PM));

        Ulid conversationId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
            var conversation = new AgentConversation { Id = Ulid.NewUlid(), OrgId = TestCurrentUser.TestOrgId, ProjectId = projectId, AgentId = pm.Id };
            db.AgentConversations.Add(conversation);
            await db.SaveChangesAsync();
            conversationId = conversation.Id;
        }

        ChatToolResult? result = null;
        Fake.EnqueueToolScript((tools, _) => ToolCallingScript(tools, "set_reminder",
            new { message = "Check the deploy status", delay_seconds = 3600 }, r => result = r, "I'll check back in an hour."));

        var resp = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/agents/{pm.Id}/conversations/{conversationId}/messages",
            new SendMessageRequest("remind yourself in an hour to check the deploy"));
        resp.EnsureSuccessStatusCode();
        await resp.Content.ReadAsStringAsync();

        result.Should().NotBeNull();
        result!.IsError.Should().BeFalse(result.Text);
        result.Text.Should().Contain("Reminder set");

        var schedules = await _client.GetFromJsonAsync<List<ScheduleResponse>>($"/api/projects/{projectId}/schedules", TestJsonOptions.Default);
        var reminder = schedules!.Single(s => s.Kind == "Reminder");
        reminder.AgentId.Should().Be(pm.Id);
        reminder.ConversationId.Should().Be(conversationId);
        reminder.Message.Should().Be("Check the deploy status");
        reminder.NextRunAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddHours(1), TimeSpan.FromMinutes(2));

        // Reminders can't be edited via the API, only deleted.
        var edit = await _client.PutAsJsonAsync($"/api/projects/{projectId}/schedules/{reminder.Id}",
            new UpsertScheduleRequest("x", "Playbook", CronExpression: "0 9 * * *", AgentId: pm.Id.ToString(), Message: "m"));
        edit.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var del = await _client.DeleteAsync($"/api/projects/{projectId}/schedules/{reminder.Id}");
        del.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    private static async IAsyncEnumerable<ChatChunk> ToolCallingScript(
        IChatToolServer? tools, string toolName, object args, Action<ChatToolResult> onResult, string finalText)
    {
        tools.Should().NotBeNull("the endpoint must attach the tool server on an eligible turn");
        var result = await tools!.CallToolAsync(toolName, JsonSerializer.SerializeToElement(args));
        onResult(result);
        yield return new ChatChunk(ChatChunkType.Text, finalText);
        yield return new ChatChunk(ChatChunkType.Done, "", PromptTokens: 10, CompletionTokens: 5);
    }
}
