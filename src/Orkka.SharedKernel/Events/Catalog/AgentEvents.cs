namespace Orkka.SharedKernel.Events.Catalog;

/// <summary>
/// Published by the Agents module when an agent-chat run finishes detached (its SSE
/// observer disconnected — the user closed the chat panel mid-reply) OR when the run
/// shipped deliverables (diagrams, tasks, documents, design assets) regardless of
/// attachment — artifact runs are long and a connected stream doesn't mean the user is
/// watching. Reactions only — the assistant message is already persisted when this fires.
/// Connected text-only replies never publish: the reply reached the user through the
/// live stream, so a bell notification would be noise.
/// RecipientUserId is the user who sent the message that started the run.
/// </summary>
public sealed record AgentChatReplyCompleted(
    Ulid OrgId,
    Ulid ProjectId,
    Ulid AgentId,
    Ulid ConversationId,
    string AgentName,
    string? ConversationTitle,
    string RecipientUserId) : IntegrationEvent;

/// <summary>
/// Published by the Agents module when a schedule firing fails (the action threw, the
/// playbook turn errored, or the run timed out). Addressed to the schedule's creator — the
/// person who set the standing order. <paramref name="Disabled"/> is true when this failure
/// tripped the auto-disable threshold, which is the part they must act on.
/// </summary>
public sealed record ScheduleRunFailed(
    Ulid OrgId,
    Ulid ProjectId,
    Ulid ScheduleId,
    string ScheduleName,
    string RecipientUserId,
    string Error,
    bool Disabled) : IntegrationEvent;
