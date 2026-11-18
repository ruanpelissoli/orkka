namespace Orkka.Modules.Agents.Features.AgentChat;

using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Orkka.Modules.Agents.Domain;
using Orkka.Modules.Agents.Infrastructure;
using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Ai;
using Orkka.SharedKernel.Security;
using Orkka.SharedKernel.Storage;

public sealed class UploadChatAttachmentRequest
{
    public IFormFile File { get; set; } = null!;
}

public sealed record ChatAttachmentResponse(
    Ulid Id, string FileName, string MimeType, long SizeBytes, DateTimeOffset CreatedAt);

/// <summary>
/// Uploads a context file for agent chat. The returned id goes into a visible
/// "[attachment:&lt;id&gt; "name"]" marker in the message; SendMessageEndpoint resolves the
/// marker at send time — text files are inlined into the agent's context, images become
/// Gemini reference inputs for the Designer.
/// </summary>
public sealed class UploadChatAttachmentEndpoint(
    AgentsDbContext db,
    ICurrentUser currentUser,
    IProjectResolver projectResolver,
    IBlobStorageService blobStorage)
    : Endpoint<UploadChatAttachmentRequest, ChatAttachmentResponse>
{
    public const string BlobContainerName = "chat-attachments";

    private const long MaxFileSizeBytes = 10 * 1024 * 1024;

    // Context files only: things an agent can read as text, plus images the Designer can
    // use as generation references. Executables/archives have no chat-context value and
    // widen the attack surface for nothing.
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".gif", ".svg",
        ".md", ".txt", ".csv", ".json", ".yaml", ".yml", ".xml", ".html", ".css",
        ".ts", ".tsx", ".js", ".jsx", ".cs", ".py", ".go", ".java", ".rb", ".rs", ".sql", ".sh", ".ps1",
    };

    public override void Configure()
    {
        Post("/api/projects/{projectId}/chat-attachments");
        AllowFileUploads();
    }

    public override async Task HandleAsync(UploadChatAttachmentRequest req, CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.AgentsChat))
            return;

        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var file = req.File;
        if (file is null || file.Length == 0)
        {
            ThrowError("No file was uploaded.", 400);
            return;
        }
        if (file.Length > MaxFileSizeBytes)
        {
            ThrowError($"The file exceeds the {MaxFileSizeBytes / (1024 * 1024)} MB attachment limit.", 400);
            return;
        }

        var extension = Path.GetExtension(file.FileName ?? "");
        if (!AllowedExtensions.Contains(extension))
        {
            ThrowError($"File type \"{extension}\" is not supported as chat context.", 400);
            return;
        }

        // The original name is display-only; the blob path uses the id so a crafted file
        // name can never influence storage layout.
        var attachmentId = Ulid.NewUlid();
        var blobName = $"{currentUser.OrgId}/{projectId}/{attachmentId}{extension.ToLowerInvariant()}";

        await blobStorage.UploadAsync(
            BlobContainerName, blobName, file.OpenReadStream(),
            string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType, ct);

        var attachment = new ChatAttachment
        {
            Id = attachmentId,
            OrgId = currentUser.OrgId,
            ProjectId = projectId,
            FileName = Path.GetFileName(file.FileName ?? $"attachment{extension}"),
            MimeType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
            SizeBytes = file.Length,
            BlobContainer = BlobContainerName,
            BlobName = blobName,
            UploadedByUserId = currentUser.UserId,
        };
        db.ChatAttachments.Add(attachment);

        await db.SaveChangesAsync(ct);

        await Send.OkAsync(new ChatAttachmentResponse(
            attachment.Id, attachment.FileName, attachment.MimeType,
            attachment.SizeBytes, attachment.CreatedAt), ct);
    }
}
