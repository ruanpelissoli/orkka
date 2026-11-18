namespace Orkka.Modules.Tasks.Features.Tasks;

using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.SharedKernel.Security;
using Orkka.Modules.Agents.Contracts;
using Microsoft.Extensions.Logging;

using FastEndpoints;
using Microsoft.EntityFrameworkCore;

public sealed record ModelRecommendationRequest(
    // "apply" copies the suggestion into the task's per-task model override;
    // "dismiss" clears the suggestion. Neither touches the agent's configuration.
    string Action);

/// <summary>
/// Resolves the advisory cheaper-model suggestion the PM review left on a task. Apply sets
/// the EXISTING per-task model override (same field the create form offers) and clears the
/// suggestion; Dismiss just clears it. The platform never applies a recommendation itself.
/// </summary>
public sealed class ModelRecommendationEndpoint(
    TasksDbContext db,
    ICurrentUser currentUser,
    ITaskNotifier taskNotifier,
    Orkka.Modules.Projects.Contracts.IProjectsApi projectsApi,
    ILogger<ModelRecommendationEndpoint> logger)
    : Endpoint<ModelRecommendationRequest, TaskResponse>
{
    public override void Configure()
    {
        Post("/api/tasks/{id:ulid}/model-recommendation");
    }

    public override async Task HandleAsync(ModelRecommendationRequest req, CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.TasksEdit))
            return;

        var id = Ulid.Parse(Route<string>("id"));
        var entity = await db.Tasks
            .FirstOrDefaultAsync(t => t.Id == id && t.OrgId == currentUser.OrgId, ct);
        if (entity is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (string.IsNullOrEmpty(entity.RecommendedModel))
        {
            ThrowError("This task has no pending model recommendation.");
            return;
        }

        var apply = req.Action?.Trim().ToLowerInvariant() switch
        {
            "apply" => true,
            "dismiss" => false,
            _ => (bool?)null,
        };
        if (apply is null)
        {
            ThrowError("Action must be 'apply' or 'dismiss'.");
            return;
        }

        var recommended = entity.RecommendedModel;
        if (apply is true)
        {
            // The catalog validates like the create form does — a recommendation produced
            // before a model was retired must not pin a task to a dead id.
            if (!ModelCatalog.IsSelectable(recommended))
            {
                entity.RecommendedModel = null;
                await db.SaveChangesAsync(ct);
                ThrowError($"'{recommended}' is no longer available — the suggestion was cleared.");
                return;
            }

            entity.Model = recommended;
            db.TaskMessages.Add(new TaskMessage
            {
                Id = Ulid.NewUlid(),
                TaskId = entity.Id,
                OrgId = entity.OrgId,
                Role = "system",
                Content = $"Model suggestion applied: this task's runs use "
                    + $"**{ModelRecommendation.DisplayName(recommended)}**. The agent's own "
                    + "model configuration is unchanged.",
                MessageType = MessageType.SystemEvent,
            });
            logger.LogInformation(
                "Task {TaskId}: model recommendation {Model} applied as the per-task override",
                id, recommended);
        }
        else
        {
            logger.LogInformation(
                "Task {TaskId}: model recommendation {Model} dismissed", id, recommended);
        }

        entity.RecommendedModel = null;
        await db.SaveChangesAsync(ct);

        await taskNotifier.SendTaskUpdatedAsync(entity.ProjectId, new TaskUpdatedEvent(
            entity.Id, entity.Title, entity.Priority.ToString(), entity.Status.ToString(),
            entity.CurrentLane.ToString(), entity.IsArchived, DateTimeOffset.UtcNow));

        var project = await projectsApi.GetProjectSummaryAsync(entity.ProjectId, ct);
        var repo = entity.RepoId is { } repoId ? await projectsApi.GetRepoAsync(repoId, ct) : null;
        await Send.OkAsync(entity.ToResponse(project!.Key, repo), ct);
    }
}
