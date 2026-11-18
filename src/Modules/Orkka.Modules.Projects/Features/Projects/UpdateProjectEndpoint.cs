namespace Orkka.Modules.Projects.Features.Projects;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.SharedKernel.Utilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using Orkka.SharedKernel.Security;
using Orkka.Modules.Projects.Infrastructure;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Orkka.Modules.Agents.Contracts;

public sealed class UpdateProjectEndpoint(
    ProjectsDbContext db,
    ICurrentUser currentUser,
    IProjectIdentifierService projectIdentifiers,
    IAgentAuditLog audit)
    : Endpoint<UpdateProjectRequest, ProjectResponse>
{
    public override void Configure()
    {
        Patch("/api/projects/{id:ulid}");
    }

    public override async Task HandleAsync(UpdateProjectRequest req, CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.ProjectsManage))
            return;

        var id = Ulid.Parse(Route<string>("id"));

        var entity = await db.Projects.FirstOrDefaultAsync(p => p.Id == id && p.OrgId == currentUser.OrgId, ct);

        if (entity is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (req.Name is not null)
            entity.Name = req.Name;

        if (req.Description is not null)
            entity.Description = req.Description;

        // Slug changes only on explicit request — a rename never touches it, so URLs stay stable
        if (req.Slug is not null && req.Slug != entity.Slug)
        {
            if (!await projectIdentifiers.IsSlugAvailableAsync(req.Slug, currentUser.OrgId, excludeProjectId: entity.Id, ct: ct))
                ThrowError(r => r.Slug!, "This slug is already in use.");

            entity.Slug = req.Slug;
        }

        Dictionary<string, object?>? settingsDiff = null;
        if (req.Settings is not null)
        {
            // Stored normalized ("pt-BR" → "pt") so every prompt builder can trust the value;
            // anything outside the supported set is a client bug, not a preference to keep.
            var language = AgentLanguage.Normalize(req.Settings.Language);
            if (req.Settings.Language is not null && language is null)
                ThrowError(r => r.Settings!.Language!, "Unsupported language.");

            var before = entity.Settings;
            entity.Settings = Mappers.SerializeSettings(req.Settings with { Language = language });
            settingsDiff = DiffSettings(before, entity.Settings);
        }

        await db.SaveChangesAsync(ct);

        // Settings drive automation, budgets and agent behaviour — worth a trail entry with
        // exactly which keys moved (values included; there are no secrets in this bag).
        if (settingsDiff is { Count: > 0 })
            await audit.RecordAsync(AgentAuditRecord.ByUser(
                currentUser.OrgId, currentUser.UserId, AgentAuditEventType.ProjectSettingsChanged,
                $"Project \"{entity.Name}\" settings changed by {currentUser.UserId}: {string.Join(", ", settingsDiff.Keys)}",
                projectId: entity.Id,
                metadata: new { changed = settingsDiff }), ct);

        await Send.OkAsync(entity.ToResponse(), ct);
    }

    /// <summary>Top-level keys whose JSON value changed → { key: { from, to } }.</summary>
    private static Dictionary<string, object?> DiffSettings(string? beforeJson, string afterJson)
    {
        var diff = new Dictionary<string, object?>();
        using var before = JsonDocument.Parse(string.IsNullOrWhiteSpace(beforeJson) ? "{}" : beforeJson);
        using var after = JsonDocument.Parse(afterJson);
        var beforeProps = before.RootElement.ValueKind == JsonValueKind.Object
            ? before.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetRawText())
            : [];
        foreach (var prop in after.RootElement.EnumerateObject())
        {
            beforeProps.TryGetValue(prop.Name, out var previous);
            var current = prop.Value.GetRawText();
            if (previous != current)
                diff[prop.Name] = new
                {
                    from = previous is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(previous),
                    to = prop.Value.Clone(),
                };
        }
        return diff;
    }
}
