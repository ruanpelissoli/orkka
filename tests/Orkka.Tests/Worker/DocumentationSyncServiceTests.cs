namespace Orkka.Tests.Worker;

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Usage.Contracts;
using Orkka.Modules.Documents.Contracts;
using Orkka.Modules.Execution.Orchestration;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.SharedKernel.Ai;

/// <summary>
/// The post-completion documentation pass. Two behaviours carry the feature's cost story and are
/// asserted hardest: the pass must not call the model AT ALL when the project has not opted in,
/// and an empty triage verdict — the expected answer for most tasks — must stop before the
/// expensive write call. The model is the creator's runner, only.
/// Everything else is about never damaging the task it is observing.
/// </summary>
public sealed class DocumentationSyncServiceTests
{
    private static readonly Ulid OrgId = Ulid.NewUlid();
    private static readonly Ulid ProjectId = Ulid.NewUlid();
    private static readonly Ulid TaskId = Ulid.NewUlid();
    private static readonly Ulid PmAgentId = Ulid.NewUlid();
    private static readonly Ulid ExistingDocId = Ulid.NewUlid();

    private static readonly DocSyncCandidateTask Candidate =
        new(TaskId, OrgId, ProjectId, "Add OAuth login", "user_creator");

    /// <summary>The creator's connected runner: replies in order, one per call, recording every
    /// request it was given — or, with <c>serves: false</c>, a runner that is offline.</summary>
    private sealed class ScriptedRunnerUtility(bool serves, params string[] replies)
        : IUserRunnerUtility
    {
        private int _index;
        public List<AnthropicRequest> Requests { get; } = [];
        public int CallCount => Requests.Count;

        public Task<AnthropicResponse?> TryCompleteAsync(
            Ulid orgId, string? userId, AnthropicRequest request, CancellationToken ct = default)
        {
            if (!serves)
                return Task.FromResult<AnthropicResponse?>(null);

            Requests.Add(request);
            var text = _index < replies.Length ? replies[_index] : "";
            _index++;
            return Task.FromResult<AnthropicResponse?>(new AnthropicResponse
            {
                Text = text,
                InputTokens = 100,
                OutputTokens = 50,
            });
        }
    }

    /// <summary>Mutable holder the CompleteDocSyncAsync callback writes into. A field on the
    /// harness would not work: the callback has to be wired before the harness exists.</summary>
    private sealed class OutcomeBox
    {
        public DocSyncStatus? Value { get; set; }
    }

    private sealed class Harness
    {
        public required ScriptedRunnerUtility Runner { get; init; }
        public required Mock<IUsageRecorder> Usage { get; init; }
        public required Mock<IDocumentsApi> Documents { get; init; }
        public required Mock<ITasksApi> Tasks { get; init; }
        public required IDocumentationSyncService Service { get; init; }
        public required OutcomeBox Outcome { get; init; }

        public DocSyncStatus? RecordedOutcome => Outcome.Value;
    }

