namespace Orkka.Tests.Features.Agents;

using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Features.AgentChat.Tools;
using Orkka.Modules.Documents.Contracts;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

public sealed class OrkkaChatToolServerTests
{
    private readonly Mock<IDocumentsApi> _documentsApi = new(MockBehavior.Strict);
    private readonly Mock<ITasksApi> _tasksApi = new(MockBehavior.Strict);
    private readonly Mock<IProjectsApi> _projectsApi = new(MockBehavior.Strict);
    private readonly Mock<IAgentsApi> _agentsApi = new(MockBehavior.Strict);
    private readonly List<object> _emitted = [];

    private ChatTurnToolContext MakeContext(AgentRole role) => new()
    {
        OrgId = Ulid.NewUlid(),
        ProjectId = Ulid.NewUlid(),
        AgentId = Ulid.NewUlid(),
        ConversationId = Ulid.NewUlid(),
        AssistantMessageId = Ulid.NewUlid(),
        RequesterUserId = "user_1",
        RequesterLocale = "en",
        AgentRole = role,
        Emit = _emitted.Add,
    };

    private OrkkaChatToolServer MakeServer(ChatTurnToolContext context)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _documentsApi.Object);
        services.AddScoped(_ => _tasksApi.Object);
        services.AddScoped(_ => _projectsApi.Object);
        services.AddScoped(_ => _agentsApi.Object);
        var provider = services.BuildServiceProvider();
        return new OrkkaChatToolServer(
            context, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance);
    }

    private static JsonElement Args(object payload) =>
        JsonSerializer.SerializeToElement(payload);

    // ─────────────────────────────────────────────────────────────────────────
    // Role gating + schemas
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(AgentRole.PM, "create_tasks")]
    [InlineData(AgentRole.SolutionArchitect, "create_diagram")]
    [InlineData(AgentRole.Designer, "create_design_assets")]
    [InlineData(AgentRole.GameDesigner, "create_design_assets")]
    public void ListTools_exposes_the_role_specific_tool_to_its_role_only(AgentRole role, string tool)
    {
        var names = MakeServer(MakeContext(role)).ListTools().Select(t => t.Name).ToList();
        names.Should().Contain(tool);

        var otherNames = MakeServer(MakeContext(AgentRole.BackendDev)).ListTools().Select(t => t.Name);
        otherNames.Should().NotContain(tool);
    }

    [Fact]
    public void ListTools_every_role_gets_document_and_read_tools()
    {
        foreach (var role in Enum.GetValues<AgentRole>())
        {
            var names = MakeServer(MakeContext(role)).ListTools().Select(t => t.Name).ToList();
            names.Should().Contain(
                [
                    "create_document", "list_documents", "get_document", "list_tasks",
                    "get_task", "list_my_tasks", "get_task_history", "list_repos",
                ],
                because: $"role {role} should have the common tool set");
        }
    }

    [Fact]
    public void ListTools_schemas_are_valid_json_objects()
    {
        foreach (var role in new[] { AgentRole.PM, AgentRole.SolutionArchitect, AgentRole.Designer })
        {
            foreach (var tool in MakeServer(MakeContext(role)).ListTools())
            {
                using var doc = JsonDocument.Parse(tool.InputSchemaJson);
                doc.RootElement.ValueKind.Should().Be(JsonValueKind.Object,
                    because: $"schema of {tool.Name} must be a JSON Schema object");
                doc.RootElement.GetProperty("type").GetString().Should().Be("object");
            }
        }
    }

    [Fact]
    public void Design_tool_is_svg_only_and_never_promises_raster_output()
    {
        var tool = MakeServer(MakeContext(AgentRole.Designer))
            .ListTools().Single(t => t.Name == "create_design_assets");

        tool.Description.Should().Contain("no raster image or GIF generation");
        using var schema = JsonDocument.Parse(tool.InputSchemaJson);
        schema.RootElement.GetProperty("properties").GetProperty("assets").GetProperty("items")
            .GetProperty("properties").GetProperty("kind").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()).Should().Equal("svg");
    }

    [Fact]
    public async Task CallTool_unknown_tool_returns_error_result()
    {
        var result = await MakeServer(MakeContext(AgentRole.PM))
            .CallToolAsync("drop_database", Args(new { }));

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("Unknown tool");
    }

    [Fact]
    public async Task CallTool_role_gated_tool_is_unknown_to_other_roles()
    {
        var result = await MakeServer(MakeContext(AgentRole.BackendDev))
            .CallToolAsync("create_tasks", Args(new { tasks = new[] { new { title = "T", description = "D" } } }));

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("Unknown tool");
        _tasksApi.VerifyNoOtherCalls();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // create_document
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateDocument_sanitizes_with_document_rules_and_emits_sse_events()
    {
        var context = MakeContext(AgentRole.BackendDev);
        IReadOnlyList<ChatDocumentSpec>? captured = null;
        _documentsApi
            .Setup(d => d.CreateChatDocumentsAsync(
                context.OrgId, context.ProjectId, context.AgentId, context.ConversationId,
                context.AssistantMessageId, context.RequesterUserId,
                It.IsAny<IReadOnlyList<ChatDocumentSpec>>(), It.IsAny<CancellationToken>()))
            .Callback((Ulid _, Ulid _, Ulid _, Ulid _, Ulid _, string _,
                IReadOnlyList<ChatDocumentSpec> specs, CancellationToken _) => captured = specs)
            .ReturnsAsync([new CreatedChatDocument(Ulid.NewUlid(), "ab")]);

        var result = await MakeServer(context).CallToolAsync("create_document", Args(new
        {
            documents = new[]
            {
                new { title = "a/b", path = "../decisions", content = "# Doc" },
            },
        }));

        result.IsError.Should().BeFalse();
        captured.Should().ContainSingle();
        captured![0].Title.Should().Be("ab", because: "slashes are stripped from titles");
        captured[0].Path.Should().Be("decisions", because: "'..' segments are dropped from paths");
        context.CreatedDocuments.Should().Be(1);
        _emitted.Should().ContainSingle(because: "each created document emits one SSE payload");
    }

    [Fact]
    public async Task CreateDocument_reports_revised_and_unchanged_outcomes()
    {
        var context = MakeContext(AgentRole.BackendDev);
        _documentsApi
            .Setup(d => d.CreateChatDocumentsAsync(
                It.IsAny<Ulid>(), It.IsAny<Ulid>(), It.IsAny<Ulid>(), It.IsAny<Ulid>(), It.IsAny<Ulid>(),
                It.IsAny<string>(), It.IsAny<IReadOnlyList<ChatDocumentSpec>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new CreatedChatDocument(Ulid.NewUlid(), "Fresh"),
                new CreatedChatDocument(Ulid.NewUlid(), "Revised", 3, ChatDocumentOutcome.Updated),
                new CreatedChatDocument(Ulid.NewUlid(), "Same", 2, ChatDocumentOutcome.Unchanged),
            ]);

        var result = await MakeServer(context).CallToolAsync("create_document", Args(new
        {
            documents = new[] { new { title = "x", path = "", content = "# Doc" } },
        }));

        result.IsError.Should().BeFalse();
        result.Text.Should().Contain("Saved 3 document(s)");
        result.Text.Should().Contain("\"Revised\"").And.Contain("now v3");
        result.Text.Should().Contain("\"Same\"").And.Contain("identical content");
    }

    [Fact]
    public async Task CreateDocument_caps_at_five_documents()
    {
        var context = MakeContext(AgentRole.PM);
        IReadOnlyList<ChatDocumentSpec>? captured = null;
        _documentsApi
            .Setup(d => d.CreateChatDocumentsAsync(
                It.IsAny<Ulid>(), It.IsAny<Ulid>(), It.IsAny<Ulid>(), It.IsAny<Ulid>(), It.IsAny<Ulid>(),
                It.IsAny<string>(), It.IsAny<IReadOnlyList<ChatDocumentSpec>>(), It.IsAny<CancellationToken>()))
            .Callback((Ulid _, Ulid _, Ulid _, Ulid _, Ulid _, string _,
                IReadOnlyList<ChatDocumentSpec> specs, CancellationToken _) => captured = specs)
            .ReturnsAsync([]);

        var documents = Enumerable.Range(1, 8)
            .Select(i => new { title = $"Doc {i}", path = "", content = "x" })
            .ToArray();
        await MakeServer(context).CallToolAsync("create_document", Args(new { documents }));

        captured.Should().HaveCount(5);
    }

    [Fact]
    public async Task CreateDocument_without_usable_documents_is_an_error_and_creates_nothing()
    {
        var result = await MakeServer(MakeContext(AgentRole.QA))
            .CallToolAsync("create_document", Args(new
            {
                documents = new[] { new { title = "///", path = "", content = "" } },
            }));

        result.IsError.Should().BeTrue();
        _documentsApi.VerifyNoOtherCalls();
        _emitted.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateDocument_with_malformed_arguments_is_an_error_not_an_exception()
    {
        var result = await MakeServer(MakeContext(AgentRole.QA))
            .CallToolAsync("create_document", Args(new { documents = "not-an-array" }));

        result.IsError.Should().BeTrue();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // create_tasks
    // ─────────────────────────────────────────────────────────────────────────

    private static RepoSummary Repo(Ulid orgId, Ulid projectId, string name) => new(
        Ulid.NewUlid(), projectId, orgId, name, $"https://example.com/{name}.git", "main",
        "blue", WorkspaceStatus.Ready);

    [Fact]
    public async Task CreateTasks_resolves_a_milestone_by_title()
    {
        var context = MakeContext(AgentRole.PM);
        var repo = Repo(context.OrgId, context.ProjectId, "backend");
        _projectsApi
            .Setup(p => p.ListReposAsync(context.ProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([repo]);

        var milestone = new ChatMilestoneRef(Ulid.NewUlid(), "Payments", "Users can pay.", "Planned");
        _tasksApi
            .Setup(t => t.ListMilestonesForChatAsync(
                context.OrgId, context.ProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([milestone]);

        IReadOnlyList<ChatTaskSpec>? captured = null;
        _tasksApi
            .Setup(t => t.CreateChatTasksAsync(
                context.OrgId, context.ProjectId, context.AssistantMessageId,
                context.RequesterUserId, context.RequesterLocale,
                It.IsAny<IReadOnlyList<ChatTaskSpec>>(), It.IsAny<CancellationToken>()))
            .Callback((Ulid _, Ulid _, Ulid _, string? _, string? _,
                IReadOnlyList<ChatTaskSpec> specs, CancellationToken _) => captured = specs)
            .ReturnsAsync([new CreatedChatTask(Ulid.NewUlid(), 3, "Charge a card")]);

        var result = await MakeServer(context).CallToolAsync("create_tasks", Args(new
        {
            tasks = new[]
            {
                new { title = "Charge a card", description = "...", milestone = "payments" },
                new { title = "Unplanned", description = "...", milestone = "No such milestone" },
            },
        }));

        result.IsError.Should().BeFalse();
        captured.Should().HaveCount(2);
        captured![0].MilestoneId.Should().Be(milestone.Id,
            because: "milestone titles match case-insensitively, like repo names");
        captured[1].MilestoneId.Should().BeNull(
            because: "an unknown milestone name falls back to the Backlog rather than guessing");
    }

    [Fact]
    public async Task CreateTasks_does_not_query_milestones_when_no_task_names_one()
    {
        var context = MakeContext(AgentRole.PM);
        var repo = Repo(context.OrgId, context.ProjectId, "backend");
        _projectsApi
            .Setup(p => p.ListReposAsync(context.ProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([repo]);

        _tasksApi
            .Setup(t => t.CreateChatTasksAsync(
                context.OrgId, context.ProjectId, context.AssistantMessageId,
                context.RequesterUserId, context.RequesterLocale,
                It.IsAny<IReadOnlyList<ChatTaskSpec>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new CreatedChatTask(Ulid.NewUlid(), 4, "Backlog work")]);

        // The strict mock has no ListMilestonesForChatAsync setup: most create_tasks calls file
        // into the Backlog and must not pay for a lookup nobody asked for.
        var result = await MakeServer(context).CallToolAsync("create_tasks", Args(new
        {
            tasks = new[] { new { title = "Backlog work", description = "..." } },
        }));

        result.IsError.Should().BeFalse();
        _tasksApi.Verify(t => t.ListMilestonesForChatAsync(
            It.IsAny<Ulid>(), It.IsAny<Ulid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateTasks_resolves_repos_and_drops_unsafe_context_files()
    {
        var context = MakeContext(AgentRole.PM);
        var backend = Repo(context.OrgId, context.ProjectId, "backend");
        var frontend = Repo(context.OrgId, context.ProjectId, "frontend");
        _projectsApi
            .Setup(p => p.ListReposAsync(context.ProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([backend, frontend]);

        IReadOnlyList<ChatTaskSpec>? captured = null;
        _tasksApi
            .Setup(t => t.CreateChatTasksAsync(
                context.OrgId, context.ProjectId, context.AssistantMessageId,
                context.RequesterUserId, context.RequesterLocale,
                It.IsAny<IReadOnlyList<ChatTaskSpec>>(), It.IsAny<CancellationToken>()))
            .Callback((Ulid _, Ulid _, Ulid _, string? _, string? _,
                IReadOnlyList<ChatTaskSpec> specs, CancellationToken _) => captured = specs)
            .ReturnsAsync([new CreatedChatTask(Ulid.NewUlid(), 7, "Do the thing")]);

        var result = await MakeServer(context).CallToolAsync("create_tasks", Args(new
        {
            tasks = new[]
            {
                new
                {
                    title = "Do the thing",
                    description = "## Context\n...",
                    repo = "FRONTEND",
                    contextFiles = new[] { "src/app/page.tsx", "../../.env" },
                },
            },
        }));

        result.IsError.Should().BeFalse();
        captured.Should().ContainSingle();
        captured![0].RepoId.Should().Be(frontend.Id, because: "repo names match case-insensitively");
        captured[0].ContextFiles.Should().ContainSingle()
            .Which.Should().Be("src/app/page.tsx", because: "traversal paths are dropped");
        captured[0].Tags.Should().Contain("pm-generated");
        context.CreatedTasks.Should().Be(1);
        _emitted.Should().ContainSingle();
        result.Text.Should().Contain("#7");
    }

    [Fact]
    public async Task CreateTasks_without_repos_is_an_error_and_creates_nothing()
    {
        var context = MakeContext(AgentRole.PM);
        _projectsApi
            .Setup(p => p.ListReposAsync(context.ProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var result = await MakeServer(context).CallToolAsync("create_tasks", Args(new
        {
            tasks = new[] { new { title = "T", description = "D" } },
        }));

        result.IsError.Should().BeTrue();
        _tasksApi.VerifyNoOtherCalls();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Reads
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetDocument_scopes_to_this_turns_org_and_project()
    {
        var context = MakeContext(AgentRole.BackendDev);
        var documentId = Ulid.NewUlid();
        _documentsApi
            .Setup(d => d.GetDocumentsContentAsync(
                context.OrgId, context.ProjectId,
                It.Is<IReadOnlyCollection<Ulid>>(ids => ids.Single() == documentId),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([new DocumentContent(documentId, "Title", "decisions", "# Body")]);

        var result = await MakeServer(context)
            .CallToolAsync("get_document", Args(new { documentId = documentId.ToString() }));

        result.IsError.Should().BeFalse();
        result.Text.Should().Contain("# Body");
        _documentsApi.VerifyAll();
    }

    [Fact]
    public async Task GetTask_rejects_a_task_from_another_project()
    {
        var context = MakeContext(AgentRole.BackendDev);
        var foreignTaskId = Ulid.NewUlid();
        _tasksApi
            .Setup(t => t.GetTaskSnapshotAsync(foreignTaskId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TaskSnapshot(
                foreignTaskId, Ulid.NewUlid(), Ulid.NewUlid(), null, 1, "Other org's task", null,
                Priority.Medium, TaskType.Feature, Lane.Drafting, TaskStatus.Draft, false, null,
                0, false, null, null, null, null, null, false, false, false, false, 0, 0, null,
                null, null, null, null, null));

        var result = await MakeServer(context)
            .CallToolAsync("get_task", Args(new { taskId = foreignTaskId.ToString() }));

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("No task with that id");
    }

    [Fact]
    public async Task ListMyTasks_renders_the_agents_own_work_scoped_to_this_turn()
    {
        var context = MakeContext(AgentRole.BackendDev);
        _tasksApi
            .Setup(t => t.ListAgentTaskSummariesAsync(
                context.OrgId, context.ProjectId, context.AgentId,
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new AgentTaskSummary(
                    Ulid.NewUlid(), 12, "Fix auth bug", Lane.Done, TaskStatus.Completed,
                    Priority.High, TaskType.BugFix, [Lane.Planning, Lane.Developing], 3,
                    new DateTimeOffset(2026, 7, 15, 10, 0, 0, TimeSpan.Zero)),
                new AgentTaskSummary(
                    Ulid.NewUlid(), 14, "Add caching", Lane.Developing, TaskStatus.Active,
                    Priority.Medium, TaskType.Feature, [], 0, null),
            ]);

        var result = await MakeServer(context).CallToolAsync("list_my_tasks", Args(new { }));

        result.IsError.Should().BeFalse();
        result.Text.Should().Contain("#12").And.Contain("3 run(s) by you")
            .And.Contain("Planning, Developing").And.Contain("last worked 2026-07-15");
        result.Text.Should().Contain("#14").And.Contain("assigned to you, no runs yet");
        _tasksApi.VerifyAll();
    }

    [Fact]
    public async Task ListMyTasks_with_no_work_yet_says_so_without_erroring()
    {
        var context = MakeContext(AgentRole.QA);
        _tasksApi
            .Setup(t => t.ListAgentTaskSummariesAsync(
                context.OrgId, context.ProjectId, context.AgentId,
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var result = await MakeServer(context).CallToolAsync("list_my_tasks", Args(new { }));

        result.IsError.Should().BeFalse();
        result.Text.Should().Contain("not worked on any tasks");
    }

    [Fact]
    public async Task GetTaskHistory_renders_the_timeline_with_agent_names_and_marks_self()
    {
        var context = MakeContext(AgentRole.BackendDev);
        var taskId = Ulid.NewUlid();
        var reviewerId = Ulid.NewUlid();
        _tasksApi
            .Setup(t => t.ListTaskExecutionSummariesAsync(
                context.OrgId, context.ProjectId, taskId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new TaskExecutionSummary(
                    context.AgentId, Lane.Developing, ExecutionStatus.Completed,
                    new DateTimeOffset(2026, 7, 14, 9, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 7, 14, 9, 20, 0, TimeSpan.Zero),
                    12345, null, """["src/a.cs","src/b.cs"]"""),
                new TaskExecutionSummary(
                    reviewerId, Lane.CodeReview, ExecutionStatus.Failed,
                    null, null, 0, "review crashed", null),
            ]);
        _agentsApi
            .Setup(a => a.GetAgentNamesAsync(
                It.Is<IReadOnlyCollection<Ulid>>(ids =>
                    ids.Contains(context.AgentId) && ids.Contains(reviewerId)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Ulid, string>
            {
                [context.AgentId] = "Atlas",
                [reviewerId] = "Sentinel",
            });

        var result = await MakeServer(context)
            .CallToolAsync("get_task_history", Args(new { taskId = taskId.ToString() }));

        result.IsError.Should().BeFalse();
        result.Text.Should().Contain("Atlas (you)").And.Contain("Sentinel");
        result.Text.Should().NotContain("Sentinel (you)");
        result.Text.Should().Contain("Developing").And.Contain("12345 tokens")
            .And.Contain("src/a.cs");
        result.Text.Should().Contain("error: review crashed");
        _tasksApi.VerifyAll();
        _agentsApi.VerifyAll();
    }

    [Fact]
    public async Task GetTaskHistory_unknown_task_is_an_error()
    {
        var context = MakeContext(AgentRole.BackendDev);
        var taskId = Ulid.NewUlid();
        _tasksApi
            .Setup(t => t.ListTaskExecutionSummariesAsync(
                context.OrgId, context.ProjectId, taskId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TaskExecutionSummary>?)null);

        var result = await MakeServer(context)
            .CallToolAsync("get_task_history", Args(new { taskId = taskId.ToString() }));

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("No task with that id");
    }

    [Fact]
    public async Task GetTaskHistory_without_executions_says_so_without_erroring()
    {
        var context = MakeContext(AgentRole.PM);
        var taskId = Ulid.NewUlid();
        _tasksApi
            .Setup(t => t.ListTaskExecutionSummariesAsync(
                context.OrgId, context.ProjectId, taskId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var result = await MakeServer(context)
            .CallToolAsync("get_task_history", Args(new { taskId = taskId.ToString() }));

        result.IsError.Should().BeFalse();
        result.Text.Should().Contain("No agent has run a phase");
        _agentsApi.VerifyNoOtherCalls();
    }
}
