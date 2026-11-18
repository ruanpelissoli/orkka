namespace Orkka.Modules.Agents.Features.Schedules;

using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Domain;
using Orkka.Modules.Agents.Infrastructure;
using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Security;

/// <summary>Validation shared by create and update: turns a request into field values, or a
/// list of errors. Reminders are agent-authored (see the reminder tools) and can't be created
/// from the API; they can be listed and deleted.</summary>
internal static class ScheduleValidation
{
    public sealed record Parsed(
        ScheduleKind Kind, string Name, string? Cron, string TimeZoneId, DateTimeOffset? FireAt,
        Ulid? AgentId, Ulid? SkillId, string? Message, decimal? CapUsd, DateTimeOffset? NextRunAt);

    public static async Task<(Parsed? Value, List<(string Field, string Message)> Errors)> ParseAsync(
        UpsertScheduleRequest req, AgentsDbContext db, ICurrentUser user, Ulid projectId, CancellationToken ct)
    {
        var errors = new List<(string, string)>();
        var name = req.Name?.Trim() ?? "";
        if (name.Length is 0 or > ScheduleRules.NameMax)
            errors.Add((nameof(req.Name), $"Name is required (max {ScheduleRules.NameMax} characters)."));

        if (!Enum.TryParse<ScheduleKind>(req.Kind, ignoreCase: true, out var kind) || kind == ScheduleKind.Reminder)
            errors.Add((nameof(req.Kind), "Kind must be Playbook, AutopilotStart or AutopilotStop."));

        // Timing: exactly one of cron / fireAt.
        string? cron = null;
        var tzId = string.IsNullOrWhiteSpace(req.TimeZoneId) ? "UTC" : req.TimeZoneId.Trim();
        DateTimeOffset? nextRun = null;
        var hasCron = !string.IsNullOrWhiteSpace(req.CronExpression);
        var hasFireAt = req.FireAt is not null;
        if (hasCron == hasFireAt)
            errors.Add((nameof(req.CronExpression), "Provide either a cron expression (recurring) or a fireAt time (one-shot)."));
        else if (hasCron)
        {
            var parsed = ScheduleRules.TryParseCron(req.CronExpression, out var cronError);
            var tz = ScheduleRules.TryResolveTimeZone(tzId, out var tzError);
            if (parsed is null) errors.Add((nameof(req.CronExpression), cronError!));
            if (tz is null) errors.Add((nameof(req.TimeZoneId), tzError!));
            if (parsed is not null && tz is not null)
            {
                if (kind == ScheduleKind.Playbook && ScheduleRules.FiresTooOften(parsed, tz, DateTimeOffset.UtcNow))
                    errors.Add((nameof(req.CronExpression), $"Playbooks may run at most every {ScheduleRules.MinPlaybookInterval.TotalMinutes:0} minutes."));
                cron = req.CronExpression!.Trim();
                nextRun = ScheduleRules.NextOccurrence(parsed, tz, DateTimeOffset.UtcNow);
                if (nextRun is null) errors.Add((nameof(req.CronExpression), "That cron expression never fires."));
            }
        }
        else
        {
            if (req.FireAt <= DateTimeOffset.UtcNow.AddSeconds(30))
                errors.Add((nameof(req.FireAt), "fireAt must be in the future."));
            nextRun = req.FireAt?.ToUniversalTime();
        }

        Ulid? agentId = null, skillId = null;
        string? message = null;
        decimal? cap = null;

        if (kind == ScheduleKind.Playbook)
        {
            if (!Ulid.TryParse(req.AgentId, out var parsedAgent))
                errors.Add((nameof(req.AgentId), "A playbook needs an agent."));
            else if (!await db.ProjectAgents.AnyAsync(a => a.Id == parsedAgent && a.ProjectId == projectId && a.OrgId == user.OrgId, ct))
                errors.Add((nameof(req.AgentId), "The agent doesn't belong to this project."));
            else agentId = parsedAgent;

            if (!string.IsNullOrWhiteSpace(req.SkillId))
            {
                if (!Ulid.TryParse(req.SkillId, out var parsedSkill))
                    errors.Add((nameof(req.SkillId), "SkillId must be a valid id."));
                else if (!await db.AgentSkills.AnyAsync(s => s.Id == parsedSkill && s.ProjectId == projectId && s.OrgId == user.OrgId, ct))
                    errors.Add((nameof(req.SkillId), "The skill doesn't belong to this project."));
                else skillId = parsedSkill;
            }

            message = req.Message?.Trim();
            if (message?.Length > ScheduleRules.MessageMax)
                errors.Add((nameof(req.Message), $"Message must be at most {ScheduleRules.MessageMax} characters."));
            if (skillId is null && string.IsNullOrWhiteSpace(message))
                errors.Add((nameof(req.Message), "A playbook needs a skill, a message, or both."));
        }
        else if (kind == ScheduleKind.AutopilotStart)
        {
            if (req.AutopilotCapUsd is { } c)
            {
                if (c <= 0) errors.Add((nameof(req.AutopilotCapUsd), "The cap must be positive."));
                else cap = c;
            }
            else if (!req.Uncapped)
                errors.Add((nameof(req.AutopilotCapUsd), "Set a spend cap, or explicitly opt into an uncapped session."));
            else if (!user.HasPermission(Permission.BillingManage))
                errors.Add((nameof(req.Uncapped), "Only a member with BillingManage can schedule an uncapped autopilot session."));
        }

        if (errors.Count > 0) return (null, errors);
        return (new Parsed(kind, name, cron, tzId, hasFireAt ? req.FireAt?.ToUniversalTime() : null, agentId, skillId, message, cap, nextRun), errors);
    }
}

