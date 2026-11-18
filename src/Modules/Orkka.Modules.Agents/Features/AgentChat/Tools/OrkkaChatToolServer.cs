namespace Orkka.Modules.Agents.Features.AgentChat.Tools;

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Domain;
using Orkka.Modules.Agents.Infrastructure;
using Orkka.SharedKernel.Storage;
using Orkka.Modules.Documents.Contracts;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;

/// <summary>
/// The per-turn "orkka" MCP tool server for agent chat: create/read documents, tasks,
/// diagrams, and design assets through the modules' public facades — the same in-process
/// path the fenced-block extractors use, with the same sanitization. One instance per turn;
/// all tenant identity comes from <see cref="ChatTurnToolContext"/>, never from tool
/// arguments. Handlers run on the SDK transport's read loop, concurrent with the turn
/// runner's scoped DbContext — every call opens its own DI scope.
/// </summary>
internal sealed class OrkkaChatToolServer(
    ChatTurnToolContext context,
    IServiceScopeFactory scopeFactory,
    ILogger logger) : IChatToolServer
{
    public const string Name = "orkka";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private const int GetDocumentContentCap = 50_000;
    private const int GetTaskDescriptionCap = 10_000;

    public string ServerName => Name;

    public IReadOnlyList<ChatToolDefinition> ListTools()
    {
        var tools = new List<ChatToolDefinition>
        {
            CreateDocumentTool,
            ListDocumentsTool,
            GetDocumentTool,
            ListTasksTool,
            GetTaskTool,
            ListMyTasksTool,
            GetTaskHistoryTool,
            ListReposTool,
            ListMilestonesTool,
            SetReminderTool,
            ListMyRemindersTool,
            CancelReminderTool,
            RememberTool,
            RecallTool,
        };

        // Role gating layer 1 of 2: other roles never even see these tools (layer 2 is the
        // invoker's allowed-tools list, built from this same filtered set).
        if (context.AgentRole == AgentRole.PM)
        {
            tools.Add(CreateTasksTool);
            tools.Add(CreateMilestoneTool);
        }
        if (context.AgentRole == AgentRole.SolutionArchitect)
            tools.Add(CreateDiagramTool);
        if (context.AgentRole is AgentRole.Designer or AgentRole.GameDesigner)
        {
            // Reading before writing. Without these the designer could only create: it had the
            // palette and nothing else, so it re-imagined the project's own mascot and icons
            // from their description every time ("we have all the icons in frontend app and on
            // the design page for you to use", 2026-09-08).
            tools.Add(ListDesignAssetsTool);
            tools.Add(GetDesignAssetTool);
            tools.Add(CreateDesignAssetsTool);
        }

        return tools;
    }

    public async Task<ChatToolResult> CallToolAsync(
        string toolName, JsonElement arguments, CancellationToken ct = default)
    {
        if (ListTools().All(t => t.Name != toolName))
            return Error($"Unknown tool \"{toolName}\".");

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var services = scope.ServiceProvider;

            return toolName switch
            {
                "create_document" => await CreateDocumentAsync(services, arguments, ct),
                "list_documents" => await ListDocumentsAsync(services, ct),
                "get_document" => await GetDocumentAsync(services, arguments, ct),
                "create_tasks" => await CreateTasksAsync(services, arguments, ct),
                "list_tasks" => await ListTasksAsync(services, ct),
                "get_task" => await GetTaskAsync(services, arguments, ct),
                "list_my_tasks" => await ListMyTasksAsync(services, ct),
                "get_task_history" => await GetTaskHistoryAsync(services, arguments, ct),
                "list_repos" => await ListReposAsync(services, ct),
                "list_milestones" => await ListMilestonesAsync(services, ct),
                "create_milestone" => await CreateMilestoneAsync(services, arguments, ct),
                "set_reminder" => await SetReminderAsync(services, arguments, ct),
                "list_my_reminders" => await ListMyRemindersAsync(services, ct),
                "cancel_reminder" => await CancelReminderAsync(services, arguments, ct),
                "remember" => await RememberAsync(services, arguments, ct),
                "recall" => await RecallAsync(services, arguments, ct),
                "create_diagram" => await CreateDiagramAsync(services, arguments, ct),
                "create_design_assets" => await CreateDesignAssetsAsync(services, arguments, ct),
                "list_design_assets" => await ListDesignAssetsAsync(services, arguments, ct),
                "get_design_asset" => await GetDesignAssetAsync(services, arguments, ct),
                _ => Error($"Unknown tool \"{toolName}\"."),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            return Error($"Invalid arguments: {ex.Message}");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Chat tool {Tool} failed for conversation {ConversationId}",
                toolName, context.ConversationId);
            // GetBaseException: EF wraps the useful message ("value too long", constraint
            // names) in DbUpdateException — the model needs the root cause to self-correct.
            return Error($"The {toolName} tool failed: {Truncate(ex.GetBaseException().Message, 300)}");
        }
    }

    private static ChatToolResult Error(string message) => new(message, IsError: true);

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    /// <summary>Inserts the assistant message row (pre-allocated id, empty content) if it
    /// doesn't exist yet. Diagrams and design assets carry REAL foreign keys to
    /// agent_messages — unlike the cross-module document/task references — and the reply
    /// that is still streaming has no row until the turn runner saves it. The runner's
    /// post-stream save updates this placeholder's content in place.</summary>
    private async Task EnsureAssistantMessageRowAsync(IServiceProvider services, CancellationToken ct)
    {
        await context.MessageRowGate.WaitAsync(ct);
        try
        {
            if (context.MessageRowEnsured)
                return;

            var db = services.GetRequiredService<AgentsDbContext>();
            var exists = await db.AgentMessages.AnyAsync(
                m => m.Id == context.AssistantMessageId && m.OrgId == context.OrgId, ct);
            if (!exists)
            {
                db.AgentMessages.Add(new AgentMessage
                {
                    Id = context.AssistantMessageId,
                    OrgId = context.OrgId,
                    ConversationId = context.ConversationId,
                    Role = "assistant",
                    Content = "",
                });
                await db.SaveChangesAsync(ct);
            }

            context.MessageRowEnsured = true;
        }
        finally
        {
            context.MessageRowGate.Release();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Documents
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<ChatToolResult> CreateDocumentAsync(
        IServiceProvider services, JsonElement arguments, CancellationToken ct)
    {
        var block = JsonSerializer.Deserialize<AgentDocumentsBlock>(arguments.GetRawText(), JsonOptions);
        if (block?.Documents is not { Count: > 0 })
            return Error("Provide at least one document in \"documents\".");

        var specs = ChatArtifactSanitizers.SanitizeDocuments(block.Documents);
        if (specs.Count == 0)
            return Error("No usable documents: every entry needs a non-empty \"title\" and \"content\".");

        var documentsApi = services.GetRequiredService<IDocumentsApi>();
        var created = await documentsApi.CreateChatDocumentsAsync(
            context.OrgId, context.ProjectId, context.AgentId, context.ConversationId,
            context.AssistantMessageId, context.RequesterUserId, specs, ct);

        context.AddCreatedDocuments(created.Count);
        foreach (var document in created)
            context.Emit(new { document = new { id = document.Id.ToString(), title = document.Title } });

        var sb = new StringBuilder($"Saved {created.Count} document(s) to the project's Documents library:\n");
        foreach (var document in created)
        {
            var outcome = document.Outcome switch
            {
                ChatDocumentOutcome.Updated =>
                    $" — revised the existing document with this title (now v{document.Version})",
                ChatDocumentOutcome.Unchanged =>
                    $" — already saved with identical content (v{document.Version}, nothing changed)",
                _ => "",
            };
            sb.Append($"- \"{document.Title}\" (id: {document.Id}){outcome}\n");
        }
        sb.Append("The chat links each document automatically — confirm briefly, do not invent URLs.");
        return new ChatToolResult(sb.ToString());
    }

    private async Task<ChatToolResult> ListDocumentsAsync(IServiceProvider services, CancellationToken ct)
    {
        var documentsApi = services.GetRequiredService<IDocumentsApi>();
        var documents = await documentsApi.ListDocumentSummariesAsync(context.OrgId, context.ProjectId, ct);

        if (documents.Count == 0)
            return new ChatToolResult("The project's Documents library is empty.");

        var sb = new StringBuilder($"{documents.Count} document(s) in the project:\n");
        foreach (var doc in documents)
        {
            var location = doc.Path.Length == 0 ? doc.Title : $"{doc.Path}/{doc.Title}";
            // Flag uploaded files: their body is a stored PDF, so create_document cannot revise
            // one and get_document returns extracted text rather than the markdown it stores.
            var kind = doc.IsFile ? ", uploaded file" : "";
            sb.Append($"- {location} (id: {doc.Id}, v{doc.CurrentVersion}, updated {doc.UpdatedAt:yyyy-MM-dd}{kind})\n");
        }
        return new ChatToolResult(sb.ToString());
    }

    private async Task<ChatToolResult> GetDocumentAsync(
        IServiceProvider services, JsonElement arguments, CancellationToken ct)
    {
        if (!arguments.TryGetProperty("documentId", out var idEl) ||
            !Ulid.TryParse(idEl.GetString(), out var documentId))
        {
            return Error("Provide \"documentId\" — a document id from list_documents.");
        }

        var documentsApi = services.GetRequiredService<IDocumentsApi>();
        var documents = await documentsApi.GetDocumentsContentAsync(
            context.OrgId, context.ProjectId, [documentId], ct);

        if (documents.Count == 0)
            return Error("No document with that id exists in this project.");

        var doc = documents[0];
        var content = doc.Content.Length <= GetDocumentContentCap
            ? doc.Content
            : doc.Content[..GetDocumentContentCap] + "\n[… truncated]";
        return new ChatToolResult($"# {doc.Title}\n(path: \"{doc.Path}\", id: {doc.Id})\n\n{content}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Tasks
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<ChatToolResult> CreateTasksAsync(
        IServiceProvider services, JsonElement arguments, CancellationToken ct)
    {
        var block = JsonSerializer.Deserialize<PmTasksBlock>(arguments.GetRawText(), JsonOptions);
        if (block?.Tasks is not { Count: > 0 })
            return Error("Provide at least one task in \"tasks\".");

        var drafts = block.Tasks.Take(ChatArtifactSanitizers.MaxTasksPerMessage).ToList();

        var projectsApi = services.GetRequiredService<IProjectsApi>();
        var projectRepos = (await projectsApi.ListReposAsync(context.ProjectId, ct))
            .Where(r => r.OrgId == context.OrgId)
            .Select(r => new ChatRepoRef(r.Id, r.Name))
            .ToList();

        if (projectRepos.Count == 0)
            return Error("This project has no repositories yet — tasks need a repo. Ask the user to link one first.");

        // Only looked up when a task actually names a milestone. Most create_tasks calls file
        // into the Backlog, and those shouldn't pay for a query nobody asked for.
        var tasksApi = services.GetRequiredService<ITasksApi>();
        var projectMilestones = drafts.Any(d => !string.IsNullOrWhiteSpace(d.Milestone))
            ? await tasksApi.ListMilestonesForChatAsync(context.OrgId, context.ProjectId, ct)
            : null;

        var specs = ChatArtifactSanitizers.SanitizeTasks(drafts, projectRepos, projectMilestones);
        if (specs.Count == 0)
            return Error("No usable tasks: every entry needs a non-empty \"title\".");

        var created = await tasksApi.CreateChatTasksAsync(
            context.OrgId, context.ProjectId, context.AssistantMessageId,
            context.RequesterUserId, context.RequesterLocale, specs, ct);

        context.AddCreatedTasks(created.Count);
        foreach (var task in created)
            context.Emit(new { task = new { id = task.Id.ToString(), number = task.Number, title = task.Title } });

        var sb = new StringBuilder($"Created {created.Count} task(s) on the project board (Drafting lane):\n");
        foreach (var task in created)
            sb.Append($"- #{task.Number} {task.Title} (id: {task.Id})\n");
        if (block.Tasks.Count > drafts.Count)
            sb.Append($"({block.Tasks.Count - drafts.Count} extra task(s) beyond the {ChatArtifactSanitizers.MaxTasksPerMessage}-per-call limit were skipped — call again for the rest.)\n");
        sb.Append("The chat links each task automatically — confirm briefly, do not invent URLs.");
        return new ChatToolResult(sb.ToString());
    }

    private async Task<ChatToolResult> ListMilestonesAsync(
        IServiceProvider services, CancellationToken ct)
    {
        var tasksApi = services.GetRequiredService<ITasksApi>();
        var milestones = await tasksApi.ListMilestonesForChatAsync(context.OrgId, context.ProjectId, ct);

        if (milestones.Count == 0)
            return new ChatToolResult("This project has no milestones yet — tasks land in the Backlog.");

        var sb = new StringBuilder("Milestones (roadmap order):\n");
        foreach (var m in milestones)
        {
            var outcome = m.Outcome ?? "(no outcome set)";
            sb.Append($"- {m.Title} [{m.Status}] — {outcome}\n");
        }
        return new ChatToolResult(sb.ToString());
    }

    // ── reminders (durable one-shot self re-invocation) ────────────────

    // ── project memory ────────────────────────────────────────────────

    private sealed record RememberArgs(string Title, string Content, string? Type = null, string? Scope = null);
    private sealed record RecallArgs(string Query, int? Limit = null);

    /// <summary>Saves a memory the agent judged durable — team-wide unless it asked for a
    /// private note. Uses the same facade the pipeline tools and the Memory page write through.</summary>
    private async Task<ChatToolResult> RememberAsync(IServiceProvider services, JsonElement arguments, CancellationToken ct)
    {
        var args = arguments.Deserialize<RememberArgs>(JsonOptions);
        var title = args?.Title?.Trim();
        var content = args?.Content?.Trim();
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(content))
            return Error("remember needs a short title and the content to keep.");
        if (!Enum.TryParse<MemoryType>(args!.Type ?? "Learning", true, out var type))
            return Error("type must be one of Decision, Convention, Preference, Learning, Mistake, Pattern.");
        var agentScoped = string.Equals(args.Scope, "agent", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(args.Scope, "private", StringComparison.OrdinalIgnoreCase);

        var agentsApi = services.GetRequiredService<IAgentsApi>();
        var id = await agentsApi.AddMemoryAsync(new NewMemory(
            context.OrgId, context.ProjectId, agentScoped ? context.AgentId : null,
            type, title, content, MemorySource.Agent,
            CreatedByUserId: null, TaskId: null, ExecutionId: null,
            DedupeKey: null), ct);
        if (id == Ulid.Empty)
            return Error("This project's memory is full — ask a human to archive old entries on the Memory page.");
        return new ChatToolResult($"Remembered ({type}, {(agentScoped ? "private to you" : "shared with the team")}) — id {id}. It will be retrieved for future work on matching topics.");
    }

    private async Task<ChatToolResult> RecallAsync(IServiceProvider services, JsonElement arguments, CancellationToken ct)
    {
        var args = arguments.Deserialize<RecallArgs>(JsonOptions);
        var query = args?.Query?.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return Error("recall needs a query — a topic, file, feature or decision to look up.");
        var agentsApi = services.GetRequiredService<IAgentsApi>();
        var items = await agentsApi.RetrieveMemoriesAsync(
            new MemoryQuery(context.OrgId, context.ProjectId, context.AgentId, query, Take: Math.Clamp(args!.Limit ?? 10, 1, 30), CharBudget: 6_000), ct);
        if (items.Count == 0)
            return new ChatToolResult("Nothing in the project memory matches that. If you learn the answer, save it with remember.");
        var sb = new StringBuilder();
        sb.AppendLine($"{items.Count} memories (most relevant first):");
        foreach (var m in items)
            sb.AppendLine($"- [{m.Type}{(m.IsPinned ? ", pinned" : "")}{(m.AgentId is null ? "" : ", yours")}] {m.Title}: {m.Content}");
        return new ChatToolResult(sb.ToString());
    }

    private sealed record SetReminderArgs(
        string Message,
        [property: System.Text.Json.Serialization.JsonPropertyName("delay_seconds")] int? DelaySeconds = null,
        [property: System.Text.Json.Serialization.JsonPropertyName("fire_at")] string? FireAt = null);
    private sealed record CancelReminderArgs(
        [property: System.Text.Json.Serialization.JsonPropertyName("reminder_id")] string ReminderId);

    private async Task<ChatToolResult> SetReminderAsync(
        IServiceProvider services, JsonElement arguments, CancellationToken ct)
    {
        var args = arguments.Deserialize<SetReminderArgs>(JsonOptions);
        var message = args?.Message?.Trim();
        if (string.IsNullOrWhiteSpace(message))
            return Error("A reminder needs a message — what should you be told when it fires?");
        if (message.Length > Schedules.ScheduleRules.MessageMax)
            return Error($"The message must be at most {Schedules.ScheduleRules.MessageMax} characters.");

        var now = DateTimeOffset.UtcNow;
        DateTimeOffset fireAt;
        if (args!.DelaySeconds is { } delay)
            fireAt = now.AddSeconds(delay);
        else if (!string.IsNullOrWhiteSpace(args.FireAt) && DateTimeOffset.TryParse(args.FireAt, null,
                     System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed))
            fireAt = parsed;
        else
            return Error("Provide delay_seconds (relative) or fire_at (ISO 8601 timestamp).");

        var delta = fireAt - now;
        if (delta < Schedules.ScheduleRules.MinReminderDelay)
            return Error($"A reminder must be at least {Schedules.ScheduleRules.MinReminderDelay.TotalSeconds:0} seconds in the future.");
        if (delta > Schedules.ScheduleRules.MaxReminderDelay)
            return Error($"A reminder can be at most {Schedules.ScheduleRules.MaxReminderDelay.TotalDays:0} days out.");

        var db = services.GetRequiredService<Orkka.Modules.Agents.Infrastructure.AgentsDbContext>();
        var pending = await db.AgentSchedules.IgnoreQueryFilters()
            .CountAsync(r => r.AgentId == context.AgentId && r.Kind == ScheduleKind.Reminder && r.IsEnabled && r.NextRunAt != null, ct);
        if (pending >= Schedules.ScheduleRules.MaxPendingRemindersPerAgent)
            return Error($"You already have {pending} pending reminders (max {Schedules.ScheduleRules.MaxPendingRemindersPerAgent}). Cancel one first.");
        var today = await db.AgentSchedules.IgnoreQueryFilters()
            .CountAsync(r => r.AgentId == context.AgentId && r.Kind == ScheduleKind.Reminder && r.CreatedAt >= now.AddDays(-1), ct);
        if (today >= Schedules.ScheduleRules.MaxRemindersPerAgentPerDay)
            return Error("Daily reminder limit reached for this agent.");

        var reminder = new Orkka.Modules.Agents.Domain.AgentSchedule
        {
            Id = Ulid.NewUlid(),
            OrgId = context.OrgId,
            ProjectId = context.ProjectId,
            Name = message.Length > 60 ? message[..60] + "…" : message,
            Kind = ScheduleKind.Reminder,
            IsEnabled = true,
            FireAt = fireAt,
            NextRunAt = fireAt,
            TimeZoneId = "UTC",
            AgentId = context.AgentId,
            ConversationId = context.ConversationId,
            Message = message,
            CreatedByUserId = context.RequesterUserId,
            RequesterLocale = context.RequesterLocale,
        };
        db.AgentSchedules.Add(reminder);
        await db.SaveChangesAsync(ct);

        return new ChatToolResult(
            $"Reminder set (id {reminder.Id}). You will be re-invoked in this conversation at {fireAt:yyyy-MM-dd HH:mm} UTC " +
            $"({FormatDelta(delta)} from now) with: \"{message}\"");
    }

    private async Task<ChatToolResult> ListMyRemindersAsync(IServiceProvider services, CancellationToken ct)
    {
        var db = services.GetRequiredService<Orkka.Modules.Agents.Infrastructure.AgentsDbContext>();
        var reminders = await db.AgentSchedules.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.AgentId == context.AgentId && r.Kind == ScheduleKind.Reminder && r.IsEnabled && r.NextRunAt != null)
            .OrderBy(r => r.NextRunAt)
            .ToListAsync(ct);
        if (reminders.Count == 0)
            return new ChatToolResult("You have no pending reminders.");
        var sb = new StringBuilder("Pending reminders:\n");
        foreach (var r in reminders)
            sb.Append($"- {r.Id} — fires {r.NextRunAt:yyyy-MM-dd HH:mm} UTC: {r.Message}\n");
        return new ChatToolResult(sb.ToString());
    }

    private async Task<ChatToolResult> CancelReminderAsync(
        IServiceProvider services, JsonElement arguments, CancellationToken ct)
    {
        var args = arguments.Deserialize<CancelReminderArgs>(JsonOptions);
        if (args is null || !Ulid.TryParse(args.ReminderId, out var id))
            return Error("reminder_id must be a valid id from list_my_reminders.");
        var db = services.GetRequiredService<Orkka.Modules.Agents.Infrastructure.AgentsDbContext>();
        var reminder = await db.AgentSchedules.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == id && r.AgentId == context.AgentId && r.Kind == ScheduleKind.Reminder, ct);
        if (reminder is null)
            return Error("No such reminder for this agent.");
        db.AgentSchedules.Remove(reminder);
        await db.SaveChangesAsync(ct);
        return new ChatToolResult("Reminder cancelled.");
    }

    private static string FormatDelta(TimeSpan delta) =>
        delta.TotalHours >= 24 ? $"{delta.TotalDays:0.#} days"
        : delta.TotalMinutes >= 60 ? $"{delta.TotalHours:0.#} hours"
        : $"{delta.TotalMinutes:0} minutes";

    private async Task<ChatToolResult> CreateMilestoneAsync(
        IServiceProvider services, JsonElement arguments, CancellationToken ct)
    {
        var block = JsonSerializer.Deserialize<ChatMilestoneArgs>(arguments.GetRawText(), JsonOptions);
        if (string.IsNullOrWhiteSpace(block?.Title))
            return Error("Provide a \"title\".");

        var tasksApi = services.GetRequiredService<ITasksApi>();
        var milestone = await tasksApi.CreateMilestoneForChatAsync(
            context.OrgId, context.ProjectId,
            Truncate(block.Title, 200),
            block.Outcome is null ? null : Truncate(block.Outcome, 2000),
            context.RequesterUserId, ct);

        return new ChatToolResult(
            $"Milestone \"{milestone.Title}\" is on the roadmap. " +
            "Pass this exact title as \"milestone\" on create_tasks to file work under it.");
    }

    private sealed record ChatMilestoneArgs(string? Title, string? Outcome);

    private async Task<ChatToolResult> ListTasksAsync(IServiceProvider services, CancellationToken ct)
    {
        var tasksApi = services.GetRequiredService<ITasksApi>();
        var tasks = await tasksApi.ListProjectTaskSummariesAsync(context.OrgId, context.ProjectId, ct: ct);

        if (tasks.Count == 0)
            return new ChatToolResult("The project board has no tasks yet.");

        var sb = new StringBuilder($"{tasks.Count} task(s) on the board (newest first):\n");
        foreach (var task in tasks)
            sb.Append($"- #{task.Number} [{task.CurrentLane}/{task.Status}, {task.Priority} {task.Type}] {task.Title} (id: {task.Id})\n");
        return new ChatToolResult(sb.ToString());
    }

    private async Task<ChatToolResult> GetTaskAsync(
        IServiceProvider services, JsonElement arguments, CancellationToken ct)
    {
        if (!arguments.TryGetProperty("taskId", out var idEl) ||
            !Ulid.TryParse(idEl.GetString(), out var taskId))
        {
            return Error("Provide \"taskId\" — a task id from list_tasks.");
        }

        var tasksApi = services.GetRequiredService<ITasksApi>();
        var task = await tasksApi.GetTaskSnapshotAsync(taskId, ct);

        // The snapshot read is worker-surface (unscoped) — enforce this turn's tenant here.
        if (task is null || task.OrgId != context.OrgId || task.ProjectId != context.ProjectId)
            return Error("No task with that id exists in this project.");

        var sb = new StringBuilder();
        sb.Append($"# Task #{task.Number}: {task.Title}\n");
        sb.Append($"id: {task.Id}\nlane: {task.CurrentLane}\nstatus: {task.Status}\n");
        sb.Append($"priority: {task.Priority}\ntype: {task.Type}\n");
        if (task.IsArchived) sb.Append("archived: true\n");
        if (!string.IsNullOrWhiteSpace(task.Tags)) sb.Append($"tags: {task.Tags}\n");
        if (!string.IsNullOrWhiteSpace(task.BranchName)) sb.Append($"branch: {task.BranchName}\n");
        if (!string.IsNullOrWhiteSpace(task.PrUrl)) sb.Append($"pr: {task.PrUrl}\n");
        sb.Append('\n');
        sb.Append(string.IsNullOrWhiteSpace(task.Description)
            ? "(no description)"
            : Truncate(task.Description, GetTaskDescriptionCap));
        return new ChatToolResult(sb.ToString());
    }

    private async Task<ChatToolResult> ListMyTasksAsync(IServiceProvider services, CancellationToken ct)
    {
        var tasksApi = services.GetRequiredService<ITasksApi>();
        var tasks = await tasksApi.ListAgentTaskSummariesAsync(
            context.OrgId, context.ProjectId, context.AgentId, ct: ct);

        if (tasks.Count == 0)
            return new ChatToolResult("You have not worked on any tasks in this project yet.");

        var sb = new StringBuilder($"{tasks.Count} task(s) you worked on (most recent first):\n");
        foreach (var task in tasks)
        {
            sb.Append($"- #{task.Number} [{task.CurrentLane}/{task.Status}, {task.Priority} {task.Type}] {task.Title}");
            sb.Append(task.ExecutionCount > 0
                ? $" — {task.ExecutionCount} run(s) by you ({string.Join(", ", task.LanesWorked)})"
                : " — assigned to you, no runs yet");
            if (task.LastWorkedAt is { } last)
                sb.Append($", last worked {last:yyyy-MM-dd}");
            sb.Append($" (id: {task.Id})\n");
        }
        sb.Append("Use get_task for a task's details, or get_task_history for its phase-by-phase timeline.");
        return new ChatToolResult(sb.ToString());
    }

    private async Task<ChatToolResult> GetTaskHistoryAsync(
        IServiceProvider services, JsonElement arguments, CancellationToken ct)
    {
        if (!arguments.TryGetProperty("taskId", out var idEl) ||
            !Ulid.TryParse(idEl.GetString(), out var taskId))
        {
            return Error("Provide \"taskId\" — a task id from list_tasks or list_my_tasks.");
        }

        var tasksApi = services.GetRequiredService<ITasksApi>();
        var executions = await tasksApi.ListTaskExecutionSummariesAsync(
            context.OrgId, context.ProjectId, taskId, ct);

        if (executions is null)
            return Error("No task with that id exists in this project.");
        if (executions.Count == 0)
            return new ChatToolResult("No agent has run a phase on this task yet.");

        var agentIds = executions
            .Where(e => e.AgentId is not null)
            .Select(e => e.AgentId!.Value)
            .Distinct()
            .ToList();
        var names = agentIds.Count == 0
            ? new Dictionary<Ulid, string>()
            : await services.GetRequiredService<IAgentsApi>().GetAgentNamesAsync(agentIds, ct);

        var sb = new StringBuilder($"{executions.Count} execution(s) on this task, oldest first:\n");
        var index = 0;
        foreach (var execution in executions)
        {
            index++;
            var who = execution.AgentId is { } executorId
                ? (names.TryGetValue(executorId, out var name) ? name : "removed agent") +
                  (executorId == context.AgentId ? " (you)" : "")
                : "system";
            sb.Append($"{index}. {execution.Lane} — {who} — {execution.Status}");
            if (execution.StartedAt is { } started) sb.Append($", started {started:yyyy-MM-dd HH:mm}");
            if (execution.CompletedAt is { } completed) sb.Append($", finished {completed:yyyy-MM-dd HH:mm}");
            if (execution.TokensUsed > 0) sb.Append($", {execution.TokensUsed} tokens");
            sb.Append('\n');
            if (!string.IsNullOrWhiteSpace(execution.ErrorMessage))
                sb.Append($"   error: {Truncate(execution.ErrorMessage, 300)}\n");
            if (!string.IsNullOrWhiteSpace(execution.FilesChanged))
                sb.Append($"   files changed: {Truncate(execution.FilesChanged, 1000)}\n");
        }
        return new ChatToolResult(sb.ToString());
    }

    /// <summary>
    /// What the project already looks like. Ordered brand-first, so when the list is capped it
    /// is the assets that define the identity that survive the cap.
    /// </summary>
    private async Task<ChatToolResult> ListDesignAssetsAsync(
        IServiceProvider services, JsonElement arguments, CancellationToken ct)
    {
        var db = services.GetRequiredService<AgentsDbContext>();

        var folder = arguments.TryGetProperty("folder", out var f) ? f.GetString() : null;
        var kind = arguments.TryGetProperty("kind", out var k) ? k.GetString() : null;
        var limit = arguments.TryGetProperty("limit", out var l) && l.TryGetInt32(out var n)
            ? Math.Clamp(n, 1, 100)
            : 40;

        var query = db.DesignAssets.AsNoTracking()
            .Where(a => a.OrgId == context.OrgId && a.ProjectId == context.ProjectId);

        if (!string.IsNullOrWhiteSpace(folder))
        {
            var path = folder.Trim().Trim('/');
            var prefix = path + "/";
            query = query.Where(a => a.FolderPath == path || a.FolderPath.StartsWith(prefix));
        }
        if (Enum.TryParse<DesignAssetKind>(kind, ignoreCase: true, out var parsedKind))
            query = query.Where(a => a.Kind == parsedKind);

        // Over-fetch, then collapse: the cap counts assets, not versions.
        var rows = await query
            .OrderByDescending(a => a.FolderPath.StartsWith("brand"))
            .ThenByDescending(a => a.Status == DesignAssetStatus.Approved)
            .ThenByDescending(a => a.CreatedAt)
            .Take(limit * 4)
            .ToListAsync(ct);

        var latest = rows
            .GroupBy(a => a.RootAssetId)
            .Select(g => g.OrderByDescending(a => a.Version).First())
            .Take(limit)
            .ToList();

        if (latest.Count == 0)
            return new ChatToolResult("This project has no design assets yet.");

        var sb = new StringBuilder($"{latest.Count} existing design asset(s):\n");
        foreach (var a in latest)
        {
            var where = string.IsNullOrEmpty(a.FolderPath) ? "(root)" : a.FolderPath;
            sb.Append($"- {a.Id} · {a.Name} · {a.Kind.ToString().ToLowerInvariant()} · {where} · "
                + $"v{a.Version} · {a.Status}\n");
        }
        sb.Append("\nRead one with get_design_asset before creating anything like it.");
        return new ChatToolResult(sb.ToString());
    }

    /// <summary>The largest SVG worth putting in a tool result: ample for any mark or icon,
    /// while a whole illustration would crowd out the conversation it exists to inform.</summary>
    private const int MaxInlinedSvgChars = 40_000;

    private async Task<ChatToolResult> GetDesignAssetAsync(
        IServiceProvider services, JsonElement arguments, CancellationToken ct)
    {
        if (!arguments.TryGetProperty("id", out var idProp)
            || !Ulid.TryParse(idProp.GetString(), out var assetId))
            return Error("\"id\" must be an asset id from list_design_assets.");

        var db = services.GetRequiredService<AgentsDbContext>();
        var asset = await db.DesignAssets.AsNoTracking().FirstOrDefaultAsync(
            a => a.Id == assetId && a.OrgId == context.OrgId && a.ProjectId == context.ProjectId,
            ct);

        if (asset is null)
            return Error("No such design asset in this project.");

        var where = string.IsNullOrEmpty(asset.FolderPath) ? "(root)" : asset.FolderPath;
        var sb = new StringBuilder(
            $"{asset.Name} · {asset.Kind.ToString().ToLowerInvariant()} · {where} · "
            + $"v{asset.Version} · {asset.Status}\n");
        if (!string.IsNullOrWhiteSpace(asset.Prompt))
            sb.Append($"\nGenerated from:\n{asset.Prompt}\n");

        if (asset.Kind == DesignAssetKind.Svg)
        {
            var blobStorage = services.GetRequiredService<IBlobStorageService>();
            await using var stream = await blobStorage.DownloadAsync(
                asset.BlobContainer, asset.BlobName, ct);
            if (stream is null)
            {
                sb.Append("\nIts markup could not be read from storage.");
            }
            else
            {
                using var reader = new StreamReader(stream);
                var svg = await reader.ReadToEndAsync(ct);
                sb.Append(svg.Length > MaxInlinedSvgChars
                    ? $"\nIts markup is {svg.Length} characters - too large to inline. Work from "
                        + "the description above and the brand palette."
                    : $"\nMarkup (reuse this geometry exactly):\n{svg}");
            }
        }
        else
        {
            // A raster asset's pixels cannot be shown to the model here. Say so, rather than
            // let it treat the description above as the whole picture.
            sb.Append("\nThis is a raster asset - its pixels are not readable here. Match it "
                + "through the prompt above and the brand palette, or reuse an SVG version if "
                + "the project has one.");
        }

        return new ChatToolResult(sb.ToString());
    }

    private async Task<ChatToolResult> ListReposAsync(IServiceProvider services, CancellationToken ct)
    {
        var projectsApi = services.GetRequiredService<IProjectsApi>();
        var repos = (await projectsApi.ListReposAsync(context.ProjectId, ct))
            .Where(r => r.OrgId == context.OrgId)
            .ToList();

        if (repos.Count == 0)
            return new ChatToolResult("This project has no repositories linked.");

        var sb = new StringBuilder($"{repos.Count} repository/repositories in the project:\n");
        foreach (var repo in repos)
            sb.Append($"- {repo.Name} (branch: {repo.DefaultBranch}, workspace: {repo.WorkspaceStatus})\n");
        return new ChatToolResult(sb.ToString());
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Diagrams (Solution Architect)
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<ChatToolResult> CreateDiagramAsync(
        IServiceProvider services, JsonElement arguments, CancellationToken ct)
    {
        // Stored verbatim and expanded client-side, like the fenced-block path — backfill
        // the envelope fields the renderer keys on so the model only supplies name+elements.
        var node = JsonNode.Parse(arguments.GetRawText())?.AsObject();
        if (node is null)
            return Error("Arguments must be a JSON object with \"name\" and \"elements\".");
        node["type"] ??= "excalidraw";
        node["version"] ??= 2;
        node["source"] ??= "orkka-archie-skeleton";
        var diagramJson = node.ToJsonString();

        using var doc = JsonDocument.Parse(diagramJson);
        if (!DiagramBlockParser.TryCreateBlock(doc.RootElement, diagramJson, out var block, out var reason))
            return Error($"Invalid diagram: {reason}.");

        // The diagram's MessageId is a real FK — the streamed reply's row must exist first.
        await EnsureAssistantMessageRowAsync(services, ct);

        var db = services.GetRequiredService<AgentsDbContext>();
        var diagram = new ArchitectureDiagram
        {
            Id = Ulid.NewUlid(),
            OrgId = context.OrgId,
            ProjectId = context.ProjectId,
            AgentId = context.AgentId,
            ConversationId = context.ConversationId,
            MessageId = context.AssistantMessageId,
            Title = block!.Title,
            Type = DiagramType.SystemArchitecture,
            DiagramData = block.DiagramJson,
        };
        db.ArchitectureDiagrams.Add(diagram);
        db.ArchitectureDiagramVersions.Add(DiagramSnapshots.FromCurrentState(
            diagram, changedByUserId: null, changedByAgentId: context.AgentId,
            requestedByUserId: context.RequesterUserId,
            sourceConversationId: context.ConversationId,
            sourceMessageId: context.AssistantMessageId));
        await db.SaveChangesAsync(ct);

        context.AddCreatedDiagrams(1);
        context.Emit(new { diagram = new { id = diagram.Id.ToString(), title = diagram.Title } });

        return new ChatToolResult(
            $"Created diagram \"{diagram.Title}\" (id: {diagram.Id}) on the Architecture Board. " +
            "The chat links it automatically — confirm briefly, do not invent URLs.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Design assets (Designer / GameDesigner)
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<ChatToolResult> CreateDesignAssetsAsync(
        IServiceProvider services, JsonElement arguments, CancellationToken ct)
    {
        // Lenient parse (shared with the fenced path): known schema drift — numeric
        // "frames" counts, "fps", pixel "width"/"height" — is normalized instead of
        // failing the whole call.
        var block = DesignBlockParser.ParseBlockJson(arguments.GetRawText(), out var diagnostics);
        if (block?.Assets is not { Count: > 0 })
            return Error(diagnostics ?? "Provide at least one asset in \"assets\".");

        // Design assets reference the message like diagrams do — ensure the row exists.
        await EnsureAssistantMessageRowAsync(services, ct);

        var generator = services.GetRequiredService<DesignAssetGenerator>();
        var outcomes = new StringBuilder();
        var readyCount = 0;
        var failedCount = 0;

        // Progress events drive the chat's "creating designs" indicator exactly like the
        // post-stream path. modelCanRetry: the calling agent is still mid-turn, so a non-SVG
        // request comes back to it as an instruction to author the SVG itself — never a
        // nested call (the runner is busy with this very turn and would deadlock).
        await foreach (var evt in generator.GenerateAsync(
            context.OrgId, context.ProjectId, context.AgentId, context.ConversationId,
            context.AssistantMessageId, context.RequesterUserId, block.Assets,
            modelCanRetry: true, context.DesignFolderPath, ct))
        {
            if (DesignEventSse.ToPayload(evt) is { } payload)
                context.Emit(payload);

            switch (evt)
            {
                case DesignAssetReady ready:
                    readyCount++;
                    outcomes.Append($"- \"{ready.Asset.Name}\" ({ready.Asset.Kind.ToString().ToLowerInvariant()}, v{ready.Asset.Version}, id: {ready.Asset.Id}) — ready\n");
                    break;
                case DesignAssetFailed failed:
                    failedCount++;
                    outcomes.Append($"- \"{failed.Name}\" ({failed.Kind}) — FAILED: {Truncate(failed.Error, 300)}\n");
                    break;
            }
        }

        context.AddCreatedDesignAssets(readyCount);

        var summary = $"{readyCount} asset(s) ready, {failedCount} failed:\n{outcomes}";
        if (readyCount > 0)
            summary += "Ready assets are on the project's Design page for approval and linked in the chat — confirm briefly, do not invent URLs.";
        // All-failed runs surface as errors so the model reacts (fix the spec, or tell the user).
        return new ChatToolResult(summary, IsError: readyCount == 0);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Tool definitions
    // ─────────────────────────────────────────────────────────────────────────

    private static readonly ChatToolDefinition CreateDocumentTool = new(
        "create_document",
        "Save one or more markdown documents to this project's Documents library — the team's " +
        "persistent knowledge base for decisions, bug post-mortems, technical explanations, and " +
        "how-tos. Documents are saved immediately as final: write polished, complete, " +
        "self-contained reference markdown. Saving a title+path that already exists revises " +
        "that document in place as a new version — reuse the exact title to update a document, " +
        "pick a new title for a separate one.",
        """
        {
          "type": "object",
          "properties": {
            "documents": {
              "type": "array",
              "minItems": 1,
              "maxItems": 5,
              "items": {
                "type": "object",
                "properties": {
                  "title": { "type": "string", "description": "Short descriptive title, max 200 characters, no slashes." },
                  "path": { "type": "string", "description": "Folder path like \"decisions\" or \"bugs/backend\": lowercase segments separated by \"/\", no leading/trailing slash, no \"..\". Omit or use \"\" for the root folder. Reuse existing folders when one fits (check with list_documents)." },
                  "content": { "type": "string", "description": "Complete, self-contained, reference-quality markdown: context, the decision/answer itself, and relevant code snippets." }
                },
                "required": ["title", "content"]
              }
            }
          },
          "required": ["documents"]
        }
        """);

    private static readonly ChatToolDefinition ListDocumentsTool = new(
        "list_documents",
        "List every document in this project's Documents library (id, folder path, title, " +
        "version, last update). Use it to find existing folders, avoid duplicate documents, or " +
        "locate a document to read with get_document.",
        """{ "type": "object", "properties": {} }""");

    private static readonly ChatToolDefinition GetDocumentTool = new(
        "get_document",
        "Read the full markdown content of one project document by id (ids come from list_documents).",
        """
        {
          "type": "object",
          "properties": {
            "documentId": { "type": "string", "description": "The document's id from list_documents." }
          },
          "required": ["documentId"]
        }
        """);

    private static readonly ChatToolDefinition CreateTasksTool = new(
        "create_tasks",
        "Publish 1-8 tasks to the project board (Drafting lane). Only call this when the user " +
        "explicitly asked for a task breakdown or task creation — never for regular discussion. " +
        "Order tasks by dependency (foundational work first). Each description must be fully " +
        "self-contained: a dev agent executes it without reading this conversation.",
        """
        {
          "type": "object",
          "properties": {
            "tasks": {
              "type": "array",
              "minItems": 1,
              "maxItems": 8,
              "items": {
                "type": "object",
                "properties": {
                  "title": { "type": "string", "description": "Short imperative title." },
                  "description": { "type": "string", "description": "Self-contained markdown: ## Context, ## What to build, ## Acceptance Criteria (checklist), ## Suggested files. Include edge cases discussed in chat." },
                  "priority": { "type": "string", "enum": ["Low", "Medium", "High", "Critical"] },
                  "type": { "type": "string", "enum": ["Feature", "BugFix", "Refactor", "Improvement"] },
                  "repo": { "type": "string", "description": "Must EXACTLY match one of the project's repository names (see Available Repositories or list_repos) — the repo where the work happens." },
                  "milestone": { "type": "string", "description": "Optional. Must EXACTLY match a milestone title from list_milestones. Omit to leave the task in the Backlog — do not guess." },
                  "tags": { "type": "array", "items": { "type": "string" } },
                  "contextFiles": { "type": "array", "items": { "type": "string" }, "description": "Repo-relative paths you actually found in the repo — never invent paths." }
                },
                "required": ["title", "description"]
              }
            }
          },
          "required": ["tasks"]
        }
        """);

    private static readonly ChatToolDefinition ListMilestonesTool = new(
        "list_milestones",
        "List the project's milestones (title, outcome, status). Call this before create_tasks " +
        "when the user is planning toward a goal, so tasks land under the right outcome instead " +
        "of in the Backlog.",
        """{ "type": "object", "properties": {} }""");

    private static readonly ChatToolDefinition CreateMilestoneTool = new(
        "create_milestone",
        "Create a milestone — an outcome a group of tasks works toward. Only call this when the " +
        "user is planning a body of work that does not fit an existing milestone; check " +
        "list_milestones first. Creating one twice with the same title returns the existing one.",
        """
        {
          "type": "object",
          "properties": {
            "title": { "type": "string", "description": "Names an outcome, not an activity — 'Subscription billing', not 'Do billing work'." },
            "outcome": { "type": "string", "description": "What is TRUE when this is done, in one or two sentences a non-engineer can verify." }
          },
          "required": ["title"]
        }
        """);

    private static readonly ChatToolDefinition ListTasksTool = new(
        "list_tasks",
        "List the project board's tasks (number, lane, status, priority, type, title, id), " +
        "newest first. Use it to reference existing work or avoid duplicate tasks.",
        """{ "type": "object", "properties": {} }""");

    private static readonly ChatToolDefinition GetTaskTool = new(
        "get_task",
        "Read one task's full details — including its description — by id (ids come from list_tasks).",
        """
        {
          "type": "object",
          "properties": {
            "taskId": { "type": "string", "description": "The task's id from list_tasks." }
          },
          "required": ["taskId"]
        }
        """);

    private static readonly ChatToolDefinition ListMyTasksTool = new(
        "list_my_tasks",
        "List the tasks YOU worked on in this project — every task where you ran a pipeline " +
        "phase (planning, developing, testing, review...) or that is assigned to you, most " +
        "recently worked first. Use it when the user asks about your work, your history, or " +
        "what you have been doing.",
        """{ "type": "object", "properties": {} }""");

    private static readonly ChatToolDefinition GetTaskHistoryTool = new(
        "get_task_history",
        "Read one task's execution timeline: which agent ran which pipeline phase, when, " +
        "its outcome (completed/failed), token usage, and files changed. Ids come from " +
        "list_tasks or list_my_tasks. Combine with get_task for the task's description and " +
        "current state.",
        """
        {
          "type": "object",
          "properties": {
            "taskId": { "type": "string", "description": "The task's id from list_tasks or list_my_tasks." }
          },
          "required": ["taskId"]
        }
        """);

    private static readonly ChatToolDefinition SetReminderTool = new(
        "set_reminder",
        "Schedule a one-shot reminder for YOURSELF: at the given time you will be re-invoked in " +
        "this conversation with your message as the input, so you can follow up on something " +
        "(check a deployment later, re-ask a stakeholder tomorrow, revisit a decision after a " +
        "sleep). Between 60 seconds and 30 days out. Not for recurring work — a human sets " +
        "schedules for that on the Automation page.",
        """
        {
          "type": "object",
          "properties": {
            "message": { "type": "string", "description": "What to tell yourself when it fires — include the context you will need." },
            "delay_seconds": { "type": "integer", "description": "Relative delay from now, in seconds (e.g. 7200 for two hours)." },
            "fire_at": { "type": "string", "description": "Absolute ISO 8601 UTC timestamp, e.g. 2026-08-18T09:00:00Z. Use either this or delay_seconds." }
          },
          "required": ["message"]
        }
        """);

    private static readonly ChatToolDefinition RememberTool = new(
        "remember",
        "Save something durable to the PROJECT MEMORY so every future run and chat — yours and " +
        "your teammates' — can use it: a decision and its reason, a convention of this codebase, " +
        "a preference the humans expressed, a learning, or a mistake to avoid. Keep it short (a " +
        "title + 1–3 sentences). Not for task progress or transient facts. Memories are shown to " +
        "agents by relevance and are visible/editable by humans on the Memory page.",
        """
        {
          "type": "object",
          "properties": {
            "title": { "type": "string", "description": "One line: what this is about (≤ 200 chars)." },
            "content": { "type": "string", "description": "The memory itself, 1–3 sentences (≤ 4000 chars). Include the WHY for decisions." },
            "type": { "type": "string", "enum": ["Decision", "Convention", "Preference", "Learning", "Mistake", "Pattern"], "description": "Default Learning. Decision/Convention/Preference are always shown to every agent; use them for the project's standing rules." },
            "scope": { "type": "string", "enum": ["team", "agent"], "description": "Default team (every agent sees it). agent = only you." }
          },
          "required": ["title", "content"]
        }
        """);

    private static readonly ChatToolDefinition RecallTool = new(
        "recall",
        "Search the project memory (decisions, conventions, learnings, mistakes) for a topic before " +
        "you make a choice the team may already have made or repeat work someone already learned from.",
        """
        {
          "type": "object",
          "properties": {
            "query": { "type": "string", "description": "Topic, feature, file, library or question — a few content words work best." },
            "limit": { "type": "integer", "description": "Max results (default 10, max 30)." }
          },
          "required": ["query"]
        }
        """);

    private static readonly ChatToolDefinition ListMyRemindersTool = new(
        "list_my_reminders",
        "List your pending reminders (id, fire time, message).",
        """{ "type": "object", "properties": {} }""");

    private static readonly ChatToolDefinition CancelReminderTool = new(
        "cancel_reminder",
        "Cancel one of your pending reminders by id.",
        """
        {
          "type": "object",
          "properties": { "reminder_id": { "type": "string" } },
          "required": ["reminder_id"]
        }
        """);

    private static readonly ChatToolDefinition ListDesignAssetsTool = new(
        "list_design_assets",
        "List design assets that already exist in this project - the brand marks, icons and "
        + "images on the Design page. Call this BEFORE creating anything: an asset that already "
        + "exists is reused or built on, never re-imagined from its description. Assets under "
        + "brand/ are the project's identity.",
        """
        {
          "type": "object",
          "properties": {
            "folder": { "type": "string", "description": "Only assets in this folder and below (e.g. brand). Omit for all." },
            "kind": { "type": "string", "enum": ["svg", "image", "gif"], "description": "Only this kind. Omit for all." },
            "limit": { "type": "integer", "description": "Max assets to return (default 40)." }
          }
        }
        """);

    private static readonly ChatToolDefinition GetDesignAssetTool = new(
        "get_design_asset",
        "Read one existing design asset. For an SVG this returns its actual markup - reuse the "
        + "real path data when you build on a mark, rather than drawing it again from memory, "
        + "which never reproduces it. For a raster asset it returns the prompt it came from, so "
        + "a new one stays consistent with it.",
        """
        {
          "type": "object",
          "properties": {
            "id": { "type": "string", "description": "Asset id from list_design_assets." }
          },
          "required": ["id"]
        }
        """);

    private static readonly ChatToolDefinition ListReposTool = new(
        "list_repos",
        "List the project's linked repositories (name, default branch, workspace status). Repo " +
        "names are what create_tasks' \"repo\" field must match.",
        """{ "type": "object", "properties": {} }""");

    private static readonly ChatToolDefinition CreateDiagramTool = new(
        "create_diagram",
        "Create an architecture diagram on the project's Architecture Board from a compact " +
        "element skeleton (the platform expands it into a full Excalidraw scene — never supply " +
        "Excalidraw scene fields like seed/versionNonce/boundElements/appState). Layout on a " +
        "roughly 220x140px grid per node: left-to-right for request flow, top-to-bottom for " +
        "layered architecture. Max 15-20 elements; call again for additional diagrams.",
        """
        {
          "type": "object",
          "properties": {
            "name": { "type": "string", "description": "Short diagram title, shown on the Architecture Board." },
            "elements": {
              "type": "array",
              "minItems": 1,
              "description": "Skeleton elements. Shapes (rectangle/ellipse/diamond) need id, x, y, width, height; optional backgroundColor, strokeColor, strokeStyle (solid|dashed|dotted), label:{text}. Colors: #a5d8ff primary services, #d0bfff secondary services, #b2f2bb databases/data stores, #ffec99 gateways/load balancers/decision points; dashed strokeStyle for network boundaries. Connect shapes with arrows via start:{id} and end:{id} (connecting arrows need NO x/y/width/height); label arrows with the protocol (HTTP, gRPC, SQL...). Standalone text: {type:\"text\", x, y, text, fontSize}.",
              "items": {
                "type": "object",
                "properties": {
                  "type": { "type": "string", "enum": ["rectangle", "ellipse", "diamond", "arrow", "line", "text"] },
                  "id": { "type": "string" },
                  "x": { "type": "number" },
                  "y": { "type": "number" },
                  "width": { "type": "number" },
                  "height": { "type": "number" },
                  "backgroundColor": { "type": "string" },
                  "strokeColor": { "type": "string" },
                  "strokeStyle": { "type": "string", "enum": ["solid", "dashed", "dotted"] },
                  "label": { "type": "object", "properties": { "text": { "type": "string" } } },
                  "start": { "type": "object", "properties": { "id": { "type": "string" } } },
                  "end": { "type": "object", "properties": { "id": { "type": "string" } } },
                  "text": { "type": "string" },
                  "fontSize": { "type": "number" }
                },
                "required": ["type"]
              }
            }
          },
          "required": ["name", "elements"]
        }
        """);

    private const string DesignAssetsSchema = """
        {
          "type": "object",
          "properties": {
            "assets": {
              "type": "array",
              "minItems": 1,
              "maxItems": 4,
              "items": {
                "type": "object",
                "properties": {
                  "kind": { "type": "string", "enum": ["svg"], "description": "Always svg: you author the complete markup yourself (logos, icons, illustrations, sprites, UI art; animated SVG via CSS/SMIL for motion)." },
                  "name": { "type": "string", "description": "Meaningful kebab-case name, e.g. logo-icon, hero-illustration." },
                  "description": { "type": "string", "description": "Why/what." },
                  "svg": { "type": "string", "description": "COMPLETE SVG markup, max 200 KB." },
                  "basedOnAssetId": { "type": "string", "description": "When iterating on an existing asset (listed under Design Assets Referenced In This Conversation or created earlier), its id — the new markup is saved as its next version. Send the complete updated SVG." }
                },
                "required": ["kind", "name", "svg"]
              }
            }
          },
          "required": ["assets"]
        }
        """;

    private static readonly ChatToolDefinition CreateDesignAssetsTool = new(
        "create_design_assets",
        "Create up to 4 SVG design assets (you author the complete markup). There is no raster " +
        "image or GIF generation — never promise photographic images, rendered mockups or GIFs; " +
        "offer an SVG instead (animated SVG via CSS/SMIL covers motion). Only call this when the " +
        "user asked for assets — never during discussion or scoping. Assets land on the " +
        "project's Design page for approval; failures come back per-asset so you can fix the " +
        "markup and retry.",
        DesignAssetsSchema);
}
