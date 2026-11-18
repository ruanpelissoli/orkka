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

using FastEndpoints;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>200 Merged:true = the PR landed now; 202 ResolutionStarted:true = conflicts
/// handed to the automatic resolver, merge follows on a worker cycle.</summary>
public sealed record MergePullRequestResponse(bool Merged, bool ResolutionStarted, string? Message);

public sealed class MergePullRequestEndpoint(
    ICurrentUser currentUser,
    ITasksApi tasksApi)
    : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("/api/tasks/{taskId:ulid}/pull-request/merge");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.CodeReviewMerge))
            return;

        var taskId = Ulid.Parse(Route<string>("taskId"));
        var outcome = await tasksApi.MergePullRequestAsync(currentUser.OrgId, taskId, ct);
        if (!outcome.Ok)
        {
            if (outcome.Failure == BoardOpFailure.NotFound)
            {
                await Send.NotFoundAsync(ct);
                return;
            }
            ThrowError(outcome.Message ?? "Merge failed.");
            return;
        }

        if (outcome.ResolutionStarted)
        {
            // The click worked — conflicts handed off to the automatic resolver, and the
            // merge lands on a later worker cycle. 202 + body, never a 400: reporting this
            // as an error is what made the auto-merge tool read as broken.
            await Send.ResponseAsync(new MergePullRequestResponse(
                Merged: false, ResolutionStarted: true, outcome.Message), 202, ct);
            return;
        }

        await Send.NoContentAsync(ct); // unchanged contract: 204 = merged
    }
}
