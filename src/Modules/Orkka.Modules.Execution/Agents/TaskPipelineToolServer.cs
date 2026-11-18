namespace Orkka.Modules.Execution.Agents;

using System.Text;
using System.Text.Json;
using ih0.Claude.Agent.SDK;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Documents.Contracts;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;

/// <summary>
/// The read-only "orkka" MCP tool server for pipeline task runs — how an agent reaches the
/// project's documentation, repositories, board and history ON DEMAND instead of the user
/// hand-attaching documents to every task. Titles are cheap to list; content is fetched only
/// when the agent decides it is relevant.
///
/// Deliberately a sibling of the chat stack's OrkkaChatToolServer rather than a reuse of it:
/// that server is conversation-shaped (assistant-message FKs, streamed emit callbacks, role-
/// gated CREATE tools), none of which a pipeline run has. Both delegate to the same module
/// facades, so the answers cannot drift — only the packaging differs. Read-only by
/// construction: a pipeline agent's writes go through its worktree and the pipeline itself.
///
/// One instance per run; tenant identity comes from the task snapshot at construction, never
/// from tool arguments. Handlers run on the SDK transport's read loop, so every call opens
/// its own DI scope.
/// </summary>
public sealed class TaskPipelineToolServer(
    Ulid orgId,
    Ulid projectId,
    Ulid taskId,
    Ulid? agentId,
    IServiceScopeFactory scopeFactory,
    ILogger logger) : ISdkMcpServer
{
    public const string ServerName = "orkka";

    private const int GetDocumentContentCap = 50_000;
    private const int GetTaskDescriptionCap = 10_000;

    public string Name => ServerName;

    public string Version => "1.0.0";

    public Task<IReadOnlyList<SdkMcpToolDefinition>> ListToolsAsync(
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<SdkMcpToolDefinition>>(
        [
            Tool("list_documents",
                "List every document in the project's Documents library — titles, paths and "
                + "ids only. Cheap; call it whenever project documentation might be relevant, "
                + "then get_document for the ones that are."),
            Tool("get_document",
                "Full markdown content of one project document by id (from list_documents).",
                ("documentId", "string", "Document id from list_documents.")),
            Tool("list_repos",
                "Every repository in this project — names and default branches. Your worktree "
                + "is ONE of them; this is how you learn what else exists."),
            Tool("list_tasks",
                "Every task on the project board — number, lane/status, title, id. How you "
                + "learn what was already built and what is in flight."),
            Tool("get_task",
                "One task's full details and description by id (from list_tasks).",
                ("taskId", "string", "Task id from list_tasks.")),
            Tool("get_task_history",
                "A task's phase-by-phase execution timeline: lane, outcome, files changed.",
                ("taskId", "string", "Task id from list_tasks.")),
            Tool("list_milestones",
                "The project roadmap — milestones in order, with outcomes and status."),
            Tool("recall",
                "Search the PROJECT MEMORY (decisions, conventions, preferences, learnings, past "
                + "mistakes recorded by the team and by agents) for a topic. Call it before choosing a "
                + "library, layout or approach the team may already have settled.",
                ("query", "string", "Topic, feature, file, library or question — a few content words.")),
            Tool("remember",
                "Save something durable to the project memory for every future run and teammate: a "
                + "decision and its reason, a convention of this codebase, a gotcha that cost you time. "
                + "Short: a title + 1–3 sentences. Not for progress notes. type: Decision | Convention | "
                + "Preference | Learning | Mistake | Pattern (default Learning). scope: team (default) | agent.",
                ("title", "string", "One line, ≤ 200 chars."),
                ("content", "string", "1–3 sentences, ≤ 4000 chars. Include the WHY for decisions."),
                ("type", "string", "Decision, Convention, Preference, Learning, Mistake or Pattern."),
                ("scope", "string", "team or agent.")),
            Tool("check_base_updates",
                "Check whether the repository's base branch has moved since you branched, and "
                + "merge it into your worktree if it has. Other tasks are running beside you and "
                + "land their work on the base while you write; this is how you meet their changes "
                + "now — when you can still adapt — instead of colliding with them at merge time, "
                + "when nobody has the context to sort it out. COMMIT your work first: it will not "
                + "run against uncommitted changes. Call it before you finish, and again before you "
                + "start editing a file other tasks are likely to touch."),
            Tool("request_repo",
                "Ask for another repository to be added to this cross-repo task, when you find "
                + "the work genuinely cannot be finished without changing one that is not "
                + "checked out here. Records the ask for a human to approve — it does NOT give "
                + "you access, and nothing is filed there until someone agrees. Only meaningful "
                + "on a task that is already one half of a cross-repo task; use list_repos first "
                + "to get the name right.",
                ("repoName", "string", "Exact repository name, as list_repos gives it."),
                ("reason", "string", "What specifically cannot be done without it. One or two sentences.")),
        ]);

    public async Task<SdkMcpToolResult> CallToolAsync(
        string name, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var services = scope.ServiceProvider;

            var text = name switch
            {
                "list_documents" => await ListDocumentsAsync(services, cancellationToken),
                "get_document" => await GetDocumentAsync(services, arguments, cancellationToken),
                "list_repos" => await ListReposAsync(services, cancellationToken),
                "list_tasks" => await ListTasksAsync(services, cancellationToken),
                "get_task" => await GetTaskAsync(services, arguments, cancellationToken),
                "get_task_history" => await GetTaskHistoryAsync(services, arguments, cancellationToken),
                "list_milestones" => await ListMilestonesAsync(services, cancellationToken),
                "recall" => await RecallAsync(services, arguments, cancellationToken),
                "remember" => await RememberAsync(services, arguments, cancellationToken),
                "check_base_updates" => await CheckBaseUpdatesAsync(services, cancellationToken),
                "request_repo" => await RequestRepoAsync(services, arguments, cancellationToken),
                _ => null,
            };

            return text is null
                ? Error($"Unknown tool \"{name}\".")
                : new SdkMcpToolResult { Content = new[] { new SdkMcpTextContent { Text = text } } };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Pipeline tool {Tool} failed for project {ProjectId}", name, projectId);
            return Error($"The {name} tool failed: {Truncate(ex.GetBaseException().Message, 300)}");
        }
    }

    private async Task<string> ListDocumentsAsync(IServiceProvider services, CancellationToken ct)
    {
        var documentsApi = services.GetRequiredService<IDocumentsApi>();
        var documents = await documentsApi.ListDocumentSummariesAsync(orgId, projectId, ct);

        if (documents.Count == 0)
            return "The project's Documents library is empty.";

        var sb = new StringBuilder($"{documents.Count} document(s) in the project:\n");
        foreach (var doc in documents)
        {
            var location = doc.Path.Length == 0 ? doc.Title : $"{doc.Path}/{doc.Title}";
            sb.Append($"- {location} (id: {doc.Id}, v{doc.CurrentVersion}, updated {doc.UpdatedAt:yyyy-MM-dd})\n");
        }
        return sb.ToString();
    }

    private async Task<string> GetDocumentAsync(
        IServiceProvider services, JsonElement arguments, CancellationToken ct)
    {
        if (!arguments.TryGetProperty("documentId", out var idEl)
            || !Ulid.TryParse(idEl.GetString(), out var documentId))
            return "Provide \"documentId\" — a document id from list_documents.";

        var documentsApi = services.GetRequiredService<IDocumentsApi>();
        var documents = await documentsApi.GetDocumentsContentAsync(
            orgId, projectId, [documentId], ct);

        if (documents.Count == 0)
            return "No document with that id exists in this project.";

        var doc = documents[0];
        var content = doc.Content.Length <= GetDocumentContentCap
            ? doc.Content
            : doc.Content[..GetDocumentContentCap] + "\n[… truncated]";
        return $"# {doc.Title}\n(path: \"{doc.Path}\", id: {doc.Id})\n\n{content}";
    }

    private async Task<string> ListReposAsync(IServiceProvider services, CancellationToken ct)
    {
        var projectsApi = services.GetRequiredService<IProjectsApi>();
        var repos = (await projectsApi.ListReposAsync(projectId, ct))
            .Where(r => r.OrgId == orgId)
            .ToList();

        if (repos.Count == 0)
            return "This project has no repositories linked.";

        var sb = new StringBuilder($"{repos.Count} repository/repositories in the project:\n");
        foreach (var repo in repos)
            sb.Append($"- {repo.Name} (branch: {repo.DefaultBranch})\n");
        return sb.ToString();
    }

    private async Task<string> ListTasksAsync(IServiceProvider services, CancellationToken ct)
    {
        var tasksApi = services.GetRequiredService<ITasksApi>();
        var tasks = await tasksApi.ListProjectTaskSummariesAsync(orgId, projectId, ct: ct);

        if (tasks.Count == 0)
            return "The project board has no tasks yet.";

        var sb = new StringBuilder($"{tasks.Count} task(s) on the board (newest first):\n");
        foreach (var task in tasks)
            sb.Append($"- #{task.Number} [{task.CurrentLane}/{task.Status}, {task.Priority} {task.Type}] {task.Title} (id: {task.Id})\n");
        return sb.ToString();
    }

    private async Task<string> GetTaskAsync(
        IServiceProvider services, JsonElement arguments, CancellationToken ct)
    {
        if (!arguments.TryGetProperty("taskId", out var idEl)
            || !Ulid.TryParse(idEl.GetString(), out var taskId))
            return "Provide \"taskId\" — a task id from list_tasks.";

        var tasksApi = services.GetRequiredService<ITasksApi>();
        var task = await tasksApi.GetTaskSnapshotAsync(taskId, ct);

        // The snapshot read is worker-surface (unscoped) — enforce this run's tenant here.
        if (task is null || task.OrgId != orgId || task.ProjectId != projectId)
            return "No task with that id exists in this project.";

        var sb = new StringBuilder();
        sb.Append($"# Task #{task.Number}: {task.Title}\n");
        sb.Append($"id: {task.Id}\nlane: {task.CurrentLane}\nstatus: {task.Status}\n");
        sb.Append($"priority: {task.Priority}\ntype: {task.Type}\n");
        if (!string.IsNullOrWhiteSpace(task.BranchName)) sb.Append($"branch: {task.BranchName}\n");
        if (!string.IsNullOrWhiteSpace(task.PrUrl)) sb.Append($"pr: {task.PrUrl}\n");
        sb.Append('\n');
        sb.Append(string.IsNullOrWhiteSpace(task.Description)
            ? "(no description)"
            : Truncate(task.Description, GetTaskDescriptionCap));
        return sb.ToString();
    }

    private async Task<string> GetTaskHistoryAsync(
        IServiceProvider services, JsonElement arguments, CancellationToken ct)
    {
        if (!arguments.TryGetProperty("taskId", out var idEl)
            || !Ulid.TryParse(idEl.GetString(), out var taskId))
            return "Provide \"taskId\" — a task id from list_tasks.";

        var tasksApi = services.GetRequiredService<ITasksApi>();
        var executions = await tasksApi.ListTaskExecutionSummariesAsync(
            orgId, projectId, taskId, ct);

        if (executions is null)
            return "No task with that id exists in this project.";
        if (executions.Count == 0)
            return "No agent has run a phase on this task yet.";

        var sb = new StringBuilder($"{executions.Count} execution(s) on this task, oldest first:\n");
        var index = 0;
        foreach (var execution in executions)
        {
            index++;
            sb.Append($"{index}. {execution.Lane} — {execution.Status}");
            if (execution.AgentId is { } executorId && executorId == agentId) sb.Append(" (you)");
            if (execution.CompletedAt is { } completed) sb.Append($", finished {completed:yyyy-MM-dd HH:mm}");
            sb.Append('\n');
            if (!string.IsNullOrWhiteSpace(execution.ErrorMessage))
                sb.Append($"   error: {Truncate(execution.ErrorMessage, 300)}\n");
            if (!string.IsNullOrWhiteSpace(execution.FilesChanged))
                sb.Append($"   files changed: {Truncate(execution.FilesChanged, 1000)}\n");
        }
        return sb.ToString();
    }

    private async Task<string> ListMilestonesAsync(IServiceProvider services, CancellationToken ct)
    {
        var tasksApi = services.GetRequiredService<ITasksApi>();
        var milestones = await tasksApi.ListMilestonesForChatAsync(orgId, projectId, ct);

        if (milestones.Count == 0)
            return "This project has no milestones yet — tasks land in the Backlog.";

        var sb = new StringBuilder("Milestones (roadmap order):\n");
        foreach (var m in milestones)
            sb.Append($"- {m.Title} [{m.Status}] — {m.Outcome ?? "(no outcome set)"}\n");
        return sb.ToString();
    }

    private async Task<string> RecallAsync(IServiceProvider services, JsonElement arguments, CancellationToken ct)
    {
        var query = arguments.TryGetProperty("query", out var q) ? q.GetString()?.Trim() : null;
        if (string.IsNullOrWhiteSpace(query))
            return "recall needs a query.";
        var agentsApi = services.GetRequiredService<IAgentsApi>();
        var items = await agentsApi.RetrieveMemoriesAsync(
            new MemoryQuery(orgId, projectId, agentId, query, Take: 12, CharBudget: 6_000), ct);
        if (items.Count == 0)
            return "Nothing in the project memory matches that. If you learn the answer, save it with remember.";
        var sb = new StringBuilder();
        sb.AppendLine($"{items.Count} memories (most relevant first):");
        foreach (var m in items)
            sb.AppendLine($"- [{m.Type}{(m.IsPinned ? ", pinned" : "")}{(m.AgentId is null ? "" : ", yours")}] {m.Title}: {m.Content}");
        return sb.ToString();
    }

    /// <summary>
    /// The one tool here that changes anything outside the agent's own worktree, and it changes
    /// only a request — never the task's repositories. See ITasksApi.RequestGroupRepoAsync for
    /// why approval stays with a human.
    /// </summary>
    /// <summary>
    /// The one tool here that writes anything — and it writes only to the run's own worktree,
    /// which the agent already owns. It exists because the alternative is worse: a task that
    /// never looks up from its branch point discovers its siblings' work as a merge conflict
    /// after review, handed to a resolver that knows neither side's intent.
    ///
    /// Conflicts are deliberately left staged rather than rolled back. The caller is a dev agent
    /// mid-run with both sides in context; rolling back would throw away the only moment where
    /// resolving is cheap.
    /// </summary>
    private async Task<string> CheckBaseUpdatesAsync(IServiceProvider services, CancellationToken ct)
    {
        var tasksApi = services.GetRequiredService<ITasksApi>();
        var task = await tasksApi.GetTaskSnapshotAsync(taskId, ct);
        if (task?.RepoId is not { } repoId)
            return "This task has no repository checked out, so there is no base branch to check.";

        var projectsApi = services.GetRequiredService<IProjectsApi>();
        var repo = await projectsApi.GetRepoAsync(repoId, ct);
        if (repo is null)
            return "This task's repository is no longer available.";

        var route = services.GetRequiredService<Orchestration.IExecutionRouter>().Resolve(task);

        // The branch this task actually forked from — the RECORDED base first ("one resolver,
        // one answer"), the repo's default branch for tasks that predate the record.
        var baseBranch = task.BaseBranch ?? repo.DefaultBranch;

        var result = await route.Git.SyncBaseBranchAsync(
            orgId, repoId, taskId, baseBranch, leaveConflictsInProgress: true, ct: ct);

        if (result.IsFailure)
        {
            return $"Could not check `{baseBranch}`: {result.Error}\n\n"
                + "If this is about uncommitted changes, commit them and call check_base_updates "
                + "again. Otherwise carry on — this is not your task to fix.";
        }

        var outcome = result.Value!;
        return outcome.Status switch
        {
            BaseSyncStatus.AlreadyCurrent =>
                $"`{baseBranch}` has not moved since your branch last took it in. "
                + "Nothing to merge — carry on.",

            BaseSyncStatus.Merged =>
                $"Merged {outcome.CommitsBehind} new commit(s) from `{baseBranch}` into your "
                + "worktree with no conflicts. Run `git diff ORIG_HEAD..HEAD` to see what arrived, and "
                + "re-check your own work against it — another task may have moved, renamed or "
                + "re-typed something you depend on, and your code still has to build against it.",

            BaseSyncStatus.Conflicted =>
                $"`{baseBranch}` moved {outcome.CommitsBehind} commit(s) ahead and merging it "
                + "conflicts with your work. The merge is STAGED in your worktree right now. Resolve "
                + "these files, keeping both sides' intent, then run `git commit --no-edit`:\n"
                + string.Join("\n", outcome.ConflictedFiles.Select(f => $"- `{f}`"))
                + "\n\nDo NOT run `git merge --abort` — that only moves this collision to merge time.",

            _ =>
                $"`{baseBranch}` moved {outcome.CommitsBehind} commit(s) ahead and does not "
                + "merge cleanly; the merge was rolled back and your worktree is untouched.",
        };
    }

    private async Task<string> RequestRepoAsync(IServiceProvider services, JsonElement arguments, CancellationToken ct)
    {
        string? Get(string name) => arguments.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var repoName = Get("repoName")?.Trim();
        var reason = Get("reason")?.Trim();

        if (string.IsNullOrWhiteSpace(repoName))
            return "request_repo needs a repoName — call list_repos for the exact name.";
        if (string.IsNullOrWhiteSpace(reason))
            return "request_repo needs a reason: what specifically cannot be done without it.";

        var tasksApi = services.GetRequiredService<ITasksApi>();
        return await tasksApi.RequestGroupRepoAsync(taskId, repoName, reason, ct);
    }

    private async Task<string> RememberAsync(IServiceProvider services, JsonElement arguments, CancellationToken ct)
    {
        string? Get(string name) => arguments.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var title = Get("title")?.Trim();
        var content = Get("content")?.Trim();
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(content))
            return "remember needs a title and content.";
        if (!Enum.TryParse<MemoryType>(Get("type") ?? "Learning", true, out var type))
            return "type must be one of Decision, Convention, Preference, Learning, Mistake, Pattern.";
        var scope = Get("scope");
        var agentScoped = agentId is not null
            && (string.Equals(scope, "agent", StringComparison.OrdinalIgnoreCase) || string.Equals(scope, "private", StringComparison.OrdinalIgnoreCase));

        var agentsApi = services.GetRequiredService<IAgentsApi>();
        var id = await agentsApi.AddMemoryAsync(new NewMemory(
            orgId, projectId, agentScoped ? agentId : null, type, title, content, MemorySource.Agent), ct);
        if (id == Ulid.Empty)
            return "This project's memory is full — ask a human to archive old entries on the Memory page. Nothing was saved.";
        return $"Remembered ({type}, {(agentScoped ? "private to you" : "shared with the team")}) — id {id}.";
    }

    private static SdkMcpToolDefinition Tool(
        string name, string description,
        params (string Name, string Type, string Description)[] properties)
    {
        var props = string.Join(",", properties.Select(p =>
            $"\"{p.Name}\":{{\"type\":\"{p.Type}\",\"description\":\"{p.Description}\"}}"));
        var required = string.Join(",", properties.Select(p => $"\"{p.Name}\""));
        var schemaJson =
            $$"""{"type":"object","properties":{{{props}}},"required":[{{required}}]}""";

        using var schema = JsonDocument.Parse(schemaJson);
        return new SdkMcpToolDefinition
        {
            Name = name,
            Description = description,
            InputSchema = schema.RootElement.Clone(),
        };
    }

    private static SdkMcpToolResult Error(string message) => new()
    {
        Content = new[] { new SdkMcpTextContent { Text = message } },
        IsError = true,
    };

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
