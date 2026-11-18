namespace Orkka.Modules.Agents.Features.Mcp;

using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Domain;
using Orkka.Modules.Agents.Features.AgentChat;
using Orkka.Modules.Agents.Features.Skills;
using Orkka.Modules.Agents.Infrastructure;
using Orkka.Modules.Documents.Contracts;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.SharedKernel.Security;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>Who is calling the MCP endpoint: an org member (via API key or a signed-in
/// session) with a permission set and, for project-bound keys, a fixed project.</summary>
public sealed record McpCaller(
    Ulid OrgId,
    Ulid MemberId,
    string UserId,
    string? Locale,
    bool IsOwner,
    IReadOnlySet<Permission> Permissions,
    Ulid? BoundProjectId,
    /// <summary>The API key id when the call is authenticated by one (null for a session).</summary>
    string? ApiKeyId = null)
{
    public bool Has(Permission p) => IsOwner || Permissions.Contains(p);
}

/// <summary>
/// The Orkka MCP tool surface ("manage Orkka from Claude Code / Cursor / your scripts").
/// A sibling of the chat tool server (same facades, different packaging — see
/// TaskPipelineToolServer for the precedent): read tools plus the board operations, each gated
/// by the caller's permission. <see cref="ListTools"/> is the authorization: a tool the
/// caller can't use isn't listed and can't be called.
///
/// Project selection: every project-scoped tool takes <c>project</c> (slug or id). A
/// project-bound API key ignores the argument and always uses its project.
/// </summary>
public sealed class OrkkaMcpToolServer(
    McpCaller caller,
    IServiceScopeFactory scopeFactory,
    ILogger<OrkkaMcpToolServer> logger,
    IAgentAuditLog? audit = null) : IChatToolServer
{
    public const string Name = "orkka";
    public string ServerName => Name;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private const int TextCap = 12_000;

    private sealed record ToolSpec(ChatToolDefinition Definition, Permission? Requires);

    // Reads are open to every member (matches the REST surface); writes carry the same
    // Permission the equivalent endpoint checks. Lazy: the definitions are declared further
    // down and static fields initialize in textual order.
    private static IReadOnlyList<ToolSpec>? _tools;
    private static IReadOnlyList<ToolSpec> Tools => _tools ??=
    [
        new(ListProjectsTool, null),
        new(GetProjectTool, null),
        new(ListTasksTool, null),
        new(GetTaskTool, null),
        new(GetTaskHistoryTool, null),
        new(ListDocumentsTool, null),
        new(GetDocumentTool, null),
        new(ListMilestonesTool, null),
        new(ListReposTool, null),
        new(ListAgentsTool, null),
        new(ListSkillsTool, null),
        new(ListSchedulesTool, null),
        new(GetInboxTool, null),
        new(GetAutopilotStatusTool, null),
        new(CreateTasksTool, Permission.TasksCreate),
        new(SubmitTaskTool, Permission.TasksExecute),
        new(AnswerTaskTool, Permission.TasksExecute),
        new(ApprovePlanTool, Permission.PlansReview),
        new(RejectPlanTool, Permission.PlansReview),
        new(ReviewPullRequestTool, Permission.CodeReviewReview),
        new(MergePullRequestTool, Permission.CodeReviewMerge),
        new(StartAutopilotTool, Permission.TasksExecute),
        new(StopAutopilotTool, Permission.TasksExecute),
        new(ChatWithAgentTool, Permission.AgentsChat),
    ];

    public IReadOnlyList<ChatToolDefinition> ListTools() =>
        Tools.Where(t => t.Requires is null || caller.Has(t.Requires.Value)).Select(t => t.Definition).ToList();

    public async Task<ChatToolResult> CallToolAsync(string toolName, JsonElement arguments, CancellationToken ct = default)
    {
        var spec = Tools.FirstOrDefault(t => t.Definition.Name == toolName);
        if (spec is null)
            return Error($"Unknown tool \"{toolName}\".");
        if (spec.Requires is { } required && !caller.Has(required))
            return Error($"Your API key lacks the {required} permission required by {toolName}.");

        // Every write through MCP lands in the trail — the equivalent UI action would.
        // Reads aren't recorded (they'd swamp the log; the REST reads aren't audited either).
        // Arguments are what the caller sent, so the writer's redaction/cap applies to them.
        var result = await InvokeAsync(toolName, arguments, ct);
        if (spec.Requires is not null && audit is not null)
            await audit.RecordAsync(new AgentAuditRecord(
                caller.OrgId, caller.BoundProjectId, AgentAuditEventType.McpToolCall,
                $"MCP {toolName} by {caller.UserId}{(result.IsError ? " (failed)" : "")}",
                Actor: caller.UserId,
                Tool: $"mcp:{toolName}",
                ActorKind: caller.ApiKeyId is null ? AuditActorKind.User : AuditActorKind.ApiKey,
                ActorUserId: caller.UserId,
                Metadata: new { tool = toolName, arguments, ok = !result.IsError, apiKeyId = caller.ApiKeyId }), ct);
        return result;
    }

    private async Task<ChatToolResult> InvokeAsync(string toolName, JsonElement arguments, CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var services = scope.ServiceProvider;
            return toolName switch
            {
                "list_projects" => await ListProjectsAsync(services, ct),
                "get_project" => await GetProjectAsync(services, arguments, ct),
                "list_tasks" => await ListTasksAsync(services, arguments, ct),
                "get_task" => await GetTaskAsync(services, arguments, ct),
                "get_task_history" => await GetTaskHistoryAsync(services, arguments, ct),
                "list_documents" => await ListDocumentsAsync(services, arguments, ct),
                "get_document" => await GetDocumentAsync(services, arguments, ct),
                "list_milestones" => await ListMilestonesAsync(services, arguments, ct),
                "list_repos" => await ListReposAsync(services, arguments, ct),
                "list_agents" => await ListAgentsAsync(services, arguments, ct),
                "list_skills" => await ListSkillsAsync(services, arguments, ct),
                "list_schedules" => await ListSchedulesAsync(services, arguments, ct),
                "get_inbox" => await GetInboxAsync(services, arguments, ct),
                "get_autopilot_status" => await GetAutopilotStatusAsync(services, arguments, ct),
                "create_tasks" => await CreateTasksAsync(services, arguments, ct),
                "submit_task" => await SubmitTaskAsync(services, arguments, ct),
                "answer_task" => await AnswerTaskAsync(services, arguments, ct),
                "approve_plan" => await ReviewPlanAsync(services, arguments, approve: true, ct),
                "reject_plan" => await ReviewPlanAsync(services, arguments, approve: false, ct),
                "review_pull_request" => await ReviewPullRequestAsync(services, arguments, ct),
                "merge_pull_request" => await MergePullRequestAsync(services, arguments, ct),
                "start_autopilot" => await StartAutopilotAsync(services, arguments, ct),
                "stop_autopilot" => await StopAutopilotAsync(services, arguments, ct),
                "chat_with_agent" => await ChatWithAgentAsync(services, arguments, ct),
                _ => Error($"Unknown tool \"{toolName}\"."),
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (JsonException ex) { return Error($"Invalid arguments: {ex.Message}"); }
        catch (Exception ex)
        {
            logger.LogError(ex, "MCP tool {Tool} failed for org {OrgId}", toolName, caller.OrgId);
            return Error($"The {toolName} tool failed: {Truncate(ex.GetBaseException().Message, 300)}");
        }
    }

    // ── project resolution ──────────────────────────────────────────────

    /// <summary>Resolves the tool's target project: the bound project for project-scoped keys,
    /// otherwise the "project" argument (slug or id) through the same membership-checked
    /// resolver every endpoint uses. Null + error text when it can't be resolved.</summary>
    private async Task<(Ulid? ProjectId, string? Error)> ResolveProjectAsync(
        IServiceProvider services, JsonElement arguments, CancellationToken ct)
    {
        if (caller.BoundProjectId is { } bound)
            return (bound, null);
        if (!arguments.TryGetProperty("project", out var p) || p.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(p.GetString()))
            return (null, "Provide \"project\" (slug or id) — see list_projects.");
        var resolved = await services.GetRequiredService<IProjectResolver>().ResolveAsync(p.GetString()!.Trim(), ct);
        return resolved is null ? (null, "No project with that slug or id is reachable with this key.") : (resolved, null);
    }

    private static Ulid? Id(JsonElement arguments, string name)
        => arguments.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String && Ulid.TryParse(el.GetString(), out var id) ? id : null;

    private static string? Str(JsonElement arguments, string name)
        => arguments.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    // ── reads ───────────────────────────────────────────────────────────

    private async Task<ChatToolResult> ListProjectsAsync(IServiceProvider services, CancellationToken ct)
    {
        var projectsApi = services.GetRequiredService<IProjectsApi>();
        var ids = caller.BoundProjectId is { } bound
            ? [bound]
            : await projectsApi.ListProjectIdsAsync(caller.OrgId, ct);
        var summaries = await projectsApi.GetProjectSummariesAsync(ids.ToList(), ct);
        // The resolver applies the same project-membership rule every endpoint uses.
        var resolver = services.GetRequiredService<IProjectResolver>();

        var sb = new StringBuilder();
        var count = 0;
        foreach (var project in summaries.Values.OrderBy(p => p.Name))
        {
            if (await resolver.ResolveAsync(project.Id.ToString(), ct) is null) continue;
            count++;
            sb.Append($"- {project.Name} (slug: {project.Slug}, key: {project.Key}, id: {project.Id})\n");
        }
        return count == 0
            ? new ChatToolResult("No projects are reachable with this key.")
            : new ChatToolResult($"{count} project(s). Use the slug as \"project\" in other tools:\n{sb}");
    }

    private async Task<ChatToolResult> GetProjectAsync(IServiceProvider services, JsonElement arguments, CancellationToken ct)
    {
        var (projectId, error) = await ResolveProjectAsync(services, arguments, ct);
        if (projectId is null) return Error(error!);
        var project = await services.GetRequiredService<IProjectsApi>().GetProjectSummaryAsync(projectId.Value, ct);
        if (project is null) return Error("Project not found.");
        var s = project.Settings;
        var sb = new StringBuilder();
        sb.Append($"# {project.Name}\nslug: {project.Slug}\nkey: {project.Key}\nid: {project.Id}\n");
        if (s is not null)
            sb.Append($"maxParallelTasks: {s.MaxParallelTasks}\nautoApprovePlan: {s.AutoApprovePlan}\nautoApproveCodeReview: {s.AutoApproveCodeReview}\nqaEnabled: {s.QaEnabled}\nautomationEnabled: {s.AutomationEnabled}\n");
        return new ChatToolResult(sb.ToString());
    }

    private async Task<ChatToolResult> ListTasksAsync(IServiceProvider services, JsonElement arguments, CancellationToken ct)
    {
        var (projectId, error) = await ResolveProjectAsync(services, arguments, ct);
        if (projectId is null) return Error(error!);
        var tasks = await services.GetRequiredService<ITasksApi>().ListProjectTaskSummariesAsync(caller.OrgId, projectId.Value, ct: ct);
        var lane = Str(arguments, "lane");
        var status = Str(arguments, "status");
        var filtered = tasks
            .Where(t => lane is null || t.CurrentLane.ToString().Equals(lane, StringComparison.OrdinalIgnoreCase))
            .Where(t => status is null || t.Status.ToString().Equals(status, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (filtered.Count == 0) return new ChatToolResult("No tasks match.");
        var sb = new StringBuilder($"{filtered.Count} task(s), newest first:\n");
        foreach (var t in filtered)
            sb.Append($"- #{t.Number} [{t.CurrentLane}/{t.Status}, {t.Priority} {t.Type}] {t.Title} (id: {t.Id})\n");
        return new ChatToolResult(sb.ToString());
    }

    private async Task<ChatToolResult> GetTaskAsync(IServiceProvider services, JsonElement arguments, CancellationToken ct)
    {
        var (projectId, error) = await ResolveProjectAsync(services, arguments, ct);
        if (projectId is null) return Error(error!);
        var taskId = Id(arguments, "task_id");
        if (taskId is null) return Error("Provide \"task_id\" — a task id from list_tasks.");
        var tasksApi = services.GetRequiredService<ITasksApi>();
        var task = await tasksApi.GetTaskSnapshotAsync(taskId.Value, ct);
        if (task is null || task.OrgId != caller.OrgId || task.ProjectId != projectId)
            return Error("No task with that id exists in this project.");

        var sb = new StringBuilder();
        sb.Append($"# Task #{task.Number}: {task.Title}\n");
        sb.Append($"id: {task.Id}\nlane: {task.CurrentLane}\nstatus: {task.Status}\npriority: {task.Priority}\ntype: {task.Type}\n");
        if (!string.IsNullOrWhiteSpace(task.BranchName)) sb.Append($"branch: {task.BranchName}\n");
        if (!string.IsNullOrWhiteSpace(task.PrUrl)) sb.Append($"pr: {task.PrUrl}\n");
        sb.Append('\n');
        sb.Append(string.IsNullOrWhiteSpace(task.Description) ? "(no description)" : Truncate(task.Description, 6000));

        var plan = await tasksApi.GetPendingPlanAsync(caller.OrgId, taskId.Value, ct);
        if (plan is not null)
        {
            sb.Append($"\n\n## Plan v{plan.Version} awaiting your review (plan id: {plan.PlanId})\n");
            sb.Append($"Summary: {plan.Summary}\n\nApproach:\n{Truncate(plan.Approach, 4000)}\n");
            if (plan.Files.Count > 0) sb.Append("Files:\n" + string.Join("\n", plan.Files.Select(f => "- " + f)) + "\n");
            sb.Append("Use approve_plan / reject_plan with this task_id.\n");
        }
        return new ChatToolResult(sb.ToString());
    }

    private async Task<ChatToolResult> GetTaskHistoryAsync(IServiceProvider services, JsonElement arguments, CancellationToken ct)
    {
        var (projectId, error) = await ResolveProjectAsync(services, arguments, ct);
        if (projectId is null) return Error(error!);
        var taskId = Id(arguments, "task_id");
        if (taskId is null) return Error("Provide \"task_id\".");
        var executions = await services.GetRequiredService<ITasksApi>().ListTaskExecutionSummariesAsync(caller.OrgId, projectId.Value, taskId.Value, ct);
        if (executions is null) return Error("No task with that id exists in this project.");
        if (executions.Count == 0) return new ChatToolResult("No agent has run a phase on this task yet.");
        var agentIds = executions.Where(e => e.AgentId is not null).Select(e => e.AgentId!.Value).Distinct().ToList();
        var names = agentIds.Count == 0 ? new Dictionary<Ulid, string>() : await services.GetRequiredService<IAgentsApi>().GetAgentNamesAsync(agentIds, ct);
        var sb = new StringBuilder($"{executions.Count} execution(s), oldest first:\n");
        var i = 0;
        foreach (var e in executions)
        {
            i++;
            var who = e.AgentId is { } aid ? names.GetValueOrDefault(aid, "removed agent") : "system";
            sb.Append($"{i}. {e.Lane} — {who} — {e.Status}");
            if (e.StartedAt is { } st) sb.Append($", started {st:yyyy-MM-dd HH:mm}");
            if (e.CompletedAt is { } co) sb.Append($", finished {co:yyyy-MM-dd HH:mm}");
            sb.Append('\n');
            if (!string.IsNullOrWhiteSpace(e.ErrorMessage)) sb.Append($"   error: {Truncate(e.ErrorMessage, 300)}\n");
        }
        return new ChatToolResult(sb.ToString());
    }

    private async Task<ChatToolResult> ListDocumentsAsync(IServiceProvider services, JsonElement arguments, CancellationToken ct)
    {
        var (projectId, error) = await ResolveProjectAsync(services, arguments, ct);
        if (projectId is null) return Error(error!);
        var docs = await services.GetRequiredService<IDocumentsApi>().ListDocumentSummariesAsync(caller.OrgId, projectId.Value, ct);
        if (docs.Count == 0) return new ChatToolResult("No documents in this project.");
        var sb = new StringBuilder($"{docs.Count} document(s):\n");
        foreach (var d in docs)
            sb.Append($"- {(string.IsNullOrEmpty(d.Path) ? "" : d.Path + "/")}{d.Title} (id: {d.Id}, v{d.CurrentVersion}{(d.IsFile ? ", file" : "")})\n");
        return new ChatToolResult(sb.ToString());
    }

    private async Task<ChatToolResult> GetDocumentAsync(IServiceProvider services, JsonElement arguments, CancellationToken ct)
    {
        var (projectId, error) = await ResolveProjectAsync(services, arguments, ct);
        if (projectId is null) return Error(error!);
        var docId = Id(arguments, "document_id");
        if (docId is null) return Error("Provide \"document_id\" — see list_documents.");
        var docs = await services.GetRequiredService<IDocumentsApi>().GetDocumentsContentAsync(caller.OrgId, projectId.Value, [docId.Value], ct);
        var doc = docs.FirstOrDefault();
        return doc is null ? Error("Document not found.") : new ChatToolResult($"# {doc.Title}\n\n{Truncate(doc.Content, TextCap)}");
    }

    private async Task<ChatToolResult> ListMilestonesAsync(IServiceProvider services, JsonElement arguments, CancellationToken ct)
    {
        var (projectId, error) = await ResolveProjectAsync(services, arguments, ct);
        if (projectId is null) return Error(error!);
        var milestones = await services.GetRequiredService<ITasksApi>().ListMilestonesForChatAsync(caller.OrgId, projectId.Value, ct);
        if (milestones.Count == 0) return new ChatToolResult("No milestones — tasks live in the Backlog.");
        var sb = new StringBuilder("Milestones (roadmap order):\n");
        foreach (var m in milestones) sb.Append($"- {m.Title} [{m.Status}] — {m.Outcome ?? "(no outcome)"} (id: {m.Id})\n");
        return new ChatToolResult(sb.ToString());
    }

    private async Task<ChatToolResult> ListReposAsync(IServiceProvider services, JsonElement arguments, CancellationToken ct)
    {
        var (projectId, error) = await ResolveProjectAsync(services, arguments, ct);
        if (projectId is null) return Error(error!);
        var repos = await services.GetRequiredService<IProjectsApi>().ListReposAsync(projectId.Value, ct);
        if (repos.Count == 0) return new ChatToolResult("No repositories linked.");
        return new ChatToolResult(string.Join("\n", repos.Select(r => $"- {r.Name} — {r.GitUrl} (branch {r.DefaultBranch}, {r.WorkspaceStatus}, id: {r.Id})")));
    }

    private async Task<ChatToolResult> ListAgentsAsync(IServiceProvider services, JsonElement arguments, CancellationToken ct)
    {
        var (projectId, error) = await ResolveProjectAsync(services, arguments, ct);
        if (projectId is null) return Error(error!);
        var agents = await services.GetRequiredService<IAgentsApi>().ListAgentsAsync(projectId.Value, ct);
        return new ChatToolResult(string.Join("\n", agents.Select(a => $"- {a.Name} ({a.Role}, model {a.Model}, id: {a.Id})")));
    }

    private async Task<ChatToolResult> ListSkillsAsync(IServiceProvider services, JsonElement arguments, CancellationToken ct)
    {
        var (projectId, error) = await ResolveProjectAsync(services, arguments, ct);
        if (projectId is null) return Error(error!);
        var db = services.GetRequiredService<AgentsDbContext>();
        var skills = await db.AgentSkills.AsNoTracking()
            .Where(s => s.OrgId == caller.OrgId && s.ProjectId == projectId).OrderBy(s => s.Name).ToListAsync(ct);
        if (skills.Count == 0) return new ChatToolResult("No skills in this project.");
        return new ChatToolResult(string.Join("\n", skills.Select(s => $"- /{s.Slug} — {s.Name}{(s.IsEnabled ? "" : " (disabled)")}: {s.Description}")));
    }

    private async Task<ChatToolResult> ListSchedulesAsync(IServiceProvider services, JsonElement arguments, CancellationToken ct)
    {
        var (projectId, error) = await ResolveProjectAsync(services, arguments, ct);
        if (projectId is null) return Error(error!);
        var db = services.GetRequiredService<AgentsDbContext>();
        var rows = await db.AgentSchedules.AsNoTracking()
            .Where(s => s.OrgId == caller.OrgId && s.ProjectId == projectId).OrderBy(s => s.Name).ToListAsync(ct);
        if (rows.Count == 0) return new ChatToolResult("No schedules in this project.");
        return new ChatToolResult(string.Join("\n", rows.Select(s =>
            $"- {s.Name} [{s.Kind}{(s.IsEnabled ? "" : ", disabled")}] {(s.CronExpression is null ? $"once at {s.FireAt:u}" : $"cron {s.CronExpression} ({s.TimeZoneId})")}" +
            $"{(s.NextRunAt is { } n ? $", next {n:u}" : "")}{(s.LastRunStatus is { } l ? $", last {l}" : "")}")));
    }

    private async Task<ChatToolResult> GetInboxAsync(IServiceProvider services, JsonElement arguments, CancellationToken ct)
    {
        Ulid? projectId = caller.BoundProjectId;
        if (projectId is null && Str(arguments, "project") is { } raw && !string.IsNullOrWhiteSpace(raw))
        {
            projectId = await services.GetRequiredService<IProjectResolver>().ResolveAsync(raw.Trim(), ct);
            if (projectId is null) return Error("No project with that slug or id is reachable with this key.");
        }
        var items = await services.GetRequiredService<ITasksApi>().ListAttentionTasksAsync(caller.OrgId, projectId, 50, ct);
        if (items.Count == 0) return new ChatToolResult("Nothing is waiting on a human right now.");
        var sb = new StringBuilder($"{items.Count} task(s) waiting on a human:\n");
        foreach (var t in items)
            sb.Append($"- {t.Key} [{t.Lane}/{t.Status}] {t.Title} (project: {t.ProjectSlug}, task_id: {t.TaskId})\n");
        sb.Append("Statuses: WaitingPlanApproval → get_task then approve_plan/reject_plan; WaitingAnswer → answer_task; WaitingCodeReview → review_pull_request/merge_pull_request.");
        return new ChatToolResult(sb.ToString());
    }

    private async Task<ChatToolResult> GetAutopilotStatusAsync(IServiceProvider services, JsonElement arguments, CancellationToken ct)
    {
        var (projectId, error) = await ResolveProjectAsync(services, arguments, ct);
        if (projectId is null) return Error(error!);
        var status = await services.GetRequiredService<ITasksApi>().GetAutopilotStatusAsync(caller.OrgId, projectId.Value, ct);
        return new ChatToolResult(status.Running
            ? $"Autopilot is RUNNING since {status.StartedAt:u} — cap {(status.CapUsd is { } c ? $"${c:0.##}" : "none")}, spent ${status.SpentUsd:0.##}, submitted {status.TasksSubmitted}, completed {status.TasksCompleted}, failed {status.TasksFailed}."
            : $"Autopilot is not running.{(status.StopReason is { } r ? $" Last session ended: {r} at {status.StoppedAt:u}." : "")}");
    }

    // ── writes ──────────────────────────────────────────────────────────

    private sealed record NewTaskArgs(string Title, string? Description = null, string? Priority = null, string? Type = null, string? Repo = null, string? Milestone = null, List<string>? Tags = null);
    private sealed record CreateTasksArgs(List<NewTaskArgs> Tasks);

    private async Task<ChatToolResult> CreateTasksAsync(IServiceProvider services, JsonElement arguments, CancellationToken ct)
    {
        var (projectId, error) = await ResolveProjectAsync(services, arguments, ct);
        if (projectId is null) return Error(error!);
        var args = arguments.Deserialize<CreateTasksArgs>(JsonOptions);
        if (args?.Tasks is not { Count: > 0 }) return Error("Provide \"tasks\": [{ title, description?, priority?, type?, repo?, milestone?, tags? }].");
        if (args.Tasks.Count > 20) return Error("At most 20 tasks per call.");

        var projectsApi = services.GetRequiredService<IProjectsApi>();
        var tasksApi = services.GetRequiredService<ITasksApi>();
        var repos = await projectsApi.ListReposAsync(projectId.Value, ct);
        if (repos.Count == 0) return Error("The project has no repositories — link one before creating tasks.");
        var milestones = await tasksApi.ListMilestonesForChatAsync(caller.OrgId, projectId.Value, ct);

        var specs = new List<ChatTaskSpec>();
        foreach (var t in args.Tasks)
        {
            if (string.IsNullOrWhiteSpace(t.Title)) return Error("Every task needs a title.");
            var repo = t.Repo is null
                ? (repos.Count == 1 ? repos[0] : null)
                : repos.FirstOrDefault(r => r.Name.Equals(t.Repo, StringComparison.OrdinalIgnoreCase) || r.Id.ToString() == t.Repo);
            if (repo is null)
                return Error(repos.Count == 1 ? "Repository not found." : $"Task \"{t.Title}\": pick a \"repo\" — one of {string.Join(", ", repos.Select(r => r.Name))}.");
            Ulid? milestoneId = null;
            if (!string.IsNullOrWhiteSpace(t.Milestone))
            {
                var m = milestones.FirstOrDefault(x => x.Title.Equals(t.Milestone, StringComparison.OrdinalIgnoreCase) || x.Id.ToString() == t.Milestone);
                if (m is null) return Error($"Milestone \"{t.Milestone}\" not found — see list_milestones.");
                milestoneId = m.Id;
            }
            specs.Add(new ChatTaskSpec(
                t.Title.Trim().Length > 500 ? t.Title.Trim()[..500] : t.Title.Trim(),
                t.Description is null ? null : (t.Description.Length > 10_000 ? t.Description[..10_000] : t.Description),
                t.Priority ?? "Medium", t.Type ?? "Feature", repo.Id, t.Tags ?? [], null, milestoneId));
        }

        // SourceMessageId = a fresh id: nothing to link, but a value keeps the PM-review skip
        // rule (SourceMessageId set = already refined by a human/agent) consistent with chat.
        var created = await tasksApi.CreateChatTasksAsync(caller.OrgId, projectId.Value, Ulid.NewUlid(), caller.UserId, caller.Locale, specs, ct);
        return new ChatToolResult("Created (Drafting — call submit_task to start work):\n" +
                                  string.Join("\n", created.Select(c => $"- #{c.Number} {c.Title} (task_id: {c.Id})")));
    }

    private async Task<ChatToolResult> SubmitTaskAsync(IServiceProvider services, JsonElement arguments, CancellationToken ct)
    {
        var taskId = Id(arguments, "task_id");
        if (taskId is null) return Error("Provide \"task_id\".");
        var outcome = await services.GetRequiredService<ITasksApi>().SubmitTaskAsync(caller.OrgId, taskId.Value, caller.Locale, ct);
        return FromOutcome(outcome, "Task submitted — it is now in the queue.");
    }

    private async Task<ChatToolResult> AnswerTaskAsync(IServiceProvider services, JsonElement arguments, CancellationToken ct)
    {
        var taskId = Id(arguments, "task_id");
        var content = Str(arguments, "message");
        if (taskId is null || string.IsNullOrWhiteSpace(content)) return Error("Provide \"task_id\" and \"message\".");
        var outcome = await services.GetRequiredService<ITasksApi>().AnswerTaskAsync(caller.OrgId, taskId.Value, content, "Text", caller.UserId, caller.Locale, ct);
        return FromOutcome(outcome, "Message posted. If the agent was waiting on you, it has been resumed.");
    }

    private async Task<ChatToolResult> ReviewPlanAsync(IServiceProvider services, JsonElement arguments, bool approve, CancellationToken ct)
    {
        var taskId = Id(arguments, "task_id");
        if (taskId is null) return Error("Provide \"task_id\".");
        var tasksApi = services.GetRequiredService<ITasksApi>();
        var planId = Id(arguments, "plan_id") ?? (await tasksApi.GetPendingPlanAsync(caller.OrgId, taskId.Value, ct))?.PlanId;
        if (planId is null) return Error("This task has no plan awaiting review.");
        var feedback = Str(arguments, "feedback");
        if (!approve && string.IsNullOrWhiteSpace(feedback)) return Error("Rejecting needs \"feedback\" — the agent re-plans from it.");
        var outcome = await tasksApi.ReviewPlanAsync(caller.OrgId, taskId.Value, planId.Value, approve, feedback, ct);
        return FromOutcome(outcome, approve ? "Plan approved — the task moved to Developing." : "Plan rejected — the task went back to Planning with your feedback.");
    }

    private async Task<ChatToolResult> ReviewPullRequestAsync(IServiceProvider services, JsonElement arguments, CancellationToken ct)
    {
        var taskId = Id(arguments, "task_id");
        var decision = Str(arguments, "decision");
        if (taskId is null || string.IsNullOrWhiteSpace(decision)) return Error("Provide \"task_id\" and \"decision\" (approve | request_changes | comment).");
        var outcome = await services.GetRequiredService<ITasksApi>().SubmitCodeReviewAsync(caller.OrgId, taskId.Value, decision, Str(arguments, "body"), null, ct);
        return FromOutcome(outcome, "Review recorded.");
    }

    private async Task<ChatToolResult> MergePullRequestAsync(IServiceProvider services, JsonElement arguments, CancellationToken ct)
    {
        var taskId = Id(arguments, "task_id");
        if (taskId is null) return Error("Provide \"task_id\".");
        var outcome = await services.GetRequiredService<ITasksApi>().MergePullRequestAsync(caller.OrgId, taskId.Value, ct);
        return FromOutcome(outcome, outcome.ResolutionStarted
            ? "Merge conflicts detected — automatic resolution started; the merge lands on a later worker cycle."
            : "Merged — the task is Done.");
    }

    private async Task<ChatToolResult> StartAutopilotAsync(IServiceProvider services, JsonElement arguments, CancellationToken ct)
    {
        var (projectId, error) = await ResolveProjectAsync(services, arguments, ct);
        if (projectId is null) return Error(error!);
        decimal? cap = arguments.TryGetProperty("cap_usd", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDecimal() : null;
        var uncapped = arguments.TryGetProperty("uncapped", out var u) && u.ValueKind == JsonValueKind.True;
        var answerEveryQuestion = arguments.TryGetProperty("answer_every_question", out var a) && a.ValueKind == JsonValueKind.True;
        if (cap is null && !uncapped) return Error("Set \"cap_usd\" (spend ceiling), or pass \"uncapped\": true.");
        if (cap is <= 0) return Error("cap_usd must be positive.");
        if (uncapped && !caller.Has(Permission.BillingManage)) return Error("Uncapped autopilot requires BillingManage.");
        if (answerEveryQuestion && !caller.Has(Permission.ProjectsManage)) return Error("answer_every_question requires ProjectsManage.");
        var tasksApi = services.GetRequiredService<ITasksApi>();
        var sessionId = await tasksApi.StartAutopilotSessionAsync(caller.OrgId, projectId.Value, caller.UserId, uncapped ? null : cap, answerEveryQuestion, ct);
        return sessionId is null ? Error("Autopilot is already running (or could not start).") : new ChatToolResult($"Autopilot started (session {sessionId}).");
    }

    private async Task<ChatToolResult> StopAutopilotAsync(IServiceProvider services, JsonElement arguments, CancellationToken ct)
    {
        var (projectId, error) = await ResolveProjectAsync(services, arguments, ct);
        if (projectId is null) return Error(error!);
        var tasksApi = services.GetRequiredService<ITasksApi>();
        var status = await tasksApi.GetAutopilotStatusAsync(caller.OrgId, projectId.Value, ct);
        if (!status.Running || status.SessionId is not { } sid) return new ChatToolResult("Autopilot was not running.");
        var stopped = await tasksApi.StopAutopilotSessionAsync(sid, AutopilotStopReason.Manual, ct);
        return new ChatToolResult(stopped ? "Autopilot stopped (in-flight tasks finish)." : "Autopilot had already stopped.");
    }

    private async Task<ChatToolResult> ChatWithAgentAsync(IServiceProvider services, JsonElement arguments, CancellationToken ct)
    {
        var (projectId, error) = await ResolveProjectAsync(services, arguments, ct);
        if (projectId is null) return Error(error!);
        var message = Str(arguments, "message");
        var agentRef = Str(arguments, "agent");
        if (string.IsNullOrWhiteSpace(message) || string.IsNullOrWhiteSpace(agentRef))
            return Error("Provide \"agent\" (name, role or id — see list_agents) and \"message\".");

        var db = services.GetRequiredService<AgentsDbContext>();
        var agents = await db.ProjectAgents.Where(a => a.OrgId == caller.OrgId && a.ProjectId == projectId).ToListAsync(ct);
        var agent = agents.FirstOrDefault(a => a.Id.ToString() == agentRef)
            ?? agents.FirstOrDefault(a => a.Name.Equals(agentRef, StringComparison.OrdinalIgnoreCase))
            ?? agents.FirstOrDefault(a => a.Role.ToString().Equals(agentRef, StringComparison.OrdinalIgnoreCase));
        if (agent is null) return Error($"No agent \"{agentRef}\" — see list_agents.");

        // One MCP conversation per (agent, caller): follow-ups continue the thread; the
        // Team page shows it like any other conversation.
        var conversationId = Id(arguments, "conversation_id");
        AgentConversation? conversation = conversationId is { } cid
            ? await db.AgentConversations.FirstOrDefaultAsync(c => c.Id == cid && c.OrgId == caller.OrgId && c.AgentId == agent.Id, ct)
            : null;
        if (conversation is null)
        {
            conversation = new AgentConversation
            {
                Id = Ulid.NewUlid(), OrgId = caller.OrgId, ProjectId = projectId.Value, AgentId = agent.Id,
                Title = $"MCP · {Truncate(message, 60)}",
            };
            db.AgentConversations.Add(conversation);
        }
        var coordinator = services.GetRequiredService<ChatRunCoordinator>();
        if (coordinator.IsRunning(conversation.Id))
            return Error("The agent is still replying to the previous message in this conversation — try again shortly.");

        var userMessage = new AgentMessage { OrgId = caller.OrgId, ConversationId = conversation.Id, Role = "user", Content = message };
        db.AgentMessages.Add(userMessage);
        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        var preparer = services.GetRequiredService<ChatTurnPreparer>();
        var projectSkills = await preparer.ListEnabledProjectSkillsAsync(caller.OrgId, projectId.Value, ct);
        var invoked = SkillInvocation.TryParse(message, projectSkills);
        var spec = await preparer.PrepareAsync(new ChatTurnInput(
            caller.OrgId, caller.UserId, caller.Locale, projectId.Value, agent, conversation, message,
            ExcludeMessageId: userMessage.Id,
            InvokedSkillBlock: invoked is null ? null : SkillInvocation.Block(invoked.Skill)), ct);
        conversation.ActiveRunStartedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        var handle = coordinator.TryStartRun(spec);
        if (handle is null) return Error("The agent is busy — try again shortly.");

        // Wait a bounded time for the reply (MCP clients time out); past that, hand back the
        // conversation id so the caller can read the reply later.
        var text = new StringBuilder();
        string? runError = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(50));
        var completed = true;
        try
        {
            await foreach (var payload in handle.Events.ReadAllAsync(timeout.Token))
            {
                var json = JsonSerializer.SerializeToElement(payload);
                if (json.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String) text.Append(t.GetString());
                if (json.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String) runError = e.GetString();
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            completed = false;
            handle.MarkDetached();
        }

        var head = $"[conversation_id: {conversation.Id} — pass it back to continue the thread]\n\n";
        if (!completed)
            return new ChatToolResult(head + $"{agent.Name} is still working (reply so far: {Truncate(text.ToString(), 2000)}). Read the full reply on the Team page or ask again with the conversation_id.");
        if (runError is not null && text.Length == 0)
            return Error($"{agent.Name} failed to reply: {runError}");
        return new ChatToolResult(head + Truncate(text.ToString().Trim(), TextCap));
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private static ChatToolResult FromOutcome(BoardOpOutcome outcome, string success) =>
        outcome.Ok
            ? new ChatToolResult(outcome.Warning is null ? success : $"{success}\nNote: {outcome.Warning}")
            : Error(outcome.Message ?? "The operation failed.");

    private static ChatToolResult Error(string message) => new(message, IsError: true);

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    // ── definitions ─────────────────────────────────────────────────────

    private const string ProjectProp =
        "\"project\": { \"type\": \"string\", \"description\": \"Project slug or id (see list_projects). Ignored for project-bound keys.\" }";
    private const string TaskIdProp =
        "\"task_id\": { \"type\": \"string\", \"description\": \"Task id (from list_tasks / get_inbox).\" }";

    private static readonly ChatToolDefinition ListProjectsTool = new("list_projects",
        "List the projects this key can reach (name, slug, key). Start here.",
        """{ "type": "object", "properties": {} }""");
    private static readonly ChatToolDefinition GetProjectTool = new("get_project",
        "Project details and the main settings.",
        $$"""{ "type": "object", "properties": { {{ProjectProp}} } }""");
    private static readonly ChatToolDefinition ListTasksTool = new("list_tasks",
        "Tasks on a project's board (newest first), optionally filtered by lane or status.",
        $$"""{ "type": "object", "properties": { {{ProjectProp}}, "lane": { "type": "string" }, "status": { "type": "string" } } }""");
    private static readonly ChatToolDefinition GetTaskTool = new("get_task",
        "A task's details, plus the plan awaiting review when there is one.",
        $$"""{ "type": "object", "properties": { {{ProjectProp}}, {{TaskIdProp}} }, "required": ["task_id"] }""");
    private static readonly ChatToolDefinition GetTaskHistoryTool = new("get_task_history",
        "Phase-by-phase execution timeline of a task (which agent ran what, when, errors).",
        $$"""{ "type": "object", "properties": { {{ProjectProp}}, {{TaskIdProp}} }, "required": ["task_id"] }""");
    private static readonly ChatToolDefinition ListDocumentsTool = new("list_documents",
        "The project's Documents library (titles, paths, ids).",
        $$"""{ "type": "object", "properties": { {{ProjectProp}} } }""");
    private static readonly ChatToolDefinition GetDocumentTool = new("get_document",
        "Full markdown of one document.",
        $$"""{ "type": "object", "properties": { {{ProjectProp}}, "document_id": { "type": "string" } }, "required": ["document_id"] }""");
    private static readonly ChatToolDefinition ListMilestonesTool = new("list_milestones",
        "Roadmap milestones (title, outcome, status).",
        $$"""{ "type": "object", "properties": { {{ProjectProp}} } }""");
    private static readonly ChatToolDefinition ListReposTool = new("list_repos",
        "Linked repositories (name is what create_tasks' \"repo\" takes).",
        $$"""{ "type": "object", "properties": { {{ProjectProp}} } }""");
    private static readonly ChatToolDefinition ListAgentsTool = new("list_agents",
        "The project's agent team (name, role, model).",
        $$"""{ "type": "object", "properties": { {{ProjectProp}} } }""");
    private static readonly ChatToolDefinition ListSkillsTool = new("list_skills",
        "Skills (playbooks) in the project — invoke one in chat_with_agent by starting the message with /slug.",
        $$"""{ "type": "object", "properties": { {{ProjectProp}} } }""");
    private static readonly ChatToolDefinition ListSchedulesTool = new("list_schedules",
        "Schedules on the project's Automation page (playbooks, autopilot windows, reminders).",
        $$"""{ "type": "object", "properties": { {{ProjectProp}} } }""");
    private static readonly ChatToolDefinition GetInboxTool = new("get_inbox",
        "Everything waiting on a human across your projects (plan approvals, questions, reviews, failures). Optionally one project.",
        $$"""{ "type": "object", "properties": { {{ProjectProp}} } }""");
    private static readonly ChatToolDefinition GetAutopilotStatusTool = new("get_autopilot_status",
        "Whether Autopilot is running on the project, cap and spend.",
        $$"""{ "type": "object", "properties": { {{ProjectProp}} } }""");
    private static readonly ChatToolDefinition CreateTasksTool = new("create_tasks",
        "Create Drafting tasks (they don't run until submit_task). Repo is required when the project has several.",
        $$"""
        { "type": "object",
          "properties": { {{ProjectProp}},
            "tasks": { "type": "array", "items": { "type": "object", "properties": {
              "title": { "type": "string" }, "description": { "type": "string" },
              "priority": { "type": "string", "enum": ["Low","Medium","High","Critical"] },
              "type": { "type": "string", "enum": ["Feature","BugFix","Refactor","Chore","Docs"] },
              "repo": { "type": "string" }, "milestone": { "type": "string" },
              "tags": { "type": "array", "items": { "type": "string" } } }, "required": ["title"] } } },
          "required": ["tasks"] }
        """);
    private static readonly ChatToolDefinition SubmitTaskTool = new("submit_task",
        "Submit a Drafting task so the agents start on it.",
        $$"""{ "type": "object", "properties": { {{TaskIdProp}} }, "required": ["task_id"] }""");
    private static readonly ChatToolDefinition AnswerTaskTool = new("answer_task",
        "Post a message on a task — answers an agent's question, gives feedback on a parked task, or resumes a review-escalated one.",
        $$"""{ "type": "object", "properties": { {{TaskIdProp}}, "message": { "type": "string" } }, "required": ["task_id", "message"] }""");
    private static readonly ChatToolDefinition ApprovePlanTool = new("approve_plan",
        "Approve the plan awaiting review on a task (moves it to Developing).",
        $$"""{ "type": "object", "properties": { {{TaskIdProp}}, "plan_id": { "type": "string" }, "feedback": { "type": "string" } }, "required": ["task_id"] }""");
    private static readonly ChatToolDefinition RejectPlanTool = new("reject_plan",
        "Reject the plan awaiting review with feedback (the agent re-plans).",
        $$"""{ "type": "object", "properties": { {{TaskIdProp}}, "plan_id": { "type": "string" }, "feedback": { "type": "string" } }, "required": ["task_id", "feedback"] }""");
    private static readonly ChatToolDefinition ReviewPullRequestTool = new("review_pull_request",
        "Submit a code review on the task's PR: approve (→ Done), request_changes (→ back to the dev agent), or comment.",
        $$"""{ "type": "object", "properties": { {{TaskIdProp}}, "decision": { "type": "string", "enum": ["approve","request_changes","comment"] }, "body": { "type": "string" } }, "required": ["task_id", "decision"] }""");
    private static readonly ChatToolDefinition MergePullRequestTool = new("merge_pull_request",
        "Squash-merge the task's PR and complete the task.",
        $$"""{ "type": "object", "properties": { {{TaskIdProp}} }, "required": ["task_id"] }""");
    private static readonly ChatToolDefinition StartAutopilotTool = new("start_autopilot",
        "Start Autopilot on a project with a spend cap (USD). answer_every_question: never wait for a person on a question, deletion/billing/production included (requires ProjectsManage).",
        $$"""{ "type": "object", "properties": { {{ProjectProp}}, "cap_usd": { "type": "number" }, "uncapped": { "type": "boolean" }, "answer_every_question": { "type": "boolean" } } }""");
    private static readonly ChatToolDefinition StopAutopilotTool = new("stop_autopilot",
        "Stop the project's Autopilot session (in-flight tasks finish).",
        $$"""{ "type": "object", "properties": { {{ProjectProp}} } }""");
    private static readonly ChatToolDefinition ChatWithAgentTool = new("chat_with_agent",
        "Send a message to one of the project's agents (by name, role or id) and get the reply. Start the message with /slug to run a skill. Pass conversation_id to continue a thread.",
        $$"""{ "type": "object", "properties": { {{ProjectProp}}, "agent": { "type": "string" }, "message": { "type": "string" }, "conversation_id": { "type": "string" } }, "required": ["agent", "message"] }""");
}
