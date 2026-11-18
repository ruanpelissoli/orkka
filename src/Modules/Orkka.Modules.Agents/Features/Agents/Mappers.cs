namespace Orkka.Modules.Agents.Features.Agents;

using Orkka.Modules.Agents.Domain;
using Orkka.Modules.Usage.Contracts;

internal static class Mappers
{
    /// <param name="billed">Token totals from the usage rows, or null when the agent has none.
    /// Kept alongside the stored counters rather than replacing them: the counters are what an
    /// unbilled host (Orkka Local) still has, and dropping to zero there would be a regression.</param>
    public static AgentResponse ToResponse(
        this ProjectAgent entity, AgentTokenTotals? billed = null) =>
        new(
            entity.Id,
            entity.ProjectId,
            entity.RepoIds.Select(g => new Ulid(g)).ToList(),
            entity.SkillIds.Select(g => new Ulid(g)).ToList(),
            entity.Name,
            entity.Role.ToString(),
            entity.Avatar,
            entity.Model,
            entity.SystemPrompt,
            entity.UserInstructions,
            entity.IsDefault,
            entity.TotalTokensIn,
            entity.TotalTokensOut,
            entity.TotalCostUsd,
            billed is null
                ? null
                : new AgentTokenBreakdown(
                    billed.InputTokens,
                    billed.OutputTokens,
                    billed.CacheReadTokens,
                    billed.CacheWriteTokens,
                    billed.TotalTokens),
            entity.TasksCompleted,
            entity.TasksFailed,
            entity.CreatedAt,
            entity.UpdatedAt);
}
