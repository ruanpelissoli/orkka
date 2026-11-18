namespace Orkka.Modules.Projects.Infrastructure.Services;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.SourceControl.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using Orkka.SharedKernel.Security;
using Orkka.Modules.Projects.Infrastructure;
using Orkka.SharedKernel.Utilities;
using Microsoft.EntityFrameworkCore;

public sealed class ProjectIdentifierService(ProjectsDbContext db) : IProjectIdentifierService
{
    private const int MaxKeyLength = 5;

    public async Task<(string Slug, string Key)> GenerateAsync(
        string name,
        string? requestedSlug,
        string? requestedKey,
        Ulid orgId,
        CancellationToken ct = default)
    {
        var baseSlug = string.IsNullOrWhiteSpace(requestedSlug)
            ? SlugGenerator.GenerateProjectSlug(name)
            : requestedSlug.Trim();
        var baseKey = string.IsNullOrWhiteSpace(requestedKey)
            ? SlugGenerator.GenerateProjectKey(name)
            : requestedKey.Trim().ToUpperInvariant();

        var existing = await db.Projects
            .Where(p => p.OrgId == orgId)
            .Select(p => new { p.Slug, p.Key })
            .ToListAsync(ct);

        var slugs = existing.Select(x => x.Slug).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var keys = existing.Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var slug = baseSlug;
        for (var i = 2; slugs.Contains(slug); i++)
            slug = $"{baseSlug}-{i}";

        var key = baseKey;
        for (var i = 2; keys.Contains(key); i++)
        {
            var suffix = i.ToString();
            key = baseKey[..Math.Min(baseKey.Length, MaxKeyLength - suffix.Length)] + suffix;
        }

        return (slug, key);
    }

    public async Task<bool> IsSlugAvailableAsync(string slug, Ulid orgId, Ulid? excludeProjectId = null, CancellationToken ct = default)
    {
        if (SlugGenerator.ReservedProjectSlugs.Contains(slug))
            return false;

        return !await db.Projects.AnyAsync(
            p => p.OrgId == orgId && p.Slug == slug && (excludeProjectId == null || p.Id != excludeProjectId),
            ct);
    }

    public async Task<bool> IsKeyAvailableAsync(string key, Ulid orgId, CancellationToken ct = default)
        => !await db.Projects.AnyAsync(p => p.OrgId == orgId && p.Key == key, ct);
}
