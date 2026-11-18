namespace Orkka.IntegrationTests;

using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orkka.IntegrationTests.Fixtures;
using Orkka.IntegrationTests.Helpers;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Domain;
using Orkka.Modules.Agents.Features.AgentChat;
using Orkka.Modules.Agents.Infrastructure;
using Orkka.Modules.Documents.Infrastructure;
using Orkka.Modules.Projects.Features.Projects;

/// <summary>
/// Exercises the tools-enabled chat path end-to-end: the endpoint attaches the per-turn
/// orkka tool server (AgentChat:Tools:Enabled is on suite-wide), a scripted "model" invokes
/// platform tools mid-stream like the CLI would, artifacts persist with the pre-allocated
/// assistant message id, SSE events reach the observer, and the post-stream fenced-block
/// extractors do NOT double-create what the tools already created.
/// </summary>
[Collection("Integration")]
public sealed class AgentChatToolTests(OrkkaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private FakeChatAgentInvoker Fake =>
        (FakeChatAgentInvoker)factory.Services.GetRequiredService<IChatAgentInvoker>();

    private async Task<(Ulid ProjectId, Ulid AgentId, Ulid ConversationId)> SeedChatAsync(
        AgentRole role = AgentRole.SolutionArchitect)
    {
        var projectResp = await _client.PostAsJsonAsync("/api/projects",
            new CreateProjectRequest($"Chat Tools {Ulid.NewUlid()}", "desc"));
        var project = await projectResp.Content.ReadFromJsonAsync<ProjectResponse>(TestJsonOptions.Default);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();

        var agent = new ProjectAgent
        {
            Id = Ulid.NewUlid(),
            OrgId = TestCurrentUser.TestOrgId,
            ProjectId = project!.Id,
            Name = "Toolie",
            Role = role,
            Model = "claude-sonnet-4-6",
        };
        var conversation = new AgentConversation
        {
            Id = Ulid.NewUlid(),
            OrgId = TestCurrentUser.TestOrgId,
            ProjectId = project.Id,
            AgentId = agent.Id,
        };
        db.ProjectAgents.Add(agent);
        db.AgentConversations.Add(conversation);
        await db.SaveChangesAsync();

        return (project.Id, agent.Id, conversation.Id);
    }

    private string MessagesUrl(Ulid projectId, Ulid agentId, Ulid conversationId) =>
        $"/api/projects/{projectId}/agents/{agentId}/conversations/{conversationId}/messages";

    private static async IAsyncEnumerable<ChatChunk> ToolCallingScript(
        IChatToolServer? tools, string toolName, object args,
        Action<ChatToolResult> onResult, string finalText)
    {
        tools.Should().NotBeNull("the endpoint must attach the tool server on an eligible turn");

        var result = await tools!.CallToolAsync(
            toolName, JsonSerializer.SerializeToElement(args));
        onResult(result);

        yield return new ChatChunk(ChatChunkType.Text, finalText);
        yield return new ChatChunk(ChatChunkType.Done, "", PromptTokens: 10, CompletionTokens: 5);
    }

    [Fact]
    public async Task ToolTurn_CreatesDocument_LinksMessage_EmitsSse_AndSkipsBlockFallback()
    {
        var (projectId, agentId, conversationId) = await SeedChatAsync();

        ChatToolResult? toolResult = null;
        // The reply ALSO carries a legacy fenced block for the same document — the
        // double-create guard must skip post-stream extraction because the tool already
        // created one this turn.
        const string sneakyBlock =
            "```document\n{\"documents\":[{\"title\":\"Tool Doc\",\"path\":\"\",\"content\":\"# Dup\"}]}\n```";
        Fake.EnqueueToolScript((tools, _) => ToolCallingScript(
            tools, "create_document",
            new { documents = new[] { new { title = "Tool Doc", path = "decisions", content = "# Via tool" } } },
            r => toolResult = r,
            $"Saved.\n{sneakyBlock}"));

        var response = await _client.PostAsJsonAsync(
            MessagesUrl(projectId, agentId, conversationId), new SendMessageRequest("save this decision"));
        var body = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();
        toolResult.Should().NotBeNull();
        toolResult!.IsError.Should().BeFalse(because: toolResult.Text);

        // Mid-stream SSE event for the created document reached the observer.
        body.Should().Contain("\"document\":{");

        using var scope = factory.Services.CreateScope();
        var documentsDb = scope.ServiceProvider.GetRequiredService<DocumentsDbContext>();
        var documents = await documentsDb.Documents.AsNoTracking()
            .Where(d => d.SourceConversationId == conversationId)
            .ToListAsync();

        // Exactly ONE document despite tool call + fenced block in the same reply.
        documents.Should().ContainSingle();
        documents[0].Title.Should().Be("Tool Doc");
        documents[0].Path.Should().Be("decisions");
        documents[0].Content.Should().Be("# Via tool");

        // The artifact links to the assistant message via the PRE-allocated id — the
        // message row was created after the tool ran, with that same id.
        var agentsDb = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
        var assistantMessage = await agentsDb.AgentMessages.AsNoTracking()
            .FirstAsync(m => m.ConversationId == conversationId && m.Role == "assistant");
        documents[0].SourceMessageId.Should().Be(assistantMessage.Id);

        // The tools-mode prompt advertises the tool and drops the legacy block format.
        var invocation = Fake.Invocations.Last(i => i.ConversationId == conversationId);
        invocation.ToolServer.Should().NotBeNull();
        invocation.SystemPrompt.Should().Contain("mcp__orkka__create_document");
        invocation.SystemPrompt.Should().NotContain("```document");
    }

    [Fact]
    public async Task ToolTurn_ArchitectCreatesDiagram_PersistedWithVersionRow()
    {
        var (projectId, agentId, conversationId) = await SeedChatAsync(AgentRole.SolutionArchitect);

        ChatToolResult? toolResult = null;
        Fake.EnqueueToolScript((tools, _) => ToolCallingScript(
            tools, "create_diagram",
            new
            {
                name = "Payments flow",
                elements = new object[]
                {
                    new { type = "rectangle", id = "api", x = 0, y = 0, width = 200, height = 80 },
                    new { type = "ellipse", id = "db", x = 400, y = 0, width = 180, height = 90 },
                    new { type = "arrow", start = new { id = "api" }, end = new { id = "db" } },
                },
            },
            r => toolResult = r,
            "Diagram is on the board."));

        var response = await _client.PostAsJsonAsync(
            MessagesUrl(projectId, agentId, conversationId), new SendMessageRequest("draw the payments flow"));
        var body = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();
        toolResult.Should().NotBeNull();
        toolResult!.IsError.Should().BeFalse(because: toolResult.Text);
        body.Should().Contain("\"diagram\":{");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
        var diagram = await db.ArchitectureDiagrams.AsNoTracking()
            .SingleAsync(d => d.ConversationId == conversationId);
        diagram.Title.Should().Be("Payments flow");
        // Stored JSON may come back reformatted — assert values, not exact formatting.
        diagram.DiagramData.Should().Contain("orkka-archie-skeleton").And.Contain("rectangle");

        (await db.ArchitectureDiagramVersions.AsNoTracking()
            .CountAsync(v => v.DiagramId == diagram.Id)).Should().Be(1);

        // Agent card credit: a deliverable-shipping turn counts as one completed task.
        (await db.ProjectAgents.AsNoTracking().FirstAsync(a => a.Id == agentId))
            .TasksCompleted.Should().Be(1);
    }

    [Fact]
    public async Task ToolTurn_RoleGating_NonPmCannotCreateTasks()
    {
        var (projectId, agentId, conversationId) = await SeedChatAsync(AgentRole.BackendDev);

        ChatToolResult? toolResult = null;
        Fake.EnqueueToolScript((tools, _) => ToolCallingScript(
            tools, "create_tasks",
            new { tasks = new[] { new { title = "Sneaky task", description = "nope" } } },
            r => toolResult = r,
            "tried it"));

        (await _client.PostAsJsonAsync(
            MessagesUrl(projectId, agentId, conversationId), new SendMessageRequest("make tasks")))
            .EnsureSuccessStatusCode();

        toolResult.Should().NotBeNull();
        toolResult!.IsError.Should().BeTrue();
        toolResult.Text.Should().Contain("Unknown tool");
    }

    [Fact]
    public async Task ToolTurn_ReadTools_ListAndGetDocuments()
    {
        var (projectId, agentId, conversationId) = await SeedChatAsync(AgentRole.QA);

        // Seed a document directly through the Documents module.
        Ulid documentId;
        using (var scope = factory.Services.CreateScope())
        {
            var documentsDb = scope.ServiceProvider.GetRequiredService<DocumentsDbContext>();
            var entity = new Orkka.Modules.Documents.Domain.DocumentEntity
            {
                Id = Ulid.NewUlid(),
                OrgId = TestCurrentUser.TestOrgId,
                ProjectId = projectId,
                Title = "Deploy runbook",
                Path = "runbooks",
                Content = "# Steps\n1. ship it",
            };
            documentsDb.Documents.Add(entity);
            await documentsDb.SaveChangesAsync();
            documentId = entity.Id;
        }

        ChatToolResult? listResult = null;
        ChatToolResult? getResult = null;

        async IAsyncEnumerable<ChatChunk> ReadScript(IChatToolServer? tools)
        {
            tools.Should().NotBeNull();
            listResult = await tools!.CallToolAsync(
                "list_documents", JsonSerializer.SerializeToElement(new { }));
            getResult = await tools.CallToolAsync(
                "get_document", JsonSerializer.SerializeToElement(new { documentId = documentId.ToString() }));
            yield return new ChatChunk(ChatChunkType.Text, "Found the runbook.");
            yield return new ChatChunk(ChatChunkType.Done, "", PromptTokens: 10, CompletionTokens: 5);
        }

        Fake.EnqueueToolScript((tools, _) => ReadScript(tools));

        (await _client.PostAsJsonAsync(
            MessagesUrl(projectId, agentId, conversationId), new SendMessageRequest("what runbooks exist?")))
            .EnsureSuccessStatusCode();

        listResult.Should().NotBeNull();
        listResult!.IsError.Should().BeFalse(because: listResult.Text);
        listResult.Text.Should().Contain("Deploy runbook").And.Contain(documentId.ToString());

        getResult.Should().NotBeNull();
        getResult!.IsError.Should().BeFalse(because: getResult.Text);
        getResult.Text.Should().Contain("# Steps");
    }

    [Fact]
    public async Task ToolTurn_WorkHistoryTools_ListMyTasksAndGetTaskHistory()
    {
        var (projectId, agentId, conversationId) = await SeedChatAsync(AgentRole.BackendDev);

        // Seed a task the chat agent "worked on" (one Developing execution by it, one
        // CodeReview execution by a second agent) directly through the Tasks module.
        Ulid taskId;
        Ulid reviewerId;
        using (var scope = factory.Services.CreateScope())
        {
            var agentsDb = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
            var reviewer = new ProjectAgent
            {
                Id = Ulid.NewUlid(),
                OrgId = TestCurrentUser.TestOrgId,
                ProjectId = projectId,
                Name = "Historian",
                Role = AgentRole.CodeReviewer,
                Model = "claude-sonnet-4-6",
            };
            agentsDb.ProjectAgents.Add(reviewer);
            await agentsDb.SaveChangesAsync();
            reviewerId = reviewer.Id;

            var tasksDb = scope.ServiceProvider
                .GetRequiredService<Orkka.Modules.Tasks.Infrastructure.TasksDbContext>();
            var task = new Orkka.Modules.Tasks.Domain.TaskEntity
            {
                Id = Ulid.NewUlid(),
                OrgId = TestCurrentUser.TestOrgId,
                ProjectId = projectId,
                Number = 1,
                Title = "Wire up the payments webhook",
                CurrentLane = Orkka.Modules.Tasks.Contracts.Lane.Done,
                Status = Orkka.Modules.Tasks.Contracts.TaskStatus.Completed,
            };
            tasksDb.Tasks.Add(task);
            tasksDb.TaskExecutions.Add(new Orkka.Modules.Tasks.Domain.TaskExecution
            {
                Id = Ulid.NewUlid(),
                OrgId = TestCurrentUser.TestOrgId,
                TaskId = task.Id,
                AgentId = agentId,
                Lane = Orkka.Modules.Tasks.Contracts.Lane.Developing,
                Status = Orkka.Modules.Tasks.Contracts.ExecutionStatus.Completed,
                StartedAt = DateTimeOffset.UtcNow.AddHours(-2),
                CompletedAt = DateTimeOffset.UtcNow.AddHours(-1),
                TokensUsed = 4242,
            });
            tasksDb.TaskExecutions.Add(new Orkka.Modules.Tasks.Domain.TaskExecution
            {
                Id = Ulid.NewUlid(),
                OrgId = TestCurrentUser.TestOrgId,
                TaskId = task.Id,
                AgentId = reviewerId,
                Lane = Orkka.Modules.Tasks.Contracts.Lane.CodeReview,
                Status = Orkka.Modules.Tasks.Contracts.ExecutionStatus.Failed,
                ErrorMessage = "review run crashed",
            });
            await tasksDb.SaveChangesAsync();
            taskId = task.Id;
        }

        ChatToolResult? myTasksResult = null;
        ChatToolResult? historyResult = null;

        async IAsyncEnumerable<ChatChunk> HistoryScript(IChatToolServer? tools)
        {
            tools.Should().NotBeNull();
            myTasksResult = await tools!.CallToolAsync(
                "list_my_tasks", JsonSerializer.SerializeToElement(new { }));
            historyResult = await tools.CallToolAsync(
                "get_task_history", JsonSerializer.SerializeToElement(new { taskId = taskId.ToString() }));
            yield return new ChatChunk(ChatChunkType.Text, "Here's what I worked on.");
            yield return new ChatChunk(ChatChunkType.Done, "", PromptTokens: 10, CompletionTokens: 5);
        }

        Fake.EnqueueToolScript((tools, _) => HistoryScript(tools));

        (await _client.PostAsJsonAsync(
            MessagesUrl(projectId, agentId, conversationId), new SendMessageRequest("what did you work on?")))
            .EnsureSuccessStatusCode();

        myTasksResult.Should().NotBeNull();
        myTasksResult!.IsError.Should().BeFalse(because: myTasksResult.Text);
        myTasksResult.Text.Should().Contain("#1").And.Contain("Wire up the payments webhook")
            .And.Contain("1 run(s) by you").And.Contain("Developing").And.Contain(taskId.ToString());

        historyResult.Should().NotBeNull();
        historyResult!.IsError.Should().BeFalse(because: historyResult.Text);
        historyResult.Text.Should().Contain("Toolie (you)").And.Contain("Historian")
            .And.Contain("4242 tokens").And.Contain("error: review run crashed");

        // The tools-mode prompt tells the agent it HAS work history to consult.
        var invocation = Fake.Invocations.Last(i => i.ConversationId == conversationId);
        invocation.SystemPrompt.Should().Contain("mcp__orkka__list_my_tasks");
    }
}
