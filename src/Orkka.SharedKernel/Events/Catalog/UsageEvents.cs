namespace Orkka.SharedKernel.Events.Catalog;

public sealed record UsageRecorded(
    Ulid OrgId,
    Ulid ProjectId,
    Ulid? TaskId,
    Ulid? AgentId,
    string Model,
    int InputTokens,
    int OutputTokens,
    decimal CostTotal) : IntegrationEvent;
