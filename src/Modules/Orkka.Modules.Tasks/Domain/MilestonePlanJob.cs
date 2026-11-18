namespace Orkka.Modules.Tasks.Domain;

using Orkka.Modules.Tasks.Contracts;
using Orkka.SharedKernel.Domain;

/// <summary>
/// One queued milestone-planning run.
///
/// Planning used to happen inline in POST /milestones/plan, bound to
/// <c>HttpContext.RequestAborted</c>. A run measured 90s–210s in production, and a user who
/// navigated away after three minutes of a spinner tore down the fetch — which cancelled the
/// relay to their runner mid-generation and surfaced as a 500. The work was already paid for
/// in tokens and there was nothing to resume from.
///
/// This row is what makes the run survive the client: the request enqueues and returns, a
/// background sweep claims and executes it away from any request thread, and the result is
/// persisted so a refresh or a reconnect picks it up.
/// </summary>
public sealed class MilestonePlanJob : TenantEntity
{
    public required Ulid ProjectId { get; set; }

    /// <summary>Planning INTO an existing milestone — its outcome is the goal. Null plans a
    /// new one.</summary>
    public Ulid? MilestoneId { get; set; }

    /// <summary>Repo the plan is built against, already resolved by the endpoint (explicit
    /// pick, else the milestone's own, else the project's first). Persisted rather than
    /// re-derived so the answer cannot drift between enqueue and execution.</summary>
    public Ulid? RepoId { get; set; }

    /// <summary>The goal as the model will see it — for a milestone-scoped plan this is the
    /// prose the endpoint derived from the milestone, not the raw request field.</summary>
    public required string Goal { get; set; }

    /// <summary>user id the token spend is attributed to, and whose runner the call routes
    /// to. Not nullable: without it there is no runner to plan on (and no platform-key
    /// fallback).</summary>
    public required string RequesterUserId { get; set; }

    /// <summary>The requester's locale, used only for projects with no language of their own
    /// (see ProjectLanguage.Resolve).</summary>
    public string? RequesterLocale { get; set; }

    public MilestonePlanStatus Status { get; set; } = MilestonePlanStatus.Pending;

    /// <summary>One of <see cref="MilestonePlanSteps"/>. Pushed to watchers as it changes —
    /// the progress the user sees instead of an indefinite spinner.</summary>
    public string? CurrentStep { get; set; }

    /// <summary>The finished proposal, serialized as the same JSON the endpoint used to
    /// return inline. Opaque to the database on purpose: it is round-tripped, never queried.
    /// </summary>
    public string? ResultJson { get; set; }

    /// <summary>One of <see cref="MilestonePlanFailureCodes"/>. Plain string rather than a
    /// mapped enum so a code added later cannot break rows written by an older worker.</summary>
    public string? FailureCode { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
