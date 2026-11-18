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

using System.Text.Json;
using Orkka.Modules.Tasks.Features.TaskExecutions;

internal static class Mappers
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    // FilesChanged is stored as a JSON string — the frontend contract is an array, never null
    private static IReadOnlyList<PlannedFileChange> ParseFilesChanged(string? json)
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

    /// <param name="cost">What this run cost, resolved by the caller through <c>IUsageQuery</c>.
    /// Zero when the host does not bill (Orkka Local) or the run used the user's own
    /// subscription.</param>
    /// <param name="billed">The token buckets the cost was computed from, resolved through the
    /// same <c>IUsageQuery</c>. Null when the execution has no usage rows yet. Kept separate from
    /// the entity's Prompt/Completion rather than replacing them: the two answer different
    /// questions, and only this one reconciles with the invoice.</param>
    public static TaskExecutionResponse ToResponse(
        this TaskExecution entity, Orkka.Modules.Agents.Contracts.AgentSummary? agent, decimal cost,
        ExecutionTokenBreakdown? billed = null) =>
        new(
            entity.Id,
            entity.TaskId,
            entity.AgentId,
            agent?.Name,
            agent?.Role.ToString(),
            entity.Lane.ToString(),
            entity.Status.ToString(),
            entity.AgentLog,
            ParseFilesChanged(entity.FilesChanged),
            entity.TokensUsed,
            entity.PromptTokens,
            entity.CompletionTokens,
            cost,
            billed,
            entity.ErrorMessage,
            entity.StartedAt,
            entity.CompletedAt,
            entity.CreatedAt);
}
