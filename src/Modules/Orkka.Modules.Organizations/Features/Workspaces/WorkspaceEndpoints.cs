namespace Orkka.Modules.Organizations.Features.Workspaces;

using FastEndpoints;
using FluentValidation;
using Orkka.Modules.Organizations.Contracts;
using Orkka.SharedKernel.Caching;
using Orkka.SharedKernel.Security;

/// <summary>One of the caller's workspaces. A developer working several jobs keeps one per job;
/// each is an ordinary org with its own projects, members, runner tokens and ticket sources.</summary>
public sealed record WorkspaceResponse(
    Ulid OrgId, string Name, string Slug, string Role, bool IsActive);

public sealed record CreateWorkspaceRequest(string Name);

public sealed record SetActiveWorkspaceRequest(Ulid OrgId);

internal static class WorkspaceMappers
{
    public static WorkspaceResponse ToResponse(this WorkspaceInfo w)
        => new(w.OrgId, w.Name, w.Slug, w.Role.ToString(), w.IsActive);

    /// <summary>The active workspace changed — the membership a request without a workspace
    /// header resolves to is stale.</summary>
    public static async Task ForgetActiveAsync(this ICacheInvalidator invalidator, string userId, CancellationToken ct)
        => await invalidator.RemoveAsync(CacheKeys.Membership(userId), ct);
}

/// <summary>The caller's workspaces, whichever one the request was made in.</summary>
public sealed class ListMyWorkspacesEndpoint(IOrgMembershipService membership, ICurrentUser currentUser)
    : EndpointWithoutRequest<WorkspaceResponse[]>
{
    public override void Configure() => Get("/api/me/workspaces");

    public override async Task HandleAsync(CancellationToken ct)
    {
        var workspaces = await membership.ListWorkspacesAsync(currentUser.UserId, ct);
        await Send.OkAsync([.. workspaces.Select(w => w.ToResponse())], ct);
    }
}

public sealed class CreateWorkspaceValidator : Validator<CreateWorkspaceRequest>
{
    public CreateWorkspaceValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Name the workspace — usually after the job or client.")
            .MaximumLength(100);
    }
}

/// <summary>
/// A new workspace for another job, owned by the caller and made their active one. Nothing is
/// shared with their other workspaces: projects, runner tokens, tracker sources and members
/// are all per workspace.
/// </summary>
public sealed class CreateWorkspaceEndpoint(
    IOrgMembershipService membership,
    ICurrentUser currentUser,
    ICacheInvalidator invalidator)
    : Endpoint<CreateWorkspaceRequest, WorkspaceResponse>
{
    public override void Configure() => Post("/api/workspaces");

    public override async Task HandleAsync(CreateWorkspaceRequest req, CancellationToken ct)
    {
        var created = await membership.CreateWorkspaceAsync(currentUser.UserId, req.Name, ct);
        await invalidator.ForgetActiveAsync(currentUser.UserId, ct);
        await Send.OkAsync(created.ToResponse(), ct);
    }
}

/// <summary>Which workspace the caller lands in when a request names none — a new tab, the
/// mobile app, a notification link.</summary>
public sealed class SetActiveWorkspaceEndpoint(
    IOrgMembershipService membership,
    ICurrentUser currentUser,
    ICacheInvalidator invalidator)
    : Endpoint<SetActiveWorkspaceRequest>
{
    public override void Configure() => Post("/api/me/active-workspace");

    public override async Task HandleAsync(SetActiveWorkspaceRequest req, CancellationToken ct)
    {
        if (!await membership.SetActiveWorkspaceAsync(currentUser.UserId, req.OrgId, ct))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await invalidator.ForgetActiveAsync(currentUser.UserId, ct);
        await Send.NoContentAsync(ct);
    }
}
