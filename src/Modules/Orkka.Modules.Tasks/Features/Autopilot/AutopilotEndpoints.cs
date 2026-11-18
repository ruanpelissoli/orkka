namespace Orkka.Modules.Tasks.Features.Autopilot;

using FastEndpoints;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.SharedKernel.Security;

/// <param name="CapUsd">Cloud spend ceiling for this session. Omitted falls back to the
/// project's saved preference.</param>
/// <param name="Uncapped">Explicit "just go". Required to run with no ceiling — a null
/// <paramref name="CapUsd"/> alone is treated as "not specified", never as "unlimited",
/// because the two are one keystroke apart and only one of them can empty a wallet.</param>
/// <param name="AnswerEveryQuestion">Answer every question the session's tasks ask, guarded
/// topics included, instead of parking the ones autopilot would not decide alone. Requires
/// ProjectsManage.</param>
public sealed record StartAutopilotRequest(
    decimal? CapUsd = null, bool Uncapped = false, bool AnswerEveryQuestion = false);

/// <summary>
/// Turns autopilot on for a project: opens a session the Worker sweep then feeds from Drafting.
///
/// The session is the only "autopilot is running" state there is — this endpoint writes no
/// project settings, so a user's pipeline configuration is untouched by turning it on and off.
/// </summary>
public sealed class StartAutopilotEndpoint(
    ITasksApi tasksApi,
    IProjectResolver projectResolver,
    IProjectsApi projectsApi,
    ITaskNotifier taskNotifier,
    ICurrentUser currentUser)
    : Endpoint<StartAutopilotRequest, AutopilotStatus>
{
    public override void Configure()
    {
        Post("/api/projects/{projectId}/autopilot");
    }

    public override async Task HandleAsync(StartAutopilotRequest req, CancellationToken ct)
    {
        // Autopilot submits tasks, and submitting is what spends money.
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.TasksExecute))
            return;

        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var project = await projectsApi.GetProjectSummaryAsync(projectId, ct);
        if (project is null || project.OrgId != currentUser.OrgId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        decimal? cap;
        if (req.Uncapped)
        {
            // Running with no ceiling is a billing decision, not a task decision. TasksExecute
            // lets you spend against a bound somebody set; removing the bound is a different act.
            if (!currentUser.HasPermission(Permission.BillingManage))
            {
                await Send.ForbiddenAsync(ct);
                return;
            }

            cap = null;
        }
        else
        {
            cap = req.CapUsd ?? project.Settings?.AutopilotCloudCapUsd;

            if (cap is null)
            {
                ThrowError("Set a spend cap, or explicitly choose to run without one.");
                return;
            }

            if (cap <= 0)
            {
                ThrowError("The spend cap must be greater than zero.");
                return;
            }
        }

        // Answering every question lets an agent decide what a person would otherwise be asked,
        // deleting data and touching production included. Starting autopilot needs only
        // TasksExecute; this choice used to be a project setting behind ProjectsManage, and
        // moving it onto the start card must not lower who can make it.
        if (req.AnswerEveryQuestion && !currentUser.HasPermission(Permission.ProjectsManage))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        var sessionId = await tasksApi.StartAutopilotSessionAsync(
            currentUser.OrgId, projectId, currentUser.UserId, cap, req.AnswerEveryQuestion, ct);

        // Null means a session was already open — including one this user's own second click
        // opened. Returning the current status is the honest answer to "turn it on": it is on.
        var status = await tasksApi.GetAutopilotStatusAsync(currentUser.OrgId, projectId, ct);

        if (sessionId is not null)
        {
            await taskNotifier.SendAutopilotChangedAsync(projectId,
                new AutopilotChangedEvent(projectId, status, DateTimeOffset.UtcNow));
        }

        await Send.OkAsync(status, ct);
    }
}

/// <summary>
/// Turns autopilot off. Stops SUBMITTING — it deliberately does not cancel work already in
/// flight: killing a mid-run agent throws away everything already paid for, and those tasks
/// are nearly always cheaper to finish than to restart. Their spend still books to the session
/// that started them.
/// </summary>
public sealed class StopAutopilotEndpoint(
    ITasksApi tasksApi,
    IProjectResolver projectResolver,
    ITaskNotifier taskNotifier,
    ICurrentUser currentUser)
    : EndpointWithoutRequest<AutopilotStatus>
{
    public override void Configure()
    {
        Delete("/api/projects/{projectId}/autopilot");
    }

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

        var current = await tasksApi.GetAutopilotStatusAsync(currentUser.OrgId, projectId, ct);

        if (current is { Running: true, SessionId: { } sessionId })
        {
            await tasksApi.StopAutopilotSessionAsync(sessionId, AutopilotStopReason.Manual, ct);

            var stopped = await tasksApi.GetAutopilotStatusAsync(currentUser.OrgId, projectId, ct);
            await taskNotifier.SendAutopilotChangedAsync(projectId,
                new AutopilotChangedEvent(projectId, stopped, DateTimeOffset.UtcNow));

            await Send.OkAsync(stopped, ct);
            return;
        }

        // Already off. Idempotent rather than an error: the user asked for a state, not a
        // transition, and two people hitting stop should both see "it's off".
        await Send.OkAsync(current, ct);
    }
}

/// <summary>
/// Autopilot's state for the board header. Readable by any org member — reads are open across
/// the platform, and knowing whether the project is running itself is not sensitive.
/// </summary>
public sealed class GetAutopilotStatusEndpoint(
    ITasksApi tasksApi,
    IProjectResolver projectResolver,
    ICurrentUser currentUser)
    : EndpointWithoutRequest<AutopilotStatus>
{
    public override void Configure()
    {
        Get("/api/projects/{projectId}/autopilot");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(
            await tasksApi.GetAutopilotStatusAsync(currentUser.OrgId, projectId, ct), ct);
    }
}
