namespace Orkka.Modules.Projects.Features.RunConfig;

using FastEndpoints;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Projects.Infrastructure.Services;
using Orkka.SharedKernel.Ai;
using Orkka.SharedKernel.Security;

/// <param name="Stale">True when the repos changed since detection — the UI offers a
/// re-detect rather than doing one, so nobody pays for a model call they did not ask
/// for.</param>
public sealed record RunConfigResponse(
    RunTargetDto[] Targets,
    string? Notes,
    string Source,
    DateTimeOffset? DetectedAt,
    bool Stale,
    bool Detected);

public sealed record SaveRunConfigRequest(RunTargetDto[] Targets, string? Notes);

internal static class RunConfigMapper
{
    public static RunConfigResponse ToResponse(this ProjectRunConfigDto? dto) => dto is null
        // "Never detected" is a first-class state, not a 404: the Run panel renders it as
        // the "work out how to run this" call to action.
        ? new RunConfigResponse([], null, "ai", null, false, false)
        : new RunConfigResponse(
            dto.Targets,
            dto.Notes,
            dto.Source switch
            {
                RunConfigSource.Manual => "manual",
                RunConfigSource.Manifest => "manifest",
                _ => "ai",
            },
            dto.DetectedAt,
            dto.Stale,
            true);
}

/// <summary>What Run would start, as detected earlier. Cheap and read-only — no model
/// call — so the Run panel can open instantly.</summary>
public sealed class GetRunConfigEndpoint(
    IRunConfigDetector detector,
    ICurrentUser currentUser,
    IProjectResolver projectResolver)
    : EndpointWithoutRequest<RunConfigResponse>
{
    public override void Configure()
    {
        Get("/api/projects/{projectId}/run-config");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var config = await detector.GetAsync(currentUser.OrgId, projectId, ct);
        await Send.OkAsync(config.ToResponse(), ct);
    }
}

/// <summary>
/// Reads the repos and works out how to run them. The only endpoint here that spends
/// tokens, which is why it is a POST the user presses rather than something the Run panel
/// does on open — and why an unforced call returns the stored answer untouched when the
/// repos have not changed.
/// </summary>
public sealed class DetectRunConfigEndpoint(
    IRunConfigDetector detector,
    ICurrentUser currentUser,
    IProjectResolver projectResolver)
    : EndpointWithoutRequest<RunConfigResponse>
{
    public override void Configure()
    {
        Post("/api/projects/{projectId}/run-config/detect");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.ReposManage))
            return;

        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var force = Query<bool?>("force", isRequired: false) ?? false;
        try
        {
            var config = await detector.DetectAsync(
                new RunConfigDetectionRequest(
                    currentUser.OrgId, projectId, currentUser.UserId, force),
                ct);
            await Send.OkAsync(config.ToResponse(), ct);
        }
        catch (RunConfigDetectionException ex)
        {
            // The user can act on these ("link a repository") — 422 rather than a 500.
            ThrowError(ex.Message, 422);
        }
        catch (ModelOutputException ex)
        {
            // The raw reply is already logged by the detector; the caller gets the reason
            // without the thousands of characters that caused it.
            ThrowError(ex.Message, 422);
        }
    }
}

/// <summary>Replaces the detected commands with the user's own — the escape hatch for when
/// detection got a command subtly wrong, which no amount of prompt work makes impossible.
/// </summary>
public sealed class SaveRunConfigEndpoint(
    IRunConfigDetector detector,
    ICurrentUser currentUser,
    IProjectResolver projectResolver)
    : Endpoint<SaveRunConfigRequest, RunConfigResponse>
{
    public override void Configure()
    {
        Put("/api/projects/{projectId}/run-config");
    }

    public override async Task HandleAsync(SaveRunConfigRequest req, CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.ReposManage))
            return;

        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var config = await detector.SaveAsync(
            currentUser.OrgId, projectId, req.Targets ?? [], req.Notes, ct);
        await Send.OkAsync(config.ToResponse(), ct);
    }
}
