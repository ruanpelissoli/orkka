namespace Orkka.Modules.Agents.Features.Schedules;

using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Domain;

public sealed record ScheduleResponse(
    Ulid Id,
    Ulid ProjectId,
    string Name,
    string Kind,
    bool IsEnabled,
    string? CronExpression,
    string TimeZoneId,
    DateTimeOffset? FireAt,
    DateTimeOffset? NextRunAt,
    DateTimeOffset? LastRunAt,
    string? LastRunStatus,
    Ulid? AgentId,
    Ulid? SkillId,
    string? Message,
    Ulid? ConversationId,
    decimal? AutopilotCapUsd,
    string CreatedByUserId,
    int RunCount,
    int ConsecutiveFailures,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ScheduleRunResponse(
    Ulid Id,
    Ulid ScheduleId,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    Ulid? ConversationId,
    string? Summary,
    string? Error,
    string Trigger);

/// <summary>Create/update payload. Recurring: <see cref="CronExpression"/> (+ TimeZoneId);
/// one-shot: <see cref="FireAt"/>. Exactly one of the two.</summary>
public sealed record UpsertScheduleRequest(
    string Name,
    string Kind,
    string? CronExpression = null,
    string? TimeZoneId = null,
    DateTimeOffset? FireAt = null,
    string? AgentId = null,
    string? SkillId = null,
    string? Message = null,
    decimal? AutopilotCapUsd = null,
    /// <summary>AutopilotStart only: explicit opt-in to an uncapped session (BillingManage).</summary>
    bool Uncapped = false,
    bool IsEnabled = true);

internal static class ScheduleMappers
{
    public static ScheduleResponse ToResponse(this AgentSchedule s) => new(
        s.Id, s.ProjectId, s.Name, s.Kind.ToString(), s.IsEnabled, s.CronExpression, s.TimeZoneId, s.FireAt,
        s.NextRunAt, s.LastRunAt, s.LastRunStatus?.ToString(), s.AgentId, s.SkillId, s.Message, s.ConversationId,
        s.AutopilotCapUsd, s.CreatedByUserId, s.RunCount, s.ConsecutiveFailures, s.CreatedAt, s.UpdatedAt);

    public static ScheduleRunResponse ToResponse(this AgentScheduleRun r) => new(
        r.Id, r.ScheduleId, r.Status.ToString(), r.StartedAt, r.FinishedAt, r.ConversationId, r.Summary, r.Error, r.Trigger);
}
