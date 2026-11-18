namespace Orkka.Modules.Tasks.Features.InteractiveSessions;

using FastEndpoints;
using FluentValidation;
using Orkka.Modules.Tasks.Contracts;
using Orkka.SharedKernel.Security;

public sealed record DecideApprovalRequest(string Decision);

public sealed class DecideApprovalValidator : Validator<DecideApprovalRequest>
{
    private static readonly string[] Allowed = ["allow", "allow_session", "deny"];

    public DecideApprovalValidator()
    {
        RuleFor(x => x.Decision)
            .Must(d => Allowed.Contains(d, StringComparer.OrdinalIgnoreCase))
            .WithMessage("Decision must be 'allow', 'allow_session', or 'deny'.");
    }
}

/// <summary>
/// A human's ruling on a command an interactive session paused for.
///
/// Gated on <see cref="Permission.TasksExecute"/> — approving a command is authorising an agent
/// to run it, which is the same authority as starting a run in the first place.
///
/// Returns 409 when the prompt is already settled rather than pretending to succeed: two people
/// watching the same session can both click, and the second needs to be told their answer did not
/// take effect instead of believing they allowed something they did not.
/// </summary>
public sealed class DecideApprovalEndpoint(
    ICurrentUser currentUser,
    IInteractiveApprovalCoordinator coordinator)
    : Endpoint<DecideApprovalRequest>
{
    public override void Configure()
    {
        Post("/api/tasks/{taskId:ulid}/approvals/{approvalId:ulid}");
    }

    public override async Task HandleAsync(DecideApprovalRequest req, CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.TasksExecute))
            return;

        var taskId = Ulid.Parse(Route<string>("taskId")!);
        var approvalId = Ulid.Parse(Route<string>("approvalId")!);

        var decision = req.Decision.ToLowerInvariant() switch
        {
            "allow" => ApprovalDecision.Allow,
            "allow_session" => ApprovalDecision.AllowForSession,
            _ => ApprovalDecision.Deny,
        };

        var applied = await coordinator.DecideAsync(
            currentUser.OrgId, taskId, approvalId, decision, currentUser.UserId, ct);

        if (!applied)
        {
            await Send.ErrorsAsync(409, ct);
            return;
        }

        await Send.NoContentAsync(ct);
    }
}

/// <summary>Prompts still waiting on this task — what a browser opening the session needs to
/// render before any live event arrives.</summary>
public sealed class ListPendingApprovalsEndpoint(
    ICurrentUser currentUser,
    IInteractiveApprovalCoordinator coordinator)
    : EndpointWithoutRequest<IReadOnlyList<PendingApproval>>
{
    public override void Configure()
    {
        Get("/api/tasks/{taskId:ulid}/approvals");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var taskId = Ulid.Parse(Route<string>("taskId")!);
        var pending = await coordinator.ListPendingAsync(currentUser.OrgId, taskId, ct);
        await Send.OkAsync(pending, ct);
    }
}
