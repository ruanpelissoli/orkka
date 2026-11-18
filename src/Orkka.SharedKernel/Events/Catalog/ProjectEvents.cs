namespace Orkka.SharedKernel.Events.Catalog;

/// <summary>
/// Published by the Projects module when a project row is created. Consumed by the
/// agent-seeding handler and project-scoped membership. Handlers must be idempotent.
/// </summary>
/// <param name="CreatedByUserId">user id of whoever created it. Carried so the
/// Organizations module can put them on the project when the org runs project-scoped
/// membership — otherwise creating a project would immediately hide it from its author.
/// Trailing default: null means "unknown creator", which simply skips that step.</param>
public sealed record ProjectCreated(
    Ulid ProjectId,
    Ulid OrgId,
    string? CreatedByUserId = null) : IntegrationEvent;

/// <summary>
/// Published by the Projects module when a project is deleted. Cross-module FKs no longer
/// exist, so modules owning project-scoped data (tasks, agents, conversations, diagrams)
/// clean up their own rows in response.
/// </summary>
public sealed record ProjectDeleted(
    Ulid ProjectId,
    Ulid OrgId) : IntegrationEvent;
