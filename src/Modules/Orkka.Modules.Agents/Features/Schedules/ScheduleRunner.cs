namespace Orkka.Modules.Agents.Features.Schedules;

using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Domain;
using Orkka.Modules.Agents.Features.AgentChat;
using Orkka.Modules.Agents.Features.Skills;
using Orkka.Modules.Agents.Infrastructure;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.SharedKernel.Events;
using Orkka.SharedKernel.Events.Catalog;

/// <summary>
/// Executes one firing of a schedule (already claimed by the caller): records the run row,
/// performs the kind's action, and settles the schedule (next occurrence, failure count,
/// auto-disable). Scoped — the processor and the "run now" endpoint each resolve one per
/// firing. Never throws for a failed action; failures are recorded on the run.
/// </summary>
public sealed class ScheduleRunner(
    AgentsDbContext db,
    ChatTurnPreparer preparer,
    ChatRunCoordinator coordinator,
    ITasksApi tasksApi,
    IProjectsApi projectsApi,
    IEventBus eventBus,
    ILogger<ScheduleRunner> logger)
{
    /// <summary>Playbook replies are summarized to this many characters on the run row.</summary>
    private const int SummaryChars = 600;
    /// <summary>A playbook turn that hasn't finished by then is abandoned by the schedule
    /// (the chat run itself keeps going detached until the invoker's own limits stop it).</summary>
    private static readonly TimeSpan PlaybookWait = TimeSpan.FromMinutes(30);

    public async Task<AgentScheduleRun> RunAsync(Ulid scheduleId, string trigger, CancellationToken ct)
    {
        // Sweep scope: no ICurrentUser, so the tenant filter is off — every query below
        // carries the ids explicitly.
        var schedule = await db.AgentSchedules.IgnoreQueryFilters()
            .FirstAsync(s => s.Id == scheduleId, ct);

        var run = new AgentScheduleRun
        {
            Id = Ulid.NewUlid(),
            OrgId = schedule.OrgId,
            ScheduleId = schedule.Id,
            ProjectId = schedule.ProjectId,
            StartedAt = DateTimeOffset.UtcNow,
            Trigger = trigger,
        };
        db.AgentScheduleRuns.Add(run);
        schedule.LastRunAt = run.StartedAt;
        schedule.LastRunStatus = ScheduleRunStatus.Running;
        schedule.RunCount++;
        await db.SaveChangesAsync(ct);

        try
        {
            var project = await projectsApi.GetProjectSummaryAsync(schedule.ProjectId, ct);
            if (project is null)
            {
                Finish(run, ScheduleRunStatus.Skipped, "Project no longer exists.");
                schedule.IsEnabled = false;
            }
            else if (project.Settings is { AutomationEnabled: false } && trigger != "manual")
            {
                Finish(run, ScheduleRunStatus.Skipped, "Automation is paused for this project.");
            }
            else
            {
                switch (schedule.Kind)
                {
                    case ScheduleKind.Playbook:
                    case ScheduleKind.Reminder:
                        await RunTurnAsync(schedule, run, ct);
                        break;
                    case ScheduleKind.AutopilotStart:
                        await StartAutopilotAsync(schedule, run, ct);
                        break;
                    case ScheduleKind.AutopilotStop:
                        await StopAutopilotAsync(schedule, run, ct);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Finish(run, ScheduleRunStatus.Failed, error: "Host shutting down.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Schedule {ScheduleId} ({Kind}) failed", schedule.Id, schedule.Kind);
            Finish(run, ScheduleRunStatus.Failed, error: Truncate(ex.GetBaseException().Message, 500));
        }

        Settle(schedule, run);
        await db.SaveChangesAsync(CancellationToken.None);

        if (run.Status == ScheduleRunStatus.Failed)
        {
            await eventBus.PublishAsync(new ScheduleRunFailed(
                schedule.OrgId, schedule.ProjectId, schedule.Id, schedule.Name,
                schedule.CreatedByUserId, run.Error ?? "unknown error", Disabled: !schedule.IsEnabled), CancellationToken.None);
        }

        return run;
    }

    // ── kinds ───────────────────────────────────────────────────────────

    private async Task RunTurnAsync(AgentSchedule schedule, AgentScheduleRun run, CancellationToken ct)
    {
        if (schedule.AgentId is not { } agentId)
        {
            Finish(run, ScheduleRunStatus.Failed, error: "The schedule has no agent.");
            return;
        }
        var agent = await db.ProjectAgents.IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.Id == agentId && a.OrgId == schedule.OrgId, ct);
        if (agent is null)
        {
            Finish(run, ScheduleRunStatus.Failed, error: "The agent no longer exists.");
            return;
        }

        var conversation = await ResolveConversationAsync(schedule, agent, ct);
        run.ConversationId = conversation.Id;

        if (coordinator.IsRunning(conversation.Id))
        {
            Finish(run, ScheduleRunStatus.Skipped, "The agent is still busy with the previous turn in this conversation.");
            return;
        }

        // Skill (playbooks): invoked explicitly, exactly like a typed /slug.
        AgentSkill? skill = null;
        if (schedule.SkillId is { } skillId)
        {
            skill = await db.AgentSkills.IgnoreQueryFilters()
                .FirstOrDefaultAsync(s => s.Id == skillId && s.OrgId == schedule.OrgId && s.IsEnabled, ct);
            if (skill is null && string.IsNullOrWhiteSpace(schedule.Message))
            {
                Finish(run, ScheduleRunStatus.Failed, error: "The schedule's skill is missing or disabled and there is no message to run.");
                return;
            }
        }

        var content = BuildTurnContent(schedule, skill);

        // The turn's input is persisted as a user message so the conversation reads as a
        // real exchange on the Team page ("⏰ Reminder: …" / "▶ Playbook: …").
        var userMessage = new AgentMessage
        {
            OrgId = schedule.OrgId,
            ConversationId = conversation.Id,
            Role = "user",
            Content = content,
        };
        db.AgentMessages.Add(userMessage);
        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        var spec = await preparer.PrepareAsync(new ChatTurnInput(
            schedule.OrgId,
            schedule.CreatedByUserId,
            schedule.RequesterLocale,
            schedule.ProjectId,
            agent,
            conversation,
            content,
            ExcludeMessageId: userMessage.Id,
            InvokedSkillBlock: skill is null ? null : SkillInvocation.Block(skill)), ct);

        conversation.ActiveRunStartedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        var handle = coordinator.TryStartRun(spec);
        if (handle is null)
        {
            conversation.ActiveRunStartedAt = null;
            Finish(run, ScheduleRunStatus.Skipped, "The agent is still busy with the previous turn in this conversation.");
            return;
        }

        // Nobody is watching this stream: mark it detached so completion raises the usual
        // "agent replied" notification for the schedule's owner, then drain the events for
        // the run summary.
        handle.MarkDetached();
        var (text, error) = await DrainAsync(handle, ct);

        if (error is not null && string.IsNullOrWhiteSpace(text))
            Finish(run, ScheduleRunStatus.Failed, error: Truncate(error, 500));
        else
            Finish(run, ScheduleRunStatus.Succeeded, Truncate(text.Trim(), SummaryChars));
    }

    private async Task StartAutopilotAsync(AgentSchedule schedule, AgentScheduleRun run, CancellationToken ct)
    {
        var status = await tasksApi.GetAutopilotStatusAsync(schedule.OrgId, schedule.ProjectId, ct);
        if (status.Running)
        {
            Finish(run, ScheduleRunStatus.Skipped, "Autopilot was already running.");
            return;
        }
        var sessionId = await tasksApi.StartAutopilotSessionAsync(
            schedule.OrgId, schedule.ProjectId, schedule.CreatedByUserId, schedule.AutopilotCapUsd,
            // A scheduled window has nobody at the start card to make this choice, so it keeps
            // the guardrails: routine questions answered, guarded ones parked for a person.
            answerEveryQuestion: false, ct);
        Finish(run,
            sessionId is null ? ScheduleRunStatus.Skipped : ScheduleRunStatus.Succeeded,
            sessionId is null
                ? "Autopilot could not be started (another session opened first)."
                : schedule.AutopilotCapUsd is { } cap ? $"Autopilot started with a ${cap:0.##} cap." : "Autopilot started (uncapped).");
    }

    private async Task StopAutopilotAsync(AgentSchedule schedule, AgentScheduleRun run, CancellationToken ct)
    {
        var status = await tasksApi.GetAutopilotStatusAsync(schedule.OrgId, schedule.ProjectId, ct);
        if (!status.Running || status.SessionId is not { } sessionId)
        {
            Finish(run, ScheduleRunStatus.Skipped, "Autopilot was not running.");
            return;
        }
        var stopped = await tasksApi.StopAutopilotSessionAsync(sessionId, AutopilotStopReason.Manual, ct);
        Finish(run, ScheduleRunStatus.Succeeded, stopped ? "Autopilot stopped." : "Autopilot had already stopped.");
    }

    // ── helpers ─────────────────────────────────────────────────────────

    /// <summary>Playbooks get one dedicated conversation per schedule (created on first run,
    /// re-created if deleted); reminders reuse the conversation they were set from.</summary>
    private async Task<AgentConversation> ResolveConversationAsync(
        AgentSchedule schedule, ProjectAgent agent, CancellationToken ct)
    {
        if (schedule.ConversationId is { } existingId)
        {
            var existing = await db.AgentConversations.IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.Id == existingId && c.OrgId == schedule.OrgId && c.AgentId == agent.Id, ct);
            if (existing is not null) return existing;
        }

        var conversation = new AgentConversation
        {
            Id = Ulid.NewUlid(),
            OrgId = schedule.OrgId,
            ProjectId = schedule.ProjectId,
            AgentId = agent.Id,
            Title = schedule.Kind == ScheduleKind.Playbook ? $"⏱ {schedule.Name}" : null,
        };
        db.AgentConversations.Add(conversation);
        schedule.ConversationId = conversation.Id;
        await db.SaveChangesAsync(ct);
        return conversation;
    }

    internal static string BuildTurnContent(AgentSchedule schedule, AgentSkill? skill)
    {
        var message = schedule.Message?.Trim() ?? "";
        if (schedule.Kind == ScheduleKind.Reminder)
            return $"⏰ Reminder you set for yourself: {message}";

        var sb = new StringBuilder();
        if (skill is not null) sb.Append('/').Append(skill.Slug).Append(' ');
        if (message.Length > 0) sb.Append(message);
        else if (skill is not null) sb.Append("Run this playbook now for the scheduled check-in.");
        return sb.ToString().Trim();
    }

    /// <summary>Reads the run's SSE payloads until it completes, collecting reply text and
    /// the last error. Bounded by <see cref="PlaybookWait"/>.</summary>
    private static async Task<(string Text, string? Error)> DrainAsync(ChatRunHandle handle, CancellationToken ct)
    {
        var text = new StringBuilder();
        string? error = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(PlaybookWait);
        try
        {
            await foreach (var payload in handle.Events.ReadAllAsync(timeout.Token))
            {
                var json = JsonSerializer.SerializeToElement(payload);
                if (json.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                    text.Append(t.GetString());
                if (json.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
                    error = e.GetString();
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            error ??= $"The run did not finish within {PlaybookWait.TotalMinutes:0} minutes.";
        }
        return (text.ToString(), error);
    }

    private static void Finish(AgentScheduleRun run, ScheduleRunStatus status, string? summary = null, string? error = null)
    {
        run.Status = status;
        run.FinishedAt = DateTimeOffset.UtcNow;
        run.Summary = summary;
        run.Error = error;
    }

    /// <summary>Next occurrence, failure streak, auto-disable, lease release.</summary>
    private static void Settle(AgentSchedule schedule, AgentScheduleRun run)
    {
        schedule.LastRunStatus = run.Status;
        schedule.ConsecutiveFailures = run.Status == ScheduleRunStatus.Failed ? schedule.ConsecutiveFailures + 1 : 0;
        if (schedule.ConsecutiveFailures >= ScheduleRules.AutoDisableAfterFailures)
            schedule.IsEnabled = false;

        // One-shots (reminders, run-once) are spent after a single firing.
        if (schedule.CronExpression is null)
            schedule.IsEnabled = false;

        schedule.LeaseOwner = null;
        schedule.LeaseExpiresAt = null;
        schedule.NextRunAt = ComputeNextRun(schedule, DateTimeOffset.UtcNow);
    }

    /// <summary>One-shots run once; recurring rows advance from now (misfires collapse into
    /// a single run rather than a burst).</summary>
    public static DateTimeOffset? ComputeNextRun(AgentSchedule schedule, DateTimeOffset now)
    {
        if (!schedule.IsEnabled) return null;
        if (schedule.CronExpression is null)
            return schedule.RunCount == 0 && schedule.FireAt is { } fireAt && fireAt > now ? fireAt : null;

        var cron = ScheduleRules.TryParseCron(schedule.CronExpression, out _);
        var tz = ScheduleRules.TryResolveTimeZone(schedule.TimeZoneId, out _);
        if (cron is null || tz is null) return null;
        return ScheduleRules.NextOccurrence(cron, tz, now);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
