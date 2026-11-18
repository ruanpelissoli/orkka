namespace Orkka.Modules.Tasks.Domain;

using Orkka.Modules.Tasks.Contracts;
using Orkka.SharedKernel.Domain;

/// <summary>
/// One stretch of hands-free running, from the moment a user toggles autopilot on to the moment
/// it stops. The session — not the month, and not the project — is the window the spend cap is
/// defined over: "run until $20" is what users mean, and toggling off resets it.
///
/// Sessions are never resumed. Toggling autopilot back on opens a new one with fresh counters,
/// which does mean a determined user can re-arm a spent cap by toggling twice. That is an
/// explicit act and no worse than editing the cap; the org's <c>BudgetConfig.MonthlyLimitUsd</c>
/// remains the ceiling that actually bounds spend.
/// </summary>
public sealed class AutopilotSession : TenantEntity
{
    public Ulid ProjectId { get; set; }

    /// <summary>Who turned it on. The spend is the org's either way — this is for the audit
    /// trail and for addressing the "autopilot stopped" notification.</summary>
    public required string StartedByUserId { get; set; }

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? StoppedAt { get; set; }

    /// <summary><see cref="AutopilotStopReason.Running"/> until it stops. Stored as a string so
    /// adding a reason later never renumbers the existing rows.</summary>
    public AutopilotStopReason StopReason { get; set; } = AutopilotStopReason.Running;

    /// <summary>
    /// The cap as it was when the session opened, in USD. Null = uncapped ("just go"), which
    /// requires <c>BillingManage</c> to set — spending without a bound is a billing decision.
    ///
    /// Snapshotted rather than read live from project settings: editing the setting mid-session
    /// must not silently re-arm a session that already stopped on the old value.
    /// </summary>
    public decimal? CapUsd { get; set; }

    /// <summary>
    /// Answer every question this session's tasks ask, with no guardrail but the per-task loop
    /// cap — questions with no recommendation, and ones about deleting data, billing,
    /// credentials or production, included. Chosen when the session starts, never read live.
    ///
    /// A session property rather than a project setting for the same reason <see cref="CapUsd"/>
    /// is snapshotted: it is a decision about this run ("I'm away tonight, don't stop"), made at
    /// the moment the run starts. As a project setting it sat three screens away from the button
    /// that starts autopilot, and outlived the night it was meant for. Choosing it requires
    /// <c>ProjectsManage</c> — the bar the project setting had — because it lets an agent decide
    /// questions a person would otherwise be asked.
    /// </summary>
    public bool AnswerEveryQuestion { get; set; }

    /// <summary>
    /// Cloud spend attributed to this session, refreshed by the autopilot sweep. UserRunner
    /// tasks contribute nothing — they run on the user's own AI subscriptions and cost us
    /// nothing, which is exactly why the cap does not apply to them.
    ///
    /// A cached projection of <c>UsageRecords</c>, not the source of truth. It exists so the
    /// board can render a live "spent / cap" strip without summing usage on every poll.
    /// </summary>
    public decimal SpentUsd { get; set; }

    public int TasksSubmitted { get; set; }
    public int TasksCompleted { get; set; }
    public int TasksFailed { get; set; }

    /// <summary>Failures since the last success. Reset by any completion — the stop condition
    /// is about a project that cannot make progress, not about a total.</summary>
    public int ConsecutiveFailures { get; set; }
}
