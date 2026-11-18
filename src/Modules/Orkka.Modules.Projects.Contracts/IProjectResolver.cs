namespace Orkka.Modules.Projects.Contracts;

public interface IProjectResolver
{
    /// <summary>Resolves a route value that is either a ULID or an org-scoped project slug.
    /// Returns null when a slug doesn't match any project in the caller's org. A syntactically
    /// valid ULID is passed through without an existence check — endpoints already do that.</summary>
    Task<Ulid?> ResolveAsync(string idOrSlug, CancellationToken ct = default);
}