public sealed class ListSchedulesEndpoint(
    AgentsDbContext db, ICurrentUser currentUser, IProjectResolver projectResolver)
    : EndpointWithoutRequest<List<ScheduleResponse>>
{
    public override void Configure() => Get("/api/projects/{projectId}/schedules");

    public override async Task HandleAsync(CancellationToken ct)
    {
        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        var rows = await db.AgentSchedules.AsNoTracking()
            .Where(s => s.OrgId == currentUser.OrgId && s.ProjectId == projectId)
            .OrderBy(s => s.Kind == ScheduleKind.Reminder ? 1 : 0)
            .ThenBy(s => s.Name)
            .ToListAsync(ct);
        await Send.OkAsync(rows.Select(r => r.ToResponse()).ToList(), ct);
    }
}

public sealed class CreateScheduleEndpoint(
    AgentsDbContext db, ICurrentUser currentUser, IProjectResolver projectResolver, IAgentAuditLog audit)
    : Endpoint<UpsertScheduleRequest, ScheduleResponse>
{
    public override void Configure() => Post("/api/projects/{projectId}/schedules");

    public override async Task HandleAsync(UpsertScheduleRequest req, CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.TasksExecute))
            return;
        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var count = await db.AgentSchedules.CountAsync(
            s => s.OrgId == currentUser.OrgId && s.ProjectId == projectId && s.Kind != ScheduleKind.Reminder, ct);
        if (count >= ScheduleRules.MaxSchedulesPerProject)
        {
            ThrowError($"A project can have at most {ScheduleRules.MaxSchedulesPerProject} schedules.", 409);
            return;
        }

        var (parsed, errors) = await ScheduleValidation.ParseAsync(req, db, currentUser, projectId, ct);
        if (parsed is null)
        {
            foreach (var (field, message) in errors) AddError(field, message);
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        var schedule = new AgentSchedule
        {
            Id = Ulid.NewUlid(),
            OrgId = currentUser.OrgId,
            ProjectId = projectId,
            Name = parsed.Name,
            Kind = parsed.Kind,
            IsEnabled = req.IsEnabled,
            CronExpression = parsed.Cron,
            TimeZoneId = parsed.TimeZoneId,
            FireAt = parsed.FireAt,
            NextRunAt = req.IsEnabled ? parsed.NextRunAt : null,
            AgentId = parsed.AgentId,
            SkillId = parsed.SkillId,
            Message = parsed.Message,
            AutopilotCapUsd = parsed.CapUsd,
            CreatedByUserId = currentUser.UserId,
            RequesterLocale = currentUser.Locale,
        };
        db.AgentSchedules.Add(schedule);
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync(AgentAuditRecord.ByUser(
            currentUser.OrgId, currentUser.UserId, AgentAuditEventType.ScheduleChanged,
            $"Schedule \"{schedule.Name}\" ({schedule.Kind}) created by {currentUser.UserId}",
            projectId: projectId, agentId: schedule.AgentId,
            metadata: new { scheduleId = schedule.Id.ToString(), change = "created", kind = schedule.Kind.ToString(), schedule.CronExpression, schedule.TimeZoneId, schedule.FireAt, schedule.IsEnabled, schedule.AutopilotCapUsd }), ct);

        await Send.CreatedAtAsync<GetScheduleEndpoint>(
            new { projectId = Route<string>("projectId"), scheduleId = schedule.Id },
            schedule.ToResponse(), cancellation: ct);
    }
}

