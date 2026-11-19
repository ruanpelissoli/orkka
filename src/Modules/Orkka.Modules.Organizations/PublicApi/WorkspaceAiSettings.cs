namespace Orkka.Modules.Organizations.PublicApi;

using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Organizations.Contracts;
using Orkka.Modules.Organizations.Infrastructure;

internal sealed class WorkspaceAiSettings(OrganizationsDbContext db) : IWorkspaceAiSettings
{
    public async Task<string?> GetAiProviderAsync(Ulid orgId, CancellationToken ct = default)
        => await db.Organizations.IgnoreQueryFilters().AsNoTracking()
            .Where(o => o.Id == orgId)
            .Select(o => o.AiProvider)
            .FirstOrDefaultAsync(ct);

    public async Task<bool> SetAiProviderAsync(Ulid orgId, string? key, CancellationToken ct = default)
    {
        var org = await db.Organizations.IgnoreQueryFilters()
            .FirstOrDefaultAsync(o => o.Id == orgId, ct);
        if (org is null)
            return false;
        if (string.Equals(org.AiProvider, key, StringComparison.Ordinal))
            return true;
        org.AiProvider = key;
        await db.SaveChangesAsync(ct);
        return true;
    }
}
