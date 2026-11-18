namespace Orkka.SharedKernel.Events.Catalog;

/// <summary>
/// Published by the Tasks module when the last open task under a milestone merges and the
/// milestone auto-completes.
///
/// The reaction is deliberately a *suggestion* to plan what's next, not auto-created tasks —
/// each task an agent runs costs real money, and spending it because a milestone happened to
/// close is not a decision the platform should make for someone. Same reasoning as the
/// propose/accept split on milestone planning.
/// </summary>
public sealed record MilestoneCompleted(
    Ulid MilestoneId,
    Ulid OrgId,
    Ulid ProjectId,
    string Title,
    /// <summary>Who created the milestone — the person the suggestion is addressed to. Null
    /// for milestones seeded by provisioning, in which case nobody is notified.</summary>
    string? CreatedByUserId,
    int TaskCount) : IntegrationEvent;
