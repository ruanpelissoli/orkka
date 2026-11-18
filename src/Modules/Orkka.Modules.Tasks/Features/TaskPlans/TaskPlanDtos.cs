namespace Orkka.Modules.Tasks.Features.TaskPlans;

using Orkka.Modules.Tasks.Domain;

public sealed record TaskPlanResponse(
    Ulid Id,
    Ulid TaskId,
    int Version,
    PlanContent Content,
    string Status,
    string? Feedback,
    DateTimeOffset? ApprovedAt,
    DateTimeOffset CreatedAt);

public sealed record ReviewPlanRequest(
    string Decision,
    string? Feedback);
