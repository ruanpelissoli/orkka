namespace Orkka.Modules.Trackers.Features.Imports;

using FastEndpoints;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Trackers.Contracts;
using Orkka.SharedKernel.Security;

public sealed record TrackerImportPreviewRequest(string Key);

public sealed record TrackerImportRequest(string Key, string[]? SubTaskKeys = null);

/// <summary>
/// The import dialog's first step: the ticket typed (a key, an id or a ticket URL), read by key
/// through the project's ticket source on its owner's runner — first the ticket, then the ticket
/// with its sub-tasks — and shown with what importing it would create. Nothing is created.
/// </summary>
public sealed class PreviewTrackerImportEndpoint(
    IProjectResolver projectResolver,
    ITrackerImports imports,
    ICurrentUser currentUser)
    : Endpoint<TrackerImportPreviewRequest, TrackerImportPreviewOutcome>
{
    public override void Configure() => Post("/api/projects/{projectId}/tracker/import/preview");

    public override async Task HandleAsync(TrackerImportPreviewRequest req, CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.TasksCreate))
            return;
        if (await projectResolver.ResolveAsync(Route<string>("projectId")!, ct) is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        await Send.OkAsync(await imports.PreviewAsync(currentUser.OrgId, currentUser.UserId, projectId, req.Key, ct), ct);
    }
}

/// <summary>Queues the import of a ticket (with the picked sub-tasks) for the next poll, made due
/// now. 202 with the pending import; 200 when the ticket is already on the board.</summary>
public sealed class RequestTrackerImportEndpoint(
    IProjectResolver projectResolver,
    ITrackerImports imports,
    ICurrentUser currentUser)
    : Endpoint<TrackerImportRequest, TrackerImportStatus>
{
    public override void Configure() => Post("/api/projects/{projectId}/tracker/import");

    public override async Task HandleAsync(TrackerImportRequest req, CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.TasksCreate))
            return;
        if (await projectResolver.ResolveAsync(Route<string>("projectId")!, ct) is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        var result = await imports.RequestAsync(projectId, currentUser.UserId, req.Key, req.SubTaskKeys ?? [], ct);
        if (result.Import is not { } import)
        {
            ThrowError(result.Error ?? "The ticket could not be imported.", result.StatusCode);
            return;
        }
        await Send.ResponseAsync(import, result.StatusCode, ct);
    }
}

/// <summary>How an import went: pending until a poll makes it, then done (with its task) or failed.</summary>
public sealed class GetTrackerImportEndpoint(
    IProjectResolver projectResolver,
    ITrackerImports imports)
    : EndpointWithoutRequest<TrackerImportStatus>
{
    public override void Configure() => Get("/api/projects/{projectId}/tracker/import/{importId:ulid}");

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (await projectResolver.ResolveAsync(Route<string>("projectId")!, ct) is not { } projectId
            || !Ulid.TryParse(Route<string>("importId"), out var importId)
            || await imports.GetAsync(projectId, importId, ct) is not { } import)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        await Send.OkAsync(import, ct);
    }
}
