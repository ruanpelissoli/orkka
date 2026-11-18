namespace Orkka.Modules.Tasks.Features.TaskAttachments;

using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.SharedKernel.Security;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.Modules.Agents.Contracts;
using Orkka.SharedKernel.Documents;
using Orkka.SharedKernel.Storage;
using Orkka.SharedKernel.Utilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using Orkka.Modules.Tasks.Features.TaskAttachments;
using FastEndpoints;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

public sealed class UploadUrlValidator : Validator<UploadUrlRequest>
{
    public UploadUrlValidator()
    {
        RuleFor(x => x.FileName).NotEmpty().MaximumLength(500);
        RuleFor(x => x.ContentType).NotEmpty().MaximumLength(100);
    }
}

public sealed class GetUploadUrlEndpoint(
    TasksDbContext db,
    ICurrentUser currentUser,
    IBlobStorageService blobStorage)
    : Endpoint<UploadUrlRequest, UploadUrlResponse>
{
    public override void Configure()
    {
        Post("/api/tasks/{taskId:ulid}/attachments/upload-url");
    }

    public override async Task HandleAsync(UploadUrlRequest req, CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.TasksEdit))
            return;

        var taskId = Ulid.Parse(Route<string>("taskId"));

        var taskExists = await db.Tasks.AsNoTracking().AnyAsync(t => t.Id == taskId && t.OrgId == currentUser.OrgId, ct);
        if (!taskExists)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var blobName = $"{currentUser.OrgId}/{taskId}/{Ulid.NewUlid()}-{req.FileName}";
        var uploadUrl = await blobStorage.GenerateUploadUrlAsync(
            "task-attachments", blobName, req.ContentType, TimeSpan.FromMinutes(15));

        await Send.OkAsync(new UploadUrlResponse(uploadUrl, blobName), ct);
    }
}

public sealed class ConfirmAttachmentValidator : Validator<ConfirmAttachmentRequest>
{
    public ConfirmAttachmentValidator()
    {
        RuleFor(x => x.BlobName).NotEmpty();
        RuleFor(x => x.FileName).NotEmpty().MaximumLength(500);
        RuleFor(x => x.ContentType).NotEmpty().MaximumLength(100);
        RuleFor(x => x.FileSize).GreaterThan(0);
    }
}

public sealed class ConfirmAttachmentEndpoint(
    TasksDbContext db,
    ICurrentUser currentUser,
    IBlobStorageService blobStorage)
    : Endpoint<ConfirmAttachmentRequest, TaskAttachmentResponse>
{
    public override void Configure()
    {
        Post("/api/tasks/{taskId:ulid}/attachments");
    }

    public override async Task HandleAsync(ConfirmAttachmentRequest req, CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.TasksEdit))
            return;

        var taskId = Ulid.Parse(Route<string>("taskId"));

        var taskExists = await db.Tasks.AsNoTracking().AnyAsync(t => t.Id == taskId && t.OrgId == currentUser.OrgId, ct);
        if (!taskExists)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var fileUrl = blobStorage.GetBlobUrl("task-attachments", req.BlobName);

        var attachment = new TaskAttachment
        {
            OrgId = currentUser.OrgId,
            TaskId = taskId,
            FileUrl = fileUrl,
            FileType = req.ContentType,
            FileName = req.FileName,
            FileSize = req.FileSize
        };

        db.TaskAttachments.Add(attachment);
        await db.SaveChangesAsync(ct);

        await Send.CreatedAtAsync<ListAttachmentsEndpoint>(
            new { taskId },
            attachment.ToResponse(),
            cancellation: ct);
    }
}

public sealed class ListAttachmentsEndpoint(TasksDbContext db, ICurrentUser currentUser)
    : EndpointWithoutRequest<List<TaskAttachmentResponse>>
{
    public override void Configure()
    {
        Get("/api/tasks/{taskId:ulid}/attachments");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var taskId = Ulid.Parse(Route<string>("taskId"));

        var taskExists = await db.Tasks.AsNoTracking().AnyAsync(t => t.Id == taskId && t.OrgId == currentUser.OrgId, ct);
        if (!taskExists)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var attachments = await db.TaskAttachments
            .AsNoTracking()
            .Where(a => a.OrgId == currentUser.OrgId && a.TaskId == taskId)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(ct);

        await Send.OkAsync(attachments.Select(a => a.ToResponse()).ToList(), ct);
    }
}

public sealed class DeleteAttachmentEndpoint(
    TasksDbContext db,
    ICurrentUser currentUser,
    IBlobStorageService blobStorage)
    : EndpointWithoutRequest
{
    public override void Configure()
    {
        Delete("/api/attachments/{id:ulid}");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.TasksEdit))
            return;

        var id = Ulid.Parse(Route<string>("id"));

        var attachment = await db.TaskAttachments.FirstOrDefaultAsync(a => a.Id == id && a.OrgId == currentUser.OrgId, ct);
        if (attachment is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (AttachmentBlobName.FromUrl(attachment.FileUrl) is { } blobName)
        {
            await blobStorage.DeleteBlobAsync("task-attachments", blobName);

            // A PDF's extracted-text sibling is created lazily at prompt-build time and has no
            // row of its own — it would outlive the attachment if not deleted here.
            if (PdfTextSidecarService.IsPdf(attachment.FileName, attachment.FileType))
                await blobStorage.DeleteBlobAsync(
                    "task-attachments", blobName + PdfTextSidecarService.SidecarSuffix);
        }

        db.TaskAttachments.Remove(attachment);
        await db.SaveChangesAsync(ct);

        await Send.NoContentAsync(ct);
    }
}
