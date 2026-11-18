namespace Orkka.Modules.Tasks.Features.TaskAttachments;

public sealed record UploadUrlRequest(
    string FileName,
    string ContentType);

public sealed record UploadUrlResponse(
    string UploadUrl,
    string BlobName);

public sealed record ConfirmAttachmentRequest(
    string BlobName,
    string FileName,
    string ContentType,
    long FileSize);

public sealed record TaskAttachmentResponse(
    Ulid Id,
    Ulid TaskId,
    string FileUrl,
    string FileType,
    string FileName,
    long FileSize,
    DateTimeOffset CreatedAt);