    private static Harness BuildHarness(
        bool documentationEnabled = true,
        bool hasPmAgent = true,
        IReadOnlyList<DocumentSummary>? index = null,
        IReadOnlyList<CreatedChatDocument>? syncResult = null,
        bool runnerServes = true,
        params string[] replies)
    {
        var runner = new ScriptedRunnerUtility(runnerServes, replies);

        var projects = new Mock<IProjectsApi>();
        projects.Setup(p => p.GetProjectSummaryAsync(ProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProjectSummary(
                ProjectId, OrgId, "Demo", "demo", "DEMO",
                new ProjectSettingsDto(3, false, DocumentationSyncEnabled: documentationEnabled)));

        var agents = new Mock<IAgentsApi>();
        agents.Setup(a => a.ListAgentsAsync(ProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(hasPmAgent
                ? [new AgentSummary(
                    PmAgentId, OrgId, ProjectId, [], "Luna", AgentRole.PM,
                    "claude-sonnet-5", "", "")]
                : []);

        var documents = new Mock<IDocumentsApi>();
        documents.Setup(d => d.ListDocumentSummariesAsync(
                OrgId, ProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(index ?? []);
        documents.Setup(d => d.GetDocumentsContentAsync(
                OrgId, ProjectId, It.IsAny<IReadOnlyCollection<Ulid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Ulid _, Ulid _, IReadOnlyCollection<Ulid> ids, CancellationToken _) =>
                [.. ids.Select(id => new DocumentContent(id, "Auth flow", "decisions", "# old"))]);
        documents.Setup(d => d.SyncTaskDocumentsAsync(
                OrgId, ProjectId, TaskId, PmAgentId, It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<TaskDocumentSpec>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(syncResult ?? []);

        var tasks = new Mock<ITasksApi>();
        tasks.Setup(t => t.GetTaskSnapshotAsync(TaskId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TaskSnapshot?)null);
        tasks.Setup(t => t.GetLatestApprovedPlanAsync(TaskId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ApprovedPlanInfo?)null);
        tasks.Setup(t => t.GetTaskChangeSummaryAsync(
                TaskId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TaskChangeSummary(["src/Auth.cs"], 1, 10, 2));
        tasks.Setup(t => t.AddTaskMessageAsync(
                TaskId, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MessageType>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TaskMessageSnapshot?)null);

        var outcome = new OutcomeBox();
        tasks.Setup(t => t.CompleteDocSyncAsync(
                TaskId, It.IsAny<DocSyncStatus>(), It.IsAny<CancellationToken>()))
            .Callback((Ulid _, DocSyncStatus status, CancellationToken _) => outcome.Value = status)
            .Returns(Task.CompletedTask);

        var services = new ServiceCollection();
        services.AddSingleton<IProjectsApi>(projects.Object);
        services.AddSingleton<IAgentsApi>(agents.Object);
        services.AddSingleton<IDocumentsApi>(documents.Object);
        services.AddSingleton<ITasksApi>(tasks.Object);
        services.AddSingleton<IUserRunnerUtility>(runner);
        var usage = new Mock<IUsageRecorder>();
        services.AddSingleton(usage.Object);
        var provider = services.BuildServiceProvider();

        return new Harness
        {
            Runner = runner,
            Usage = usage,
            Documents = documents,
            Tasks = tasks,
            Outcome = outcome,
            Service = new DocumentationSyncService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                // FastUtilityModel is derived: with no DefaultModel override it is the fallback.
                Options.Create(new AnthropicOptions()),
                Options.Create(new DocumentationSyncOptions()),
                NullLogger<DocumentationSyncService>.Instance),
        };
    }

    [Fact]
    public async Task Nothing_runs_when_the_project_has_not_opted_in()
    {
        var harness = BuildHarness(documentationEnabled: false);

        await harness.Service.RunAsync(Candidate);

        harness.Runner.CallCount.Should().Be(0, "an opted-out project must cost nothing");
        harness.RecordedOutcome.Should().Be(DocSyncStatus.Skipped);
    }

    [Fact]
    public async Task Nothing_runs_when_the_project_has_no_pm_agent()
    {
        var harness = BuildHarness(hasPmAgent: false);

        await harness.Service.RunAsync(Candidate);

        harness.Runner.CallCount.Should().Be(0);
        harness.RecordedOutcome.Should().Be(DocSyncStatus.Skipped);
    }

    [Fact]
    public async Task An_empty_triage_verdict_stops_before_the_write_call()
    {
        // The common case, and the one that decides whether the feature is affordable.
        var harness = BuildHarness(replies: ["""{"create": null, "updateIds": []}"""]);

        await harness.Service.RunAsync(Candidate);

        harness.Runner.CallCount.Should().Be(1, "triage alone answers most tasks");
        harness.RecordedOutcome.Should().Be(DocSyncStatus.Skipped);
        harness.Documents.Verify(d => d.SyncTaskDocumentsAsync(
            It.IsAny<Ulid>(), It.IsAny<Ulid>(), It.IsAny<Ulid>(), It.IsAny<Ulid>(),
            It.IsAny<string?>(), It.IsAny<IReadOnlyList<TaskDocumentSpec>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Triage_uses_the_fast_model_and_the_write_uses_the_agents_model()
    {
        var harness = BuildHarness(
            syncResult: [new CreatedChatDocument(Ulid.NewUlid(), "Auth flow")],
            replies:
            [
                """{"create": {"title": "Auth flow", "path": "decisions", "reason": "new subsystem"}, "updateIds": []}""",
                """{"documents": [{"title": "Auth flow", "path": "decisions", "content": "# How auth works"}]}""",
            ]);

        await harness.Service.RunAsync(Candidate);

        harness.Runner.CallCount.Should().Be(2);
        harness.Runner.Requests[0].Model.Should().Be(AnthropicOptions.FallbackFastUtilityModel);
        harness.Runner.Requests[1].Model.Should().Be("claude-sonnet-5");
        harness.RecordedOutcome.Should().Be(DocSyncStatus.Completed);
    }

    [Fact]
    public async Task Triage_is_never_shown_a_file_document()
    {
        // A PDF's Content column is empty by design, so it can be neither compared nor rewritten.
        var harness = BuildHarness(
            index:
            [
                new DocumentSummary(ExistingDocId, "Auth flow", "decisions", 1, DateTimeOffset.UtcNow),
                new DocumentSummary(Ulid.NewUlid(), "Contract", "", 1, DateTimeOffset.UtcNow, IsFile: true),
            ],
            replies: ["""{"create": null, "updateIds": []}"""]);

        await harness.Service.RunAsync(Candidate);

        var prompt = harness.Runner.Requests[0].Messages[0].Content
            .OfType<AnthropicTextContent>().Single().Text;
        prompt.Should().Contain("Auth flow");
        prompt.Should().NotContain("Contract");
    }

    [Fact]
    public async Task A_write_targeting_a_document_that_was_not_offered_is_dropped()
    {
        var harness = BuildHarness(replies:
        [
            """{"create": {"title": "Auth flow", "path": "", "reason": "new"}, "updateIds": []}""",
            // No document was offered for revision, so this id cannot be honoured.
            $$"""{"documents": [{"id": "{{Ulid.NewUlid()}}", "title": "Hijacked", "path": "", "content": "# x"}]}""",
        ]);

        await harness.Service.RunAsync(Candidate);

        harness.Documents.Verify(d => d.SyncTaskDocumentsAsync(
            It.IsAny<Ulid>(), It.IsAny<Ulid>(), It.IsAny<Ulid>(), It.IsAny<Ulid>(),
            It.IsAny<string?>(), It.IsAny<IReadOnlyList<TaskDocumentSpec>>(),
            It.IsAny<CancellationToken>()), Times.Never);
        harness.RecordedOutcome.Should().Be(DocSyncStatus.Skipped);
    }

    [Fact]
    public async Task A_fenced_json_reply_is_still_parsed()
    {
        var harness = BuildHarness(replies:
        [
            "```json\n{\"create\": null, \"updateIds\": []}\n```",
        ]);

        await harness.Service.RunAsync(Candidate);

        harness.RecordedOutcome.Should().Be(DocSyncStatus.Skipped);
    }

    [Fact]
    public async Task Unparseable_triage_output_skips_rather_than_failing_the_task()
    {
        var harness = BuildHarness(replies: ["I could not decide."]);

        await harness.Service.RunAsync(Candidate);

        harness.Runner.CallCount.Should().Be(1);
        harness.RecordedOutcome.Should().Be(DocSyncStatus.Skipped);
    }

    [Fact]
    public async Task A_model_failure_is_recorded_as_failed_and_leaves_the_task_untouched()
    {
        var harness = BuildHarness(replies: ["""{"create": null, "updateIds": []}"""]);
        harness.Tasks.Setup(t => t.GetTaskChangeSummaryAsync(
                TaskId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        await harness.Service.RunAsync(Candidate);

        harness.RecordedOutcome.Should().Be(DocSyncStatus.Failed);
        // Nothing on the task's own pipeline state is ever written by this pass.
        harness.Tasks.Verify(t => t.AddTaskMessageAsync(
            It.IsAny<Ulid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MessageType>(),
            It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task An_all_unchanged_sync_is_skipped_not_announced()
    {
        var harness = BuildHarness(
            syncResult:
            [
                new CreatedChatDocument(
                    ExistingDocId, "Auth flow", 1, ChatDocumentOutcome.Unchanged),
            ],
            index: [new DocumentSummary(ExistingDocId, "Auth flow", "decisions", 1, DateTimeOffset.UtcNow)],
            replies:
            [
                $$"""{"create": null, "updateIds": ["{{ExistingDocId}}"]}""",
                $$"""{"documents": [{"id": "{{ExistingDocId}}", "title": "Auth flow", "path": "decisions", "content": "# old"}]}""",
            ]);

        await harness.Service.RunAsync(Candidate);

        harness.RecordedOutcome.Should().Be(DocSyncStatus.Skipped);
        harness.Tasks.Verify(t => t.AddTaskMessageAsync(
            It.IsAny<Ulid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MessageType>(),
            It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_completed_pass_announces_what_it_wrote_on_the_task()
    {
        var harness = BuildHarness(
            syncResult: [new CreatedChatDocument(Ulid.NewUlid(), "Auth flow")],
            replies:
            [
                """{"create": {"title": "Auth flow", "path": "decisions", "reason": "new subsystem"}, "updateIds": []}""",
                """{"documents": [{"title": "Auth flow", "path": "decisions", "content": "# How auth works"}]}""",
            ]);

        await harness.Service.RunAsync(Candidate);

        harness.RecordedOutcome.Should().Be(DocSyncStatus.Completed);
        harness.Tasks.Verify(t => t.AddTaskMessageAsync(
            TaskId, "system",
            It.Is<string>(m => m.Contains("Luna") && m.Contains("Auth flow")),
            MessageType.SystemEvent, It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task A_runner_served_pass_never_touches_the_billed_client_and_records_zero()
    {
        var harness = BuildHarness(
            syncResult: [new CreatedChatDocument(Ulid.NewUlid(), "Auth flow")],
            replies:
            [
                """{"create": {"title": "Auth flow", "path": "decisions", "reason": "new subsystem"}, "updateIds": []}""",
                """{"documents": [{"title": "Auth flow", "path": "decisions", "content": "# How auth works"}]}""",
            ]);

        await harness.Service.RunAsync(Candidate);

        // Both passes (triage and write) went to the creator's runner.
        harness.Runner.CallCount.Should().Be(2);

        // Tokens are still attributed — only the spend is zero, which is what FlatCostBase: 0
        // means downstream in UsageRecorder.
        harness.Usage.Verify(u => u.RecordUsageAsync(
            It.Is<RecordUsageCommand>(c =>
                c.FlatCostBase == 0m
                && c.InputTokens == 100
                && c.UserId == "user_creator"),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task A_runner_miss_is_retried_later()
    {
        var harness = BuildHarness(
            runnerServes: false,
            replies: ["""{"create": null, "updateIds": []}"""]);

        await harness.Service.RunAsync(Candidate);

        // Failed, not Skipped: the sweep retries a failed pass (under its cap) once the runner
        // is back, instead of the task's documentation being silently written off.
        harness.RecordedOutcome.Should().Be(DocSyncStatus.Failed);
        harness.Usage.Verify(u => u.RecordUsageAsync(
            It.IsAny<RecordUsageCommand>(), It.IsAny<CancellationToken>()), Times.Never);
        harness.Tasks.Verify(t => t.AddTaskMessageAsync(
            It.IsAny<Ulid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MessageType>(),
            It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
