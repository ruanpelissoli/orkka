namespace Orkka.Modules.Tasks.Features.TaskPlans;

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

using Orkka.Modules.Tasks.Features.TaskPlans;
using FastEndpoints;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

public sealed class ReviewPlanValidator : Validator<ReviewPlanRequest>
{
    public ReviewPlanValidator()
    {
        RuleFor(x => x.Decision)
            .NotEmpty()
            .Must(d => d.Equals("approve", StringComparison.OrdinalIgnoreCase)
                     || d.Equals("reject", StringComparison.OrdinalIgnoreCase)
                     || d.Equals("approved", StringComparison.OrdinalIgnoreCase)
                     || d.Equals("rejected", StringComparison.OrdinalIgnoreCase))
            .WithMessage("Decision must be 'approve', 'reject', 'approved', or 'rejected'.");

        RuleFor(x => x.Feedback)
            .MaximumLength(5000).WithMessage("Feedback must be 5,000 characters or fewer.");
    }
}

public sealed class ReviewPlanEndpoint(
    TasksDbContext db,
    ICurrentUser currentUser,
    ITasksApi tasksApi)
    : Endpoint<ReviewPlanRequest, TaskPlanResponse>
{
    public override void Configure()
    {
        Post("/api/tasks/{taskId:ulid}/plans/{planId:ulid}/review");
    }

    public override async Task HandleAsync(ReviewPlanRequest req, CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.PlansReview))
            return;

        var taskId = Ulid.Parse(Route<string>("taskId"));
        var planId = Ulid.Parse(Route<string>("planId"));
        var approve = req.Decision.StartsWith("approve", StringComparison.OrdinalIgnoreCase);

        var outcome = await tasksApi.ReviewPlanAsync(currentUser.OrgId, taskId, planId, approve, req.Feedback, ct);
        if (!outcome.Ok)
        {
            await this.SendBoardFailureAsync(outcome, ct);
            return;
        }

        if (!approve)
        {
            await Send.NoContentAsync(ct);
            return;
        }

        var plan = await db.TaskPlans.AsNoTracking()
            .FirstAsync(p => p.Id == planId && p.OrgId == currentUser.OrgId, ct);
        await Send.OkAsync(plan.ToResponse(), ct);
    }
}
