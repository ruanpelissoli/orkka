namespace Orkka.Modules.Tasks.Features.CodeReview;

using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.SharedKernel.Security;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.Modules.Agents.Contracts;
using Orkka.SharedKernel.Storage;
using Orkka.SharedKernel.Utilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using Orkka.Modules.Tasks.Features.CodeReview;
using FastEndpoints;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

public sealed class SubmitCodeReviewValidator : Validator<SubmitCodeReviewRequest>
{
    public SubmitCodeReviewValidator()
    {
        RuleFor(x => x.Decision)
            .NotEmpty()
            .Must(d => d.Equals("approve", StringComparison.OrdinalIgnoreCase)
                     || d.Equals("request_changes", StringComparison.OrdinalIgnoreCase)
                     || d.Equals("comment", StringComparison.OrdinalIgnoreCase))
            .WithMessage("Decision must be 'approve', 'request_changes', or 'comment'.");

        RuleFor(x => x.Body)
            .NotEmpty()
            .When(x => x.Decision.Equals("request_changes", StringComparison.OrdinalIgnoreCase))
            .WithMessage("Body is required when requesting changes.");

        RuleFor(x => x.Body)
            .MaximumLength(10000).WithMessage("Body must be 10,000 characters or fewer.");
    }
}

public sealed class SubmitCodeReviewEndpoint(
    ICurrentUser currentUser,
    ITasksApi tasksApi)
    : Endpoint<SubmitCodeReviewRequest>
{
    public override void Configure()
    {
        Post("/api/tasks/{taskId:ulid}/pull-request/reviews");
    }

    public override async Task HandleAsync(SubmitCodeReviewRequest req, CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.CodeReviewReview))
            return;

        var taskId = Ulid.Parse(Route<string>("taskId"));
        var comments = req.Comments?.Select(c => new BoardReviewComment(c.Path, c.Line, c.Body)).ToList();

        var outcome = await tasksApi.SubmitCodeReviewAsync(currentUser.OrgId, taskId, req.Decision, req.Body, comments, ct);
        if (!outcome.Ok)
        {
            await this.SendBoardFailureAsync(outcome, ct);
            return;
        }

        if (outcome.Warning is not null)
        {
            // The review is recorded in Orkka and the lane has moved — say plainly that the forge
            // copy did not land, rather than reporting clean success or failing the whole call.
            await Send.OkAsync(new SubmitCodeReviewResponse(outcome.Warning), ct);
            return;
        }

        await Send.NoContentAsync(ct);
    }
}

/// <summary>Returned only when the forge copy of the review did not land. The task state has
/// still been applied — this exists so the UI can say so instead of implying a clean success.</summary>
public sealed record SubmitCodeReviewResponse(string ForgeWarning);
