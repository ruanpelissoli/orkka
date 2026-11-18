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

internal static class Mappers
{
    public static TaskPlanResponse ToResponse(this TaskPlan entity) =>
        new(
            entity.Id,
            entity.TaskId,
            entity.Version,
            entity.Content,
            entity.Status.ToString(),
            entity.Feedback,
            entity.ApprovedAt,
            entity.CreatedAt);
}
