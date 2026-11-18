namespace Orkka.Modules.Agents.Domain;

using Orkka.Modules.Agents.Contracts;
using Orkka.SharedKernel.Domain;

/// <summary>
/// A time-based automation in a project: a recurring playbook run, an autopilot start/stop
/// window, or a one-shot reminder an agent set for itself. Ticked by ScheduleProcessor in
/// the API host (the only host that can run chat turns).
///
/// A schedule row is claimed set-based (LeaseOwner/LeaseExpiresAt) so multiple hosts can
/// share the sweep without double-firing.
/// </summary>
public sealed class AgentSchedule : TenantEntity
{
    public Ulid ProjectId { get; set; }
    public required string Name { get; set; }
    public ScheduleKind Kind { get; set; }
    public bool IsEnabled { get; set; } = true;

    /// <summary>Standard 5-field cron (Cronos). Null for one-shot rows.</summary>
    public string? CronExpression { get; set; }
    /// <summary>IANA or Windows time-zone id the cron is evaluated in. Defaults to UTC.</summary>
    public string TimeZoneId { get; set; } = "UTC";
    /// <summary>One-shot fire time (reminders, run-once). Null for recurring rows.</summary>
    public DateTimeOffset? FireAt { get; set; }

    /// <summary>When the processor should next pick this row up. Null = never (disabled,
    /// exhausted one-shot, or unparsable cron).</summary>
    public DateTimeOffset? NextRunAt { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public ScheduleRunStatus? LastRunStatus { get; set; }

    // Playbook / Reminder: which agent, which skill (optional), what to say.
    public Ulid? AgentId { get; set; }
    public Ulid? SkillId { get; set; }
    public string? Message { get; set; }
    /// <summary>Playbooks: the dedicated conversation each run appends to (created lazily).
    /// Reminders: the conversation the agent set the reminder from.</summary>
    public Ulid? ConversationId { get; set; }

    // AutopilotStart: spend ceiling for the session it opens (null = uncapped; only a
    // BillingManage holder can create such a schedule).
    public decimal? AutopilotCapUsd { get; set; }

    /// <summary>The member whose identity runs are attributed to (cost, permissions).</summary>
    public required string CreatedByUserId { get; set; }
    public string? RequesterLocale { get; set; }

    public int RunCount { get; set; }
    public int ConsecutiveFailures { get; set; }

    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
}

/// <summary>One firing of a schedule — what the Automation page shows under "Runs".</summary>
public sealed class AgentScheduleRun : TenantEntity
{
    public Ulid ScheduleId { get; set; }
    public Ulid ProjectId { get; set; }
    public ScheduleRunStatus Status { get; set; } = ScheduleRunStatus.Running;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    /// <summary>Playbook/reminder runs: the conversation the reply landed in.</summary>
    public Ulid? ConversationId { get; set; }
    /// <summary>Short outcome — the first lines of the reply, "autopilot started", or the error.</summary>
    public string? Summary { get; set; }
    public string? Error { get; set; }
    /// <summary>What triggered it: the cron tick or a manual "run now".</summary>
    public string Trigger { get; set; } = "cron";

    public AgentSchedule Schedule { get; set; } = null!;
}
