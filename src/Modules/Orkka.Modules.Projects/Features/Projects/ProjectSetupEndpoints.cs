namespace Orkka.Modules.Projects.Features.Projects;

using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Projects.Infrastructure;
using Orkka.SharedKernel.Security;

/// <summary>
/// Confirms (or takes back) one step of the project's guided setup — what the setup page resumes
/// from. Confirming a step changes nothing else: every setting a step shows is saved by its own
/// endpoint, exactly as on the settings pages.
/// </summary>
public sealed class SetProjectSetupStepEndpoint(
    ProjectsDbContext db,
    ICurrentUser currentUser,
    IProjectResolver projectResolver)
    : Endpoint<ProjectSetupStepRequest, ProjectResponse>
{
    public override void Configure() => Put("/api/projects/{projectId}/setup/steps");

    public override async Task HandleAsync(ProjectSetupStepRequest req, CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.ProjectsManage))
            return;
        if (!ProjectSetupSteps.IsConfirmable(req.Step))
        {
            ThrowError(r => r.Step, $"A setup step is one of: {string.Join(", ", ProjectSetupSteps.Confirmable)}.");
            return;
        }
        if (await projectResolver.ResolveAsync(Route<string>("projectId")!, ct) is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId && p.OrgId == currentUser.OrgId, ct);
        if (project is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var steps = Mappers.ParseSetupSteps(project.SetupStepsJson).ToHashSet(StringComparer.Ordinal);
        if (req.Done)
            steps.Add(req.Step);
        else
            steps.Remove(req.Step);
        project.SetupStepsJson = Mappers.SerializeSetupSteps(steps);
        await db.SaveChangesAsync(ct);

        await Send.OkAsync(project.ToResponse(), ct);
    }
}

/// <summary>
/// Start: the end of the guided setup. From now on the project's ticket source is polled and its
/// tickets become tasks (until then the poll skips it — <see cref="IProjectsApi.GetSetupPendingProjectIdsAsync"/>).
/// Idempotent: a project already started keeps its first start time. The readiness checks run
/// on the developer's machine and are shown by the setup page; what the developer chose to start
/// despite them is recorded in the audit trail.
/// </summary>
public sealed class StartProjectSetupEndpoint(
    ProjectsDbContext db,
    ICurrentUser currentUser,
    IProjectResolver projectResolver,
    IAgentAuditLog audit)
    : Endpoint<StartProjectSetupRequest, ProjectResponse>
{
    public override void Configure() => Post("/api/projects/{projectId}/setup/start");

    public override async Task HandleAsync(StartProjectSetupRequest req, CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.ProjectsManage))
            return;
        if (await projectResolver.ResolveAsync(Route<string>("projectId")!, ct) is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId && p.OrgId == currentUser.OrgId, ct);
        if (project is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (project.SetupCompletedAt is null)
        {
            project.SetupCompletedAt = DateTimeOffset.UtcNow;
            project.SetupStepsJson = Mappers.SerializeSetupSteps(ProjectSetupSteps.Confirmable);
            await db.SaveChangesAsync(ct);

            var warnings = (req.Warnings ?? [])
                .Where(w => !string.IsNullOrWhiteSpace(w))
                .Select(w => w.Trim().Length > 300 ? w.Trim()[..300] : w.Trim())
                .Take(50)
                .ToArray();
            await audit.RecordAsync(AgentAuditRecord.ByUser(
                currentUser.OrgId, currentUser.UserId, AgentAuditEventType.ProjectSettingsChanged,
                warnings.Length == 0
                    ? $"Project \"{project.Name}\" setup finished — its ticket source may be polled now"
                    : $"Project \"{project.Name}\" started anyway with {warnings.Length} warning(s)",
                projectId: project.Id,
                metadata: new { setupStarted = true, warnings }), ct);
        }

        await Send.OkAsync(project.ToResponse(), ct);
    }
}