public sealed class GetScheduleEndpoint(
    AgentsDbContext db, ICurrentUser currentUser, IProjectResolver projectResolver)
    : EndpointWithoutRequest<ScheduleResponse>
{
    public override void Configure() => Get("/api/projects/{projectId}/schedules/{scheduleId:ulid}");

    public override async Task HandleAsync(CancellationToken ct)
    {
        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        var scheduleId = Ulid.Parse(Route<string>("scheduleId"));
        var schedule = await db.AgentSchedules.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == scheduleId && s.ProjectId == projectId && s.OrgId == currentUser.OrgId, ct);
        if (schedule is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        await Send.OkAsync(schedule.ToResponse(), ct);
    }
}

public sealed class UpdateScheduleEndpoint(
    AgentsDbContext db, ICurrentUser currentUser, IProjectResolver projectResolver, IAgentAuditLog audit)
    : Endpoint<UpsertScheduleRequest, ScheduleResponse>
{
    public override void Configure() => Put("/api/projects/{projectId}/schedules/{scheduleId:ulid}");

    public override async Task HandleAsync(UpsertScheduleRequest req, CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.TasksExecute))
            return;
        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        var scheduleId = Ulid.Parse(Route<string>("scheduleId"));
        var schedule = await db.AgentSchedules
            .FirstOrDefaultAsync(s => s.Id == scheduleId && s.ProjectId == projectId && s.OrgId == currentUser.OrgId, ct);
        if (schedule is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        if (schedule.Kind == ScheduleKind.Reminder)
        {
            ThrowError("Reminders are managed by the agent that set them — delete it instead.", 409);
            return;
        }

        var (parsed, errors) = await ScheduleValidation.ParseAsync(req, db, currentUser, projectId, ct);
        if (parsed is null)
        {
            foreach (var (field, message) in errors) AddError(field, message);
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        schedule.Name = parsed.Name;
        schedule.Kind = parsed.Kind;
        schedule.CronExpression = parsed.Cron;
        schedule.TimeZoneId = parsed.TimeZoneId;
        schedule.FireAt = parsed.FireAt;
        schedule.AgentId = parsed.AgentId;
        schedule.SkillId = parsed.SkillId;
        schedule.Message = parsed.Message;
        schedule.AutopilotCapUsd = parsed.CapUsd;
        schedule.IsEnabled = req.IsEnabled;
        // Re-enabling clears a failure streak; timing edits recompute the next firing.
        if (req.IsEnabled) schedule.ConsecutiveFailures = 0;
        schedule.NextRunAt = req.IsEnabled ? parsed.NextRunAt : null;
        var changedFields = db.Entry(schedule).Properties
            .Where(p => p.IsModified && p.Metadata.Name is not ("UpdatedAt" or "NextRunAt" or "ConsecutiveFailures"))
            .Select(p => p.Metadata.Name)
            .ToList();
        await db.SaveChangesAsync(ct);

        if (changedFields.Count > 0)
            await audit.RecordAsync(AgentAuditRecord.ByUser(
                currentUser.OrgId, currentUser.UserId, AgentAuditEventType.ScheduleChanged,
                $"Schedule \"{schedule.Name}\" ({schedule.Kind}) updated by {currentUser.UserId}: {string.Join(", ", changedFields)}",
                projectId: projectId, agentId: schedule.AgentId,
                metadata: new { scheduleId = schedule.Id.ToString(), change = "updated", fields = changedFields, kind = schedule.Kind.ToString(), schedule.CronExpression, schedule.IsEnabled }), ct);

        await Send.OkAsync(schedule.ToResponse(), ct);
    }
}

/// <summary>Enable/disable without re-sending the whole definition — the toggle on the list.</summary>
public sealed record ToggleScheduleRequest(bool IsEnabled);

