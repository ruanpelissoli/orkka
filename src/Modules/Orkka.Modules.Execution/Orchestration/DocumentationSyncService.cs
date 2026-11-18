namespace Orkka.Modules.Execution.Orchestration;

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Usage.Contracts;
using Orkka.Modules.Documents.Contracts;
using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Ai;
using Orkka.SharedKernel.Utilities;

/// <summary>
/// Keeps a project's documents honest about the work that shipped.
///
/// Once a task has settled — Done, and merged when it touched a repo — this asks two questions
/// of what it changed: is there something here worth a document the project does not have, and
/// did this make an existing document wrong? Anything it writes is attributed to the task, so
/// both the task page and the document's history can say which piece of work caused it.
///
/// It reads no repository. By the time a task settles its worktree has been collected and its
/// branch merged away, so the evidence is what was captured while they still existed: the task's
/// description, its approved plan, its published API surface, and the stored code-change
/// snapshots. That constraint is also what makes the pass cheap enough to run on every task.
/// </summary>
public interface IDocumentationSyncService
{
    Task RunAsync(DocSyncCandidateTask task, CancellationToken ct = default);
}

public sealed class DocumentationSyncService(
    IServiceScopeFactory scopeFactory,
    IOptions<AnthropicOptions> anthropicOptions,
    IOptions<DocumentationSyncOptions> syncOptions,
    ILogger<DocumentationSyncService> logger) : IDocumentationSyncService
{
    private readonly AnthropicOptions _anthropic = anthropicOptions.Value;
    private readonly DocumentationSyncOptions _options = syncOptions.Value;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public async Task RunAsync(DocSyncCandidateTask task, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var tasksApi = scope.ServiceProvider.GetRequiredService<ITasksApi>();

        var outcome = DocSyncStatus.Skipped;
        try
        {
            outcome = await RunCoreAsync(scope, tasksApi, task, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Worker shutting down mid-pass. Recorded as Failed rather than left Running so the
            // sweep can pick it up on the next boot without waiting out the abandoned-claim
            // window; the attempt was already counted when it was claimed.
            outcome = DocSyncStatus.Failed;
            logger.LogInformation(
                "Documentation sync for task {TaskId} was interrupted — it will be retried", task.Id);
        }
        catch (RunnerUtilityUnavailableException ex)
        {
            // Retried by the sweep (Failed, under its attempt cap) once the runner is back.
            outcome = DocSyncStatus.Failed;
            logger.LogInformation(
                "Documentation sync for task {TaskId} could not run: {Reason}", task.Id, ex.Message);
        }
        catch (Exception ex)
        {
            outcome = DocSyncStatus.Failed;
            logger.LogWarning(ex,
                "Documentation sync failed for task {TaskId} — the task itself is unaffected", task.Id);
        }

        // Never inside the try: a task whose pass ended must not be left claimed as Running,
        // which would hide it from the sweep forever.
        await tasksApi.CompleteDocSyncAsync(task.Id, outcome, CancellationToken.None);
    }

    private async Task<DocSyncStatus> RunCoreAsync(
        IServiceScope scope, ITasksApi tasksApi, DocSyncCandidateTask task, CancellationToken ct)
    {
        var projectsApi = scope.ServiceProvider.GetRequiredService<IProjectsApi>();
        var project = await projectsApi.GetProjectSummaryAsync(task.ProjectId, ct);

        if (project?.Settings?.DocumentationSyncEnabled != true)
            return DocSyncStatus.Skipped;

        var agentsApi = scope.ServiceProvider.GetRequiredService<IAgentsApi>();
        var agents = await agentsApi.ListAgentsAsync(task.ProjectId, ct);
        var pm = agents.FirstOrDefault(a => a.Role == AgentRole.PM);
        if (pm is null)
        {
            logger.LogInformation(
                "Skipping documentation for task {TaskId} — project {ProjectId} has no PM agent",
                task.Id, task.ProjectId);
            return DocSyncStatus.Skipped;
        }

        var documentsApi = scope.ServiceProvider.GetRequiredService<IDocumentsApi>();

        // Markdown only. An uploaded PDF stores bytes rather than a body, so it can be neither
        // rewritten nor meaningfully compared — and keeping it out of the index is the cheapest
        // way to stop the model proposing it as a target.
        var index = (await documentsApi.ListDocumentSummariesAsync(task.OrgId, task.ProjectId, ct))
            .Where(d => !d.IsFile)
            .ToList();

        var context = await BuildTaskContextAsync(tasksApi, task, ct);

        // Documents are read by the project's people, so they are written in the project's
        // language. The sweep runs with no human in the loop — there is no requester locale to
        // fall back to, and a project without a language set gets English documents.
        var language = ProjectLanguage.Resolve(project, requesterLocale: null);

        var triage = await TriageAsync(scope, task, pm, context, index, language, ct);
        if (triage is null || (triage.Create is null && triage.UpdateIds.Count == 0))
        {
            logger.LogDebug("Task {TaskId} needs no documentation changes", task.Id);
            return DocSyncStatus.Skipped;
        }

        var targets = ResolveUpdateTargets(triage, index);

        var existing = targets.Count > 0
            ? await documentsApi.GetDocumentsContentAsync(
                task.OrgId, task.ProjectId, [.. targets.Select(t => t.Id)], ct)
            : [];

        var written = await WriteAsync(scope, task, pm, context, triage, existing, language, ct);
        if (written.Count == 0)
            return DocSyncStatus.Skipped;

        var saved = await documentsApi.SyncTaskDocumentsAsync(
            task.OrgId, task.ProjectId, task.Id, pm.Id, task.CreatedByUserId, written, ct);

        var changed = saved.Where(s => s.Outcome != ChatDocumentOutcome.Unchanged).ToList();
        if (changed.Count == 0)
            return DocSyncStatus.Skipped;

        await AnnounceAsync(tasksApi, task, pm, changed, ct);
        return DocSyncStatus.Completed;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Context
    // ─────────────────────────────────────────────────────────────────────────

    private sealed record TaskContext(string Summary, int ChangedFileCount);

    /// <summary>
    /// What the task did, from rows that outlive the branch: its text, its plan, the files it
    /// touched. Shared by both model calls.
    /// </summary>
    private async Task<TaskContext> BuildTaskContextAsync(
        ITasksApi tasksApi, DocSyncCandidateTask task, CancellationToken ct)
    {
        var snapshot = await tasksApi.GetTaskSnapshotAsync(task.Id, ct);
        var plan = await tasksApi.GetLatestApprovedPlanAsync(task.Id, ct);
        var changes = await tasksApi.GetTaskChangeSummaryAsync(task.Id, ct);

        var sb = new StringBuilder();
        sb.AppendLine(PromptSafety.Wrap("task", $"Title: {task.Title}"));

        if (!string.IsNullOrWhiteSpace(snapshot?.Description))
        {
            sb.AppendLine();
            sb.AppendLine(PromptSafety.Wrap("task-description", Truncate(
                snapshot.Description, _options.MaxDescriptionChars)));
        }

        if (!string.IsNullOrWhiteSpace(plan?.Summary))
        {
            sb.AppendLine();
            sb.AppendLine(PromptSafety.Wrap("approved-plan", Truncate(
                plan.Summary, _options.MaxPlanChars)));
        }

        if (!string.IsNullOrWhiteSpace(snapshot?.ApiSurface))
        {
            sb.AppendLine();
            sb.AppendLine("Interface this task exposed to other services:");
            sb.AppendLine(PromptSafety.Wrap("api-surface", snapshot.ApiSurface));
        }

        if (changes is { FilePaths.Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine($"Files changed ({changes.FilePaths.Count}):");
            foreach (var path in changes.FilePaths.Take(_options.MaxFilePaths))
                sb.AppendLine($"- {path}");
            if (changes.FilePaths.Count > _options.MaxFilePaths)
                sb.AppendLine($"- … and {changes.FilePaths.Count - _options.MaxFilePaths} more");
        }

        // Paths, not code: Orkka keeps no copy of the diff. The writer runs on the developer's
        // runner and is told which files changed; that is what decides a document's scope.
        return new TaskContext(sb.ToString(), changes?.FilePaths.Count ?? 0);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Pass 1: triage
    // ─────────────────────────────────────────────────────────────────────────

    private sealed record TriageCreate(string Title, string Path, string Reason);

    private sealed record TriageResult(TriageCreate? Create, List<string> UpdateIds);

    private const string TriageSystemPrompt = """
        You decide whether a completed piece of work changes what a project's documentation
        should say. You are conservative: most tasks change nothing a document needs to record,
        and proposing work that is not needed wastes the team's money.

        Propose a NEW document only when the work introduces something a newcomer would have to
        understand and could not reconstruct from the code alone — a subsystem, an integration, a
        non-obvious decision and its reasoning, a workflow that spans several places.

        Propose UPDATING an existing document only when this work makes something it says WRONG
        or INCOMPLETE. Judge from its title and folder path. A document that merely touches on
        the same area is not enough — the test is whether a reader would now be misled.

        Do NOT propose anything for: bug fixes, refactors, dependency bumps, styling, test-only
        changes, copy edits, or work whose effect is already obvious from the task title.

        Reply with JSON only, no prose and no code fence:
        {"create": {"title": "...", "path": "...", "reason": "..."} | null, "updateIds": ["..."]}

        - "path" is a folder path inside the document tree, "" for the root. Reuse a folder that
          already exists rather than inventing a parallel one.
        - "updateIds" holds ids copied exactly from the document list. At most {{MAX_UPDATES}}.
        - When nothing is warranted, reply {"create": null, "updateIds": []}. That is the normal
          and expected answer.
        """;

    private async Task<TriageResult?> TriageAsync(
        IServiceScope scope, DocSyncCandidateTask task, AgentSummary pm,
        TaskContext context, IReadOnlyList<DocumentSummary> index, string? language,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("A task just finished and its work has been merged.");
        sb.AppendLine();
        sb.AppendLine(context.Summary);
        sb.AppendLine();

        if (index.Count == 0)
        {
            sb.AppendLine("The project has no documents yet.");
        }
        else
        {
            sb.AppendLine($"The project's existing documents ({index.Count}):");
            foreach (var doc in index)
            {
                var location = doc.Path.Length == 0 ? doc.Title : $"{doc.Path}/{doc.Title}";
                sb.AppendLine(PromptSafety.Wrap("document", $"id: {doc.Id} — {location}"));
            }
        }

        // Triage names the document that gets created — the title must already be in the
        // project's language, or the tree shows an English heading over a localized body.
        var system = TriageSystemPrompt.Replace(
            "{{MAX_UPDATES}}", _options.MaxUpdatesPerTask.ToString(),
            StringComparison.Ordinal) + AgentLanguage.Instruction(language);

        var text = await CallModelAsync(
            scope, task, pm, _anthropic.FastUtilityModel, _options.TriageMaxTokens,
            $"{system}\n\n{PromptSafety.Notice}", sb.ToString(), ct);

        if (text is null)
            return null;

        var parsed = ParseJson<TriageResponse>(text, task.Id, "triage");
        if (parsed is null)
            return null;

        var create = parsed.Create is { } c && !string.IsNullOrWhiteSpace(c.Title)
            ? new TriageCreate(c.Title, c.Path ?? "", c.Reason ?? "")
            : null;

        return new TriageResult(
            create,
            [.. (parsed.UpdateIds ?? []).Take(_options.MaxUpdatesPerTask)]);
    }

    /// <summary>Triage ids matched back to the index it was shown. An id the model invented, or
    /// one belonging to a file document, simply does not resolve and is dropped.</summary>
    private static List<DocumentSummary> ResolveUpdateTargets(
        TriageResult triage, IReadOnlyList<DocumentSummary> index)
    {
        var byId = index.ToDictionary(d => d.Id.ToString(), StringComparer.OrdinalIgnoreCase);

        return [.. triage.UpdateIds
            .Select(id => byId.TryGetValue(id.Trim(), out var doc) ? doc : null)
            .OfType<DocumentSummary>()
            .DistinctBy(d => d.Id)];
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Pass 2: write
    // ─────────────────────────────────────────────────────────────────────────

    private const string WriteSystemPrompt = """
        You maintain a software project's documentation. You are writing for a teammate who was
        not involved in this work, in markdown.

        For a NEW document: explain what the thing is, why it exists, and how the parts fit —
        the reasoning a reader cannot recover by reading the code. Do not narrate the task, do
        not list the files it touched, and do not paste code that will drift.

        For an EXISTING document: return the COMPLETE revised document, not a patch and not a
        changelog entry. Correct what this work made wrong, fill in what it made incomplete, and
        leave every other part exactly as it was — including its heading structure and voice.
        Never append a "recent changes" section; a document describes how things are now.

        Reply with JSON only, no prose and no code fence:
        {"documents": [{"id": "<id, or omit to create>", "title": "...", "path": "...",
          "content": "<full markdown body>"}]}

        Return an empty array if, having now seen the detail, nothing is actually worth writing.
        """;

    private async Task<List<TaskDocumentSpec>> WriteAsync(
        IServiceScope scope, DocSyncCandidateTask task, AgentSummary pm, TaskContext context,
        TriageResult triage, IReadOnlyList<DocumentContent> existing, string? language,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("A task just finished and its work has been merged.");
        sb.AppendLine();
        sb.AppendLine(context.Summary);

        sb.AppendLine();

        if (triage.Create is { } create)
        {
            sb.AppendLine("Write a NEW document:");
            sb.AppendLine($"- title: {create.Title}");
            sb.AppendLine($"- path: {(create.Path.Length == 0 ? "(root)" : create.Path)}");
            sb.AppendLine($"- why it is needed: {create.Reason}");
            sb.AppendLine("Omit \"id\" for this one.");
            sb.AppendLine();
        }

        foreach (var doc in existing)
        {
            var location = doc.Path.Length == 0 ? doc.Title : $"{doc.Path}/{doc.Title}";
            sb.AppendLine($"Revise this document — return it complete, with id {doc.Id}:");
            sb.AppendLine($"- title: {doc.Title}");
            sb.AppendLine($"- location: {location}");
            sb.AppendLine(PromptSafety.Wrap(
                "document-content", Truncate(doc.Content, _options.MaxExistingDocChars)));
            sb.AppendLine();
        }

        // The PM's own model, not the fast utility one: triage is a yes/no over metadata, but
        // this rewrites documents a person will read and rely on.
        var text = await CallModelAsync(
            scope, task, pm, pm.Model, _options.WriteMaxTokens,
            $"{WriteSystemPrompt}{AgentLanguage.Instruction(language)}\n\n{PromptSafety.Notice}", sb.ToString(), ct);

        if (text is null)
            return [];

        var parsed = ParseJson<WriteResponse>(text, task.Id, "write");
        if (parsed?.Documents is null)
            return [];

        var specs = new List<TaskDocumentSpec>();
        foreach (var doc in parsed.Documents)
        {
            if (string.IsNullOrWhiteSpace(doc.Title) || string.IsNullOrWhiteSpace(doc.Content))
                continue;

            // An id the model echoed back must be one we actually offered — never a document it
            // decided on its own to overwrite.
            Ulid? documentId = null;
            if (!string.IsNullOrWhiteSpace(doc.Id))
            {
                if (!Ulid.TryParse(doc.Id, out var parsedId)
                    || !existing.Any(e => e.Id == parsedId))
                {
                    logger.LogWarning(
                        "Task {TaskId} documentation returned id '{Id}', which was not offered for "
                        + "revision — skipped", task.Id, doc.Id);
                    continue;
                }
                documentId = parsedId;
            }

            specs.Add(new TaskDocumentSpec(documentId, doc.Title, doc.Path ?? "", doc.Content));
        }

        return specs;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Model plumbing
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One utility generation on the task creator's own runner (their subscription, $0),
    /// attributed to them.
    ///
    /// A one-shot utility call rather than the agent invokers on purpose: this needs no
    /// worktree, no tools and no session. There is no platform-key fallback — a runner miss
    /// throws <see cref="RunnerUtilityUnavailableException"/>, which <see cref="RunAsync"/>
    /// records as Failed, so the sweep retries the pass (up to its cap) once the runner is back.
    /// </summary>
    private async Task<string?> CallModelAsync(
        IServiceScope scope, DocSyncCandidateTask task, AgentSummary pm,
        string model, int maxTokens, string system, string userMessage, CancellationToken ct)
    {
        var runnerUtility = scope.ServiceProvider.GetRequiredService<IUserRunnerUtility>();

        var request = new AnthropicRequest
        {
            Model = model,
            MaxTokens = maxTokens,
            System = system,
            Messages = [new AnthropicMessage("user", userMessage)],
        };

        var response = await runnerUtility.CompleteOnRunnerAsync(
            task.OrgId, task.CreatedByUserId, request, "keep this project's documents in sync", ct);

        await RecordUsageAsync(scope, task, pm, model, response, ct);

        var text = response.Text?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private async Task RecordUsageAsync(
        IServiceScope scope, DocSyncCandidateTask task, AgentSummary pm,
        string model, AnthropicResponse response, CancellationToken ct)
    {
        try
        {
            var usage = scope.ServiceProvider.GetRequiredService<IUsageRecorder>();
            await usage.RecordUsageAsync(new RecordUsageCommand(
                task.OrgId, task.ProjectId, task.Id, pm.Id, ExecutionId: null,
                model, response.InputTokens, response.OutputTokens,
                response.CacheReadTokens, response.CacheWriteTokens,
                // Runner-served generations record at $0, the same convention runner agent runs
                // use — attribution stays visible, spend stays zero.
                FlatCostBase: 0m,
                // "Whoever asked for the work owns its cost" — the task's creator, not whoever
                // happened to approve the review that put it in Done.
                UserId: task.CreatedByUserId), ct);
        }
        catch (Exception ex)
        {
            // The tokens are already spent; losing the record must not also lose the documents.
            logger.LogWarning(ex,
                "Could not record documentation usage for task {TaskId}", task.Id);
        }
    }

    /// <summary>Parses a model's JSON reply, tolerating the fences and commentary it was told
    /// not to use (ModelJson pulls the object out of the wrapping). Returns null on anything
    /// unparseable — the caller treats that as "produced nothing".</summary>
    private T? ParseJson<T>(string text, Ulid taskId, string stage) where T : class
    {
        var json = ModelJson.ExtractObject(text);
        if (json is null)
        {
            logger.LogWarning(
                "Task {TaskId} documentation {Stage} reply contained no complete JSON object", taskId, stage);
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex,
                "Task {TaskId} documentation {Stage} reply was not valid JSON", taskId, stage);
            return null;
        }
    }

    private async Task AnnounceAsync(
        ITasksApi tasksApi, DocSyncCandidateTask task, AgentSummary pm,
        IReadOnlyList<CreatedChatDocument> changed, CancellationToken ct)
    {
        var created = changed.Where(c => c.Outcome == ChatDocumentOutcome.Created).ToList();
        var updated = changed.Where(c => c.Outcome == ChatDocumentOutcome.Updated).ToList();

        var parts = new List<string>();
        if (created.Count > 0)
            parts.Add($"created {Join(created)}");
        if (updated.Count > 0)
            parts.Add($"updated {Join(updated)}");

        var message = $"{pm.Name} {string.Join(" and ", parts)} in the project's documents.";

        try
        {
            await tasksApi.AddTaskMessageAsync(
                task.Id, "system", message, MessageType.SystemEvent, ct: ct);
        }
        catch (Exception ex)
        {
            // The documents are written and the task card lists them; a missing timeline line
            // must not turn a successful pass into a retried one.
            logger.LogWarning(ex,
                "Could not write the documentation note for task {TaskId}", task.Id);
        }

        logger.LogInformation(
            "Documentation for task {TaskId}: {Created} created, {Updated} updated",
            task.Id, created.Count, updated.Count);

        static string Join(IReadOnlyList<CreatedChatDocument> docs)
            => string.Join(", ", docs.Select(d => $"**{d.Title}**"));
    }

    private static string Truncate(string value, int maxChars)
        => value.Length <= maxChars ? value : value[..maxChars];

    // Wire shapes — deliberately separate from the domain records above so a missing or oddly
    // typed field from the model deserializes to null instead of throwing.
    private sealed class TriageResponse
    {
        public TriageCreateResponse? Create { get; set; }
        public List<string>? UpdateIds { get; set; }
    }

    private sealed class TriageCreateResponse
    {
        public string? Title { get; set; }
        public string? Path { get; set; }
        public string? Reason { get; set; }
    }

    private sealed class WriteResponse
    {
        public List<WriteDocumentResponse>? Documents { get; set; }
    }

    private sealed class WriteDocumentResponse
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public string? Path { get; set; }
        public string? Content { get; set; }
    }
}
