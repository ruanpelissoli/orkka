namespace Orkka.Modules.Tasks.Infrastructure.Services;

using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.SharedKernel.Ai;
using Orkka.SharedKernel.Utilities;
using Microsoft.Extensions.Logging;

using Orkka.Modules.Projects.Contracts;

// The task-number counter lives on the project row, which the Projects module owns since P7 —
// reservations go through the module contract. The interface stays here for legacy callers
// until the Tasks module is extracted (P9).
public sealed class TaskNumberService(IProjectsApi projectsApi) : ITaskNumberService
{
    public Task<int> ReserveAsync(Ulid projectId, CancellationToken ct = default)
        => projectsApi.ReserveTaskNumbersAsync(projectId, 1, ct);

    public Task<int> ReserveRangeAsync(Ulid projectId, int count, CancellationToken ct = default)
        => projectsApi.ReserveTaskNumbersAsync(projectId, count, ct);
}
