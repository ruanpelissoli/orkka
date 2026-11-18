namespace Orkka.Modules.Tasks.Features.Evidence;

using System.Text.Json;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Usage.Contracts;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Features.Tasks;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.SharedKernel.Security;

/// <summary>
/// Assembles the proof-of-done bundle for a task. Reviewing an agent's diff cold is the
/// slowest part of the pipeline; this endpoint front-loads the evidence — what was promised,
/// what was touched, what QA and the reviewer concluded — so the diff becomes confirmation
/// rather than investigation.
/// </summary>
public sealed class GetTaskEvidenceEndpoint(
    TasksDbContext db,
    IAgentsApi agentsApi,
    IProjectsApi projectsApi,
    IUsageQuery usageQuery,
    ICurrentUser currentUser)
    : EndpointWithoutRequest<TaskEvidenceResponse>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public override void Configure()
    {
        Get("/api/tasks/{taskId:ulid}/evidence");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var taskId = Ulid.Parse(Route<string>("taskId"));

        var task = await db.Tasks
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == taskId && t.OrgId == currentUser.OrgId, ct);

        if (task is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var project = await projectsApi.GetProjectSummaryAsync(task.ProjectId, ct);

        var executions = await db.TaskExecutions
            .AsNoTracking()
            .Where(e => e.OrgId == currentUser.OrgId && e.TaskId == taskId)
            .OrderBy(e => e.StartedAt)
            .ToListAsync(ct);

        // The approved plan is the promise being checked; fall back to the newest plan so a
        // task still parked in PlanReady shows what it intends to do.
        var plan = await db.TaskPlans
            .AsNoTracking()
            .Where(p => p.OrgId == currentUser.OrgId && p.TaskId == taskId)
            .OrderByDescending(p => p.Status == PlanStatus.Approved)
            .ThenByDescending(p => p.Version)
            .FirstOrDefaultAsync(ct);

        var verdictMessages = await db.TaskMessages
            .AsNoTracking()
            .Where(m => m.OrgId == currentUser.OrgId && m.TaskId == taskId)
            .Where(m => m.MessageType == MessageType.QaReport || m.MessageType == MessageType.CodeReview)
            .OrderByDescending(m => m.CreatedAt)
            .ThenByDescending(m => m.Id)
            .ToListAsync(ct);

        var attachments = await db.TaskAttachments
            .AsNoTracking()
            .Where(a => a.OrgId == currentUser.OrgId && a.TaskId == taskId)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(ct);

        var agentIds = executions.Where(e => e.AgentId.HasValue).Select(e => e.AgentId!.Value).Distinct().ToList();
        var agents = (await agentsApi.GetAgentsAsync(agentIds, ct)).ToDictionary(a => a.Id);

        // Cost comes from the Usage module's rows, which is the only place it exists. Executions
        // carry token counts but never carried cost — the column that used to sit here was
        // written by nothing and reported $0.00 on every task.
        var costByExecution = await usageQuery.GetExecutionCostsAsync(
            currentUser.OrgId, task.ProjectId, executions.Select(e => e.Id).ToList(), ct);

        var deliveredFiles = executions
            .SelectMany(e => ParseFilesChanged(e.FilesChanged))
            .Select(f => f.Path)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var runs = executions.Select(e => new EvidenceRunResponse(
            e.Id,
            e.Lane.ToString(),
            e.Status.ToString(),
            e.AgentId is { } aid ? agents.GetValueOrDefault(aid)?.Name : null,
            e.ErrorMessage,
            ParseFilesChanged(e.FilesChanged).Count,
            e.TokensUsed,
            costByExecution.GetValueOrDefault(e.Id),
            e.StartedAt is { } s && e.CompletedAt is { } c ? (c - s).TotalSeconds : null,
            e.StartedAt,
            e.CompletedAt)).ToList();

        var totals = new EvidenceTotalsResponse(
            TotalTokens: executions.Sum(e => e.TokensUsed),
            TotalCost: costByExecution.Values.Sum(),
            RunCount: executions.Count,
            FilesTouched: deliveredFiles.Count,
            TotalDurationSeconds: runs.Sum(r => r.DurationSeconds ?? 0) is var total && total > 0 ? total : null,
            ReviewCycleCount: task.ReviewCycleCount,
            QaCycleCount: task.QaCycleCount,
            CheckCycleCount: task.CheckCycleCount);

        var response = new TaskEvidenceResponse(
            TaskId: task.Id,
            Code: project is null ? $"#{task.Number}" : $"{project.Key}-{task.Number}",
            Title: task.Title,
            Lane: task.CurrentLane.ToString(),
            Status: task.Status.ToString(),
            BranchName: task.BranchName,
            PrUrl: task.PrUrl,
            IsMerged: task.IsMerged,
            Plan: plan is null ? null : new EvidencePlanResponse(
                plan.Id, plan.Version, plan.Status.ToString(),
                plan.Content.Summary, plan.Content.Approach,
                plan.Content.EstimatedTokens, plan.ApprovedAt),
            FileChecklist: BuildChecklist(plan?.Content.FilesChanged, deliveredFiles),
            QaVerdict: BuildVerdict(verdictMessages.FirstOrDefault(m => m.MessageType == MessageType.QaReport)),
            CodeReview: BuildVerdict(verdictMessages.FirstOrDefault(m => m.MessageType == MessageType.CodeReview)),
            Runs: runs,
            Attachments: attachments.Select(a => new EvidenceAttachmentResponse(
                a.Id, a.FileName, a.FileType, a.FileUrl, a.FileSize, a.CreatedAt)).ToList(),
            Totals: totals);

        await Send.OkAsync(response, ct);
    }

    /// <summary>
    /// Cross-references the plan's file list against what the runs actually touched. Missing
    /// rows are the interesting ones — an agent that quietly skipped a planned file is the
    /// failure mode a diff review is least likely to catch.
    /// </summary>
    private static List<EvidenceFileCheckResponse> BuildChecklist(
        List<PlannedFileChange>? planned, HashSet<string> delivered)
    {
        var checklist = new List<EvidenceFileCheckResponse>();
        var plannedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in planned ?? [])
        {
            plannedPaths.Add(p.Path);
            checklist.Add(new EvidenceFileCheckResponse(
                p.Path,
                delivered.Contains(p.Path) ? "delivered" : "missing",
                p.Action,
                p.Description));
        }

        foreach (var path in delivered.Where(d => !plannedPaths.Contains(d)).OrderBy(d => d))
            checklist.Add(new EvidenceFileCheckResponse(path, "unplanned", null, null));

        return checklist;
    }

    /// <summary>
    /// Reads the pass/fail marker the orchestrator writes into the first line of QA and review
    /// reports. Unknown is a real outcome — an unparseable verdict is exactly the case a human
    /// is being asked to adjudicate.
    /// </summary>
    private static EvidenceVerdictResponse? BuildVerdict(TaskMessage? message)
    {
        if (message is null) return null;

        var firstLine = message.Content.Split('\n', 2)[0];
        var verdict = firstLine switch
        {
            var l when l.Contains("Passed", StringComparison.OrdinalIgnoreCase) => "pass",
            var l when l.Contains("Failed", StringComparison.OrdinalIgnoreCase) => "fail",
            var l when l.Contains("Approved", StringComparison.OrdinalIgnoreCase) => "approve",
            var l when l.Contains("Changes requested", StringComparison.OrdinalIgnoreCase) => "request_changes",
            _ => "unknown"
        };

        return new EvidenceVerdictResponse(
            verdict, message.Content, BoardQueries.ExtractAgentName(message.Metadata), message.CreatedAt);
    }

    private static List<PlannedFileChange> ParseFilesChanged(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<PlannedFileChange>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
