namespace Orkka.Modules.Tasks.Features.TaskExecutions;

using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.SharedKernel.Security;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.Modules.Agents.Contracts;
using Orkka.SharedKernel.Storage;
using Orkka.SharedKernel.Utilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using Orkka.Modules.Tasks.Features.TaskExecutions;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;

public sealed class ListTaskExecutionsEndpoint(
    TasksDbContext db,
    Orkka.Modules.Agents.Contracts.IAgentsApi agentsApi,
    Orkka.Modules.Usage.Contracts.IUsageQuery usageQuery,
    ICurrentUser currentUser)
    : EndpointWithoutRequest<List<TaskExecutionResponse>>
{
    public override void Configure()
    {
        Get("/api/tasks/{taskId:ulid}/executions");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var taskId = Ulid.Parse(Route<string>("taskId"));

        // ProjectId, not just existence: the usage lookup filters on it (nullable id columns
        // can't be matched with a Contains).
        var projectId = await db.Tasks.AsNoTracking()
            .Where(t => t.Id == taskId && t.OrgId == currentUser.OrgId)
            .Select(t => (Ulid?)t.ProjectId)
            .FirstOrDefaultAsync(ct);

        if (projectId is not { } taskProjectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var executions = await db.TaskExecutions
            .AsNoTracking()
            .Where(e => e.OrgId == currentUser.OrgId && e.TaskId == taskId)
            .OrderByDescending(e => e.StartedAt)
            .ToListAsync(ct);

        // Agent display info lives in the Agents module (no cross-module navigation)
        var agentIds = executions.Where(e => e.AgentId.HasValue).Select(e => e.AgentId!.Value).Distinct().ToList();
        var agents = (await agentsApi.GetAgentsAsync(agentIds, ct)).ToDictionary(a => a.Id);

        // Cost lives in the Usage module's rows keyed by ExecutionId — the tasks schema stores
        // token counts only, and only the two smallest buckets of them (see
        // ExecutionTokenBreakdown). Both reads come from the same rows, so the tokens shown
        // always reconcile with the dollar figure shown beside them.
        var executionIds = executions.Select(e => e.Id).ToList();
        var costByExecution = await usageQuery.GetExecutionCostsAsync(
            currentUser.OrgId, taskProjectId, executionIds, ct);
        var samples = await usageQuery.GetExecutionTokenSamplesAsync(
            currentUser.OrgId, taskProjectId, executionIds, ct);

        // Summed across models: GetExecutionTokenSamplesAsync returns one row per
        // (execution, model), and an execution that called more than one model would otherwise
        // report whichever row happened to land first.
        var billedByExecution = samples
            .GroupBy(s => s.ExecutionId)
            .ToDictionary(
                g => g.Key,
                g => new ExecutionTokenBreakdown(
                    g.Sum(s => (long)s.InputTokens),
                    g.Sum(s => (long)s.OutputTokens),
                    g.Sum(s => (long)s.CacheReadTokens),
                    g.Sum(s => (long)s.CacheWriteTokens)));

        await Send.OkAsync(executions
            .Select(e => e.ToResponse(
                e.AgentId.HasValue ? agents.GetValueOrDefault(e.AgentId.Value) : null,
                costByExecution.GetValueOrDefault(e.Id),
                billedByExecution.GetValueOrDefault(e.Id)))
            .ToList(), ct);
    }
}
