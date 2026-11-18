namespace Orkka.Modules.Documents.Features.Documents;

using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Documents.Contracts;
using Orkka.Modules.Documents.Infrastructure;
using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Security;

public sealed record RenameDocumentFolderRequest(string? FromPath, string? ToPath);

/// <summary>
/// Renames/moves a documents folder: every document at FromPath (or below) gets the path
/// prefix replaced with ToPath. Folders are virtual (derived from document paths).
/// Content is untouched, so no revisions are written — unlike a single-document move,
/// which versions the change. Rejected with 409 when a moved document would collide with
/// an existing (path, title) pair.
/// </summary>
public sealed class RenameDocumentFolderEndpoint(
    DocumentsDbContext db,
    IProjectsApi projectsApi,
    IProjectResolver projectResolver,
    ICurrentUser currentUser)
    : Endpoint<RenameDocumentFolderRequest>
{
    public override void Configure()
    {
        Post("/api/projects/{projectId}/documents/folders/rename");
    }

    public override async Task HandleAsync(RenameDocumentFolderRequest req, CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.DocumentsManage))
            return;

        var projectId = await ProjectAccess.ResolveOwnedProjectAsync(
            projectResolver, projectsApi, currentUser, Route<string>("projectId")!, ct);
        if (projectId is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var fromPath = DocumentRules.NormalizePath(req.FromPath);
        var toPath = DocumentRules.NormalizePath(req.ToPath);
        if (fromPath.Length == 0)
        {
            ThrowError("The folder to rename must not be the root.", 400);
            return;
        }
        if (toPath == fromPath)
        {
            await Send.NoContentAsync(ct);
            return;
        }
        if (toPath.StartsWith(fromPath + "/", StringComparison.Ordinal))
        {
            ThrowError("A folder cannot be moved into itself.", 400);
            return;
        }

        var prefix = fromPath + "/";
        var affected = await db.Documents
            .Where(d => d.OrgId == currentUser.OrgId && d.ProjectId == projectId
                && (d.Path == fromPath || d.Path.StartsWith(prefix)))
            .ToListAsync(ct);
        if (affected.Count == 0)
        {
            await Send.NoContentAsync(ct);
            return;
        }

        // Uniqueness is (path, title) — pre-check every target so the rename is
        // all-or-nothing instead of dying midway on the unique index.
        var targets = affected
            .Select(d => (Doc: d, NewPath: DocumentRules.NormalizePath(
                toPath + (d.Path.Length > fromPath.Length ? d.Path[fromPath.Length..] : ""))))
            .ToList();

        var affectedIds = affected.Select(d => d.Id).ToList();
        foreach (var (doc, newPath) in targets)
        {
            var collides = await db.Documents.AnyAsync(
                d => d.OrgId == currentUser.OrgId && d.ProjectId == projectId
                    && d.Path == newPath && d.Title == doc.Title && !affectedIds.Contains(d.Id), ct);
            if (collides)
            {
                ThrowError($"\"{newPath}/{doc.Title}\" already exists — rename or move it first.", 409);
                return;
            }
        }

        foreach (var (doc, newPath) in targets)
            doc.Path = newPath;

        await db.SaveChangesAsync(ct);
        await Send.NoContentAsync(ct);
    }
}
