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

public sealed class AddLineCommentValidator : Validator<AddLineCommentRequest>
{
    public AddLineCommentValidator()
    {
        RuleFor(x => x.Path).NotEmpty().WithMessage("File path is required.");
        RuleFor(x => x.Line).GreaterThan(0).WithMessage("Line number must be greater than 0.");
        RuleFor(x => x.Body).NotEmpty().MaximumLength(10000)
            .WithMessage("Comment body is required and must be 10,000 characters or fewer.");
    }
}

public sealed class AddLineCommentEndpoint(
    TasksDbContext db,
    Orkka.Modules.Projects.Contracts.IProjectsApi projectsApi,
    ICurrentUser currentUser,
    IPullRequestService pullRequests,
    ILogger<AddLineCommentEndpoint> logger)
    : Endpoint<AddLineCommentRequest, PrReviewCommentResponse>
{
    public override void Configure()
    {
        Post("/api/tasks/{taskId:ulid}/pull-request/comments");
    }

    public override async Task HandleAsync(AddLineCommentRequest req, CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.CodeReviewReview))
            return;

        var taskId = Ulid.Parse(Route<string>("taskId"));

        var context = await CodeReviewHelpers.ResolveTaskContextAsync(db, projectsApi, taskId, currentUser.OrgId, ct);
        if (context is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // Get the head SHA from the PR
        var prResult = await pullRequests.GetAsync(
            context.RepoId, context.PrNumber, ct);

        if (prResult.IsFailure)
        {
            logger.LogWarning("Failed to get PR for comment on task {TaskId}: {Error}", taskId, prResult.Error);
            ThrowError(prResult.Error!);
            return;
        }

        var commitSha = prResult.Value!.HeadSha;

        var commentResult = await pullRequests.CreateCommentAsync(
            context.RepoId, context.PrNumber,
            commitSha, req.Path, req.Line, req.Body, ct);

        if (commentResult.IsFailure)
        {
            logger.LogWarning("Failed to create line comment for task {TaskId}: {Error}", taskId, commentResult.Error);
            ThrowError(commentResult.Error!);
            return;
        }

        var comment = commentResult.Value!;
        var response = new PrReviewCommentResponse(
            comment.Id, comment.Body, comment.Path, comment.Line,
            comment.User.Login, comment.User.AvatarUrl, comment.CreatedAt);

        await Send.OkAsync(response, ct);
    }
}