public sealed class ToggleScheduleEndpoint(
    AgentsDbContext db, ICurrentUser currentUser, IProjectResolver projectResolver, IAgentAuditLog audit)
    : Endpoint<ToggleScheduleRequest, ScheduleResponse>
{
    public override void Configure() => Patch("/api/projects/{projectId}/schedules/{scheduleId:ulid}/enabled");

    public override async Task HandleAsync(ToggleScheduleRequest req, CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.TasksExecute))
            return;
        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        var scheduleId = Ulid.Parse(Route<string>("scheduleId"));
        var schedule = await db.AgentSchedules
            .FirstOrDefaultAsync(s => s.Id == scheduleId && s.ProjectId == projectId && s.OrgId == currentUser.OrgId, ct);
        if (schedule is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        schedule.IsEnabled = req.IsEnabled;
        if (req.IsEnabled)
        {
            schedule.ConsecutiveFailures = 0;
            schedule.NextRunAt = ScheduleRunner.ComputeNextRun(schedule, DateTimeOffset.UtcNow);
        }
        else
        {
            schedule.NextRunAt = null;
        }
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync(AgentAuditRecord.ByUser(
            currentUser.OrgId, currentUser.UserId, AgentAuditEventType.ScheduleChanged,
            $"Schedule \"{schedule.Name}\" ({schedule.Kind}) {(req.IsEnabled ? "enabled" : "disabled")} by {currentUser.UserId}",
            projectId: projectId, agentId: schedule.AgentId,
            metadata: new { scheduleId = schedule.Id.ToString(), change = req.IsEnabled ? "enabled" : "disabled", kind = schedule.Kind.ToString() }), ct);

        await Send.OkAsync(schedule.ToResponse(), ct);
    }
}

public sealed class DeleteScheduleEndpoint(
    AgentsDbContext db, ICurrentUser currentUser, IProjectResolver projectResolver, IAgentAuditLog audit)
    : EndpointWithoutRequest
{
    public override void Configure() => Delete("/api/projects/{projectId}/schedules/{scheduleId:ulid}");

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.TasksExecute))
            return;
        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        var scheduleId = Ulid.Parse(Route<string>("scheduleId"));
        var schedule = await db.AgentSchedules
            .FirstOrDefaultAsync(s => s.Id == scheduleId && s.ProjectId == projectId && s.OrgId == currentUser.OrgId, ct);
        if (schedule is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        db.AgentSchedules.Remove(schedule);
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync(AgentAuditRecord.ByUser(
            currentUser.OrgId, currentUser.UserId, AgentAuditEventType.ScheduleChanged,
            $"Schedule \"{schedule.Name}\" ({schedule.Kind}) deleted by {currentUser.UserId}",
            projectId: projectId, agentId: schedule.AgentId,
            metadata: new { scheduleId = schedule.Id.ToString(), change = "deleted", kind = schedule.Kind.ToString() }), ct);

        await Send.NoContentAsync(ct);
    }
}

/// <summary>Fires the schedule immediately (bypasses the Autonomy pause — the human asked).
/// Returns the finished run; playbook turns are awaited so the caller sees the outcome.</summary>
public sealed class RunScheduleNowEndpoint(
    AgentsDbContext db, ICurrentUser currentUser, IProjectResolver projectResolver, ScheduleRunner runner)
    : EndpointWithoutRequest<ScheduleRunResponse>
{
    public override void Configure() => Post("/api/projects/{projectId}/schedules/{scheduleId:ulid}/run");

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.TasksExecute))
            return;
        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        var scheduleId = Ulid.Parse(Route<string>("scheduleId"));
        var schedule = await db.AgentSchedules.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == scheduleId && s.ProjectId == projectId && s.OrgId == currentUser.OrgId, ct);
        if (schedule is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        if (schedule.LeaseExpiresAt is { } lease && lease > DateTimeOffset.UtcNow)
        {
            ThrowError("This schedule is running right now.", 409);
            return;
        }

        // Runs detached from the request's cancellation: a closed tab must not abandon a
        // half-finished playbook the way it would a chat stream.
        var run = await runner.RunAsync(scheduleId, "manual", CancellationToken.None);
        await Send.OkAsync(run.ToResponse(), ct);
    }
}

public sealed class ListScheduleRunsEndpoint(
    AgentsDbContext db, ICurrentUser currentUser, IProjectResolver projectResolver)
    : EndpointWithoutRequest<List<ScheduleRunResponse>>
{
    public override void Configure() => Get("/api/projects/{projectId}/schedules/{scheduleId:ulid}/runs");

    public override async Task HandleAsync(CancellationToken ct)
    {
        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        var scheduleId = Ulid.Parse(Route<string>("scheduleId"));
        var runs = await db.AgentScheduleRuns.AsNoTracking()
            .Where(r => r.OrgId == currentUser.OrgId && r.ProjectId == projectId && r.ScheduleId == scheduleId)
            .OrderByDescending(r => r.StartedAt)
            .Take(50)
            .ToListAsync(ct);
        await Send.OkAsync(runs.Select(r => r.ToResponse()).ToList(), ct);
    }
}
