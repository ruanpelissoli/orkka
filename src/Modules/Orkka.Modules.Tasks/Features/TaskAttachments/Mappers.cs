namespace Orkka.Modules.Tasks.Features.TaskAttachments;

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

using Orkka.Modules.Tasks.Features.TaskAttachments;

internal static class Mappers
{
    public static TaskAttachmentResponse ToResponse(this TaskAttachment entity) =>
        new(
            entity.Id,
            entity.TaskId,
            entity.FileUrl,
            entity.FileType,
            entity.FileName,
            entity.FileSize,
            entity.CreatedAt);
}
