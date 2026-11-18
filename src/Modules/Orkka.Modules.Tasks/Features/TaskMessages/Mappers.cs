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

internal static class Mappers
{
    public static TaskMessageResponse ToResponse(this TaskMessage entity) =>
        new(
            entity.Id,
            entity.TaskId,
            entity.Role,
            entity.Content,
            entity.MessageType.ToString(),
            entity.SenderId,
            entity.Metadata,
            entity.CreatedAt);
}
