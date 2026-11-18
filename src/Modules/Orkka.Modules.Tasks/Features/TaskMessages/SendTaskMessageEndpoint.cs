namespace Orkka.Modules.Tasks.Features.TaskMessages;

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

using Orkka.Modules.Tasks.Features.TaskMessages;
using FastEndpoints;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

public sealed class SendTaskMessageValidator : Validator<SendMessageRequest>
{
    public SendTaskMessageValidator()
    {
        RuleFor(x => x.Content)
            .NotEmpty().WithMessage("Content is required.");

        RuleFor(x => x.MessageType)
            .Must(mt => Enum.TryParse<MessageType>(mt, true, out _))
            .WithMessage("Invalid message type.");
    }
}

public sealed class SendTaskMessageEndpoint(
    TasksDbContext db,
    ICurrentUser currentUser,
    ITasksApi tasksApi)
    : Endpoint<SendMessageRequest, TaskMessageResponse>
{
    public override void Configure()
    {
        Post("/api/tasks/{taskId:ulid}/messages");
    }

    public override async Task HandleAsync(SendMessageRequest req, CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.TasksExecute))
            return;

        var taskId = Ulid.Parse(Route<string>("taskId"));

        var outcome = await tasksApi.AnswerTaskAsync(
            currentUser.OrgId, taskId, req.Content, req.MessageType, currentUser.UserId, currentUser.Locale, ct);
        if (!outcome.Ok)
        {
            await this.SendBoardFailureAsync(outcome, ct);
            return;
        }

        var message = await db.TaskMessages.AsNoTracking()
            .FirstAsync(m => m.Id == outcome.EntityId!.Value && m.OrgId == currentUser.OrgId, ct);

        await Send.CreatedAtAsync<ListTaskMessagesEndpoint>(
            new { taskId },
            message.ToResponse(),
            cancellation: ct);
    }
}
