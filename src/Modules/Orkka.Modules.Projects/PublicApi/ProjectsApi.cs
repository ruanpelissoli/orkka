namespace Orkka.Modules.Projects.PublicApi;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.SharedKernel.Security;
using Orkka.SharedKernel.Utilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Projects.Infrastructure;
using Orkka.Modules.Projects.Domain;

/// <summary>Module implementation of <see cref="IProjectsApi"/>. Tenant filtering follows the
/// ambient <c>ICurrentUser</c> of the scope: org-scoped in the API host, unfiltered in the Worker.</summary>
internal sealed class ProjectsApi(
    ProjectsDbContext db,
    Features.ProjectRepos.RepoCloneSynchronizer repoClones,
    ILogger<ProjectsApi> logger,
    // Resolved only when a project is created: the creator needs the event bus, which a host
    // that merely reads projects does not have to register.
    IServiceProvider? services = null) : IProjectsApi
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public async Task<IReadOnlyDictionary<Ulid, string>> GetProjectNamesAsync(
        IReadOnlyCollection<Ulid> projectIds, CancellationToken ct = default)
        => await db.Projects.AsNoTracking()
            .Where(p => projectIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Name, ct);

    // Explicit OrgId predicate + IgnoreQueryFilters: the caller is the Organizations module
    // acting on an org it already resolved, and this must not depend on tenant context.
    public async Task<IReadOnlyList<Ulid>> ListProjectIdsAsync(
        Ulid orgId, CancellationToken ct = default)
        => await db.Projects.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.OrgId == orgId)
            .Select(p => p.Id)
            .ToListAsync(ct);

    public async Task<ProjectSummary?> GetProjectSummaryAsync(Ulid projectId, CancellationToken ct = default)
    {
        var project = await db.Projects.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == projectId, ct);

        if (project is null)
            return null;

        return ToSummary(project);
    }

    public async Task<IReadOnlyDictionary<Ulid, ProjectSummary>> GetProjectSummariesAsync(
        IReadOnlyCollection<Ulid> projectIds, CancellationToken ct = default)
    {
        if (projectIds.Count == 0)
            return new Dictionary<Ulid, ProjectSummary>();

        var projects = await db.Projects.AsNoTracking()
            .Where(p => projectIds.Contains(p.Id))
            .ToListAsync(ct);

        return projects.ToDictionary(p => p.Id, ToSummary);
    }

    public async Task<RepoSummary?> GetRepoAsync(Ulid repoId, CancellationToken ct = default)
    {
        var repo = await db.ProjectRepos.AsNoTracking()
            .Include(r => r.Project)
            .FirstOrDefaultAsync(r => r.Id == repoId, ct);

        return repo is null ? null : ToSummary(repo);
    }

    public async Task<RepoSummary?> AddLocalRepoAsync(
        Ulid orgId, Ulid projectId, string path, string name, string defaultBranch,
        CancellationToken ct = default)
    {
        var projectExists = await db.Projects
            .AnyAsync(p => p.Id == projectId && p.OrgId == orgId, ct);
        if (!projectExists)
            return null;

        // Idempotent: the same folder linked twice is a retry after a failed clone, and a
        // duplicate row would give the project two mirrors of one working copy.
        var existing = await db.ProjectRepos
            .FirstOrDefaultAsync(
                r => r.ProjectId == projectId && r.OrgId == orgId && r.GitUrl == path, ct);
        if (existing is not null)
            return ToSummary(existing);

        var repo = new Domain.ProjectRepo
        {
            OrgId = orgId,
            ProjectId = projectId,
            Name = name,
            GitUrl = path,
            DefaultBranch = defaultBranch,
            WorkspaceStatus = WorkspaceStatus.Cloning,
            CardColor = Features.ProjectRepos.Mappers.RandomCardColor(),
        };

        db.ProjectRepos.Add(repo);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Linked local folder as repo {RepoId} for project {ProjectId}", repo.Id, projectId);

        return ToSummary(repo);
    }

    public async Task SetRepoWorkspaceStatusAsync(
        Ulid repoId, WorkspaceStatus status, CancellationToken ct = default)
    {
        var repo = await db.ProjectRepos.FirstOrDefaultAsync(r => r.Id == repoId, ct);
        if (repo is null)
            return;

        repo.WorkspaceStatus = status;
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyDictionary<Ulid, string?>> GetRepoStacksAsync(
        IReadOnlyCollection<Ulid> repoIds, CancellationToken ct = default)
    {
        if (repoIds.Count == 0)
            return new Dictionary<Ulid, string?>();

        var ids = repoIds.Distinct().ToList();
        return await db.ProjectRepos.AsNoTracking()
            .Where(r => ids.Contains(r.Id))
            .Select(r => new { r.Id, r.Stack })
            .ToDictionaryAsync(r => r.Id, r => r.Stack, ct);
    }

    public async Task SetRepoStackAsync(Ulid repoId, string stack, CancellationToken ct = default)
    {
        var normalized = RepoStacks.Normalize(stack);
        await db.ProjectRepos
            .Where(r => r.Id == repoId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Stack, normalized), ct);
    }

    public async Task<IReadOnlyList<RepoSummary>> ListReposAsync(Ulid projectId, CancellationToken ct = default)
        => (await db.ProjectRepos.AsNoTracking()
            .Where(r => r.ProjectId == projectId)
            .OrderBy(r => r.Name)
            .Select(r => new RepoSummary(
                r.Id, r.ProjectId, r.OrgId, r.Name, r.GitUrl, r.DefaultBranch,
                r.CardColor, r.WorkspaceStatus, r.SetupCommand, r.QaRunCommand, r.BuildCommand, r.CheckCommand,
                r.ForgeKind, r.CliAccount,
                r.FolderPath, r.Description, r.MissingSince,
                r.FolderPath == null ? null : r.Project.BaseFolder, r.Stack))
            .ToListAsync(ct))
            .Select(WithCheckoutPath)
            .ToList();

    public async Task<string?> EnsureRepoWorkspaceAsync(
        Ulid repoId, string purpose, CancellationToken ct = default)
    {
        // Tracked: the synchronizer writes WorkspaceStatus.
        var repo = await db.ProjectRepos
            .FirstOrDefaultAsync(r => r.Id == repoId, ct);

        return repo is null
            ? "Repository not found."
            : await repoClones.EnsureAsync(repo, purpose, ct);
    }

    public async Task<IReadOnlyDictionary<Ulid, RepoSummary>> ListReposForProjectsAsync(
        IReadOnlyCollection<Ulid> projectIds, CancellationToken ct = default)
    {
        if (projectIds.Count == 0)
            return new Dictionary<Ulid, RepoSummary>();

        return (await db.ProjectRepos.AsNoTracking()
            .Where(r => projectIds.Contains(r.ProjectId))
            .Select(r => new RepoSummary(
                r.Id, r.ProjectId, r.OrgId, r.Name, r.GitUrl, r.DefaultBranch,
                r.CardColor, r.WorkspaceStatus, r.SetupCommand, r.QaRunCommand, r.BuildCommand, r.CheckCommand,
                r.ForgeKind, r.CliAccount,
                r.FolderPath, r.Description, r.MissingSince,
                r.FolderPath == null ? null : r.Project.BaseFolder, r.Stack))
            .ToListAsync(ct))
            .Select(WithCheckoutPath)
            .ToDictionary(r => r.Id);
    }

    public async Task<IReadOnlyList<Ulid>> ListLiveRepoIdsAsync(CancellationToken ct = default)
        => await db.ProjectRepos.AsNoTracking()
            .Where(r => db.Projects.Any(p => p.Id == r.ProjectId))
            .Select(r => r.Id)
            .ToListAsync(ct);

    public async Task<int> ReserveTaskNumbersAsync(Ulid projectId, int count, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        // The id as SQLite stores it (EF writes a Guid as upper-case TEXT); a raw Guid
        // parameter would bind as a BLOB and match nothing.
        var projectKey = projectId.ToGuid().ToString().ToUpperInvariant();

        // One statement, so one write lock: concurrent reservations serialize on SQLite's
        // single writer. Never call this inside another context's open write transaction —
        // that writer holds the lock this UPDATE waits for. Enumerate without composing LINQ
        // operators: composition would wrap the UPDATE in a subquery, which SQLite refuses.
        var results = await db.Database
            .SqlQuery<int>($"""
                UPDATE "projects"
                SET "LastTaskNumber" = "LastTaskNumber" + {count}
                WHERE "Id" = {projectKey}
                RETURNING "LastTaskNumber" AS "Value"
                """)
            .ToListAsync(ct);

        return results.Single();
    }

    private static RepoSummary ToSummary(Domain.ProjectRepo r) => new(
        r.Id, r.ProjectId, r.OrgId, r.Name, r.GitUrl, r.DefaultBranch,
        r.CardColor, r.WorkspaceStatus, r.SetupCommand, r.QaRunCommand, r.BuildCommand, r.CheckCommand,
        r.ForgeKind, r.CliAccount, r.FolderPath, r.Description, r.MissingSince,
        BaseFolders.CheckoutPath(r.Project?.BaseFolder, r.FolderPath), r.Stack);

    /// <summary>The projections read the project's base folder into LocalCheckoutPath (EF
    /// cannot call the joiner); this turns it into the checkout path proper.</summary>
    private static RepoSummary WithCheckoutPath(RepoSummary r) =>
        r with { LocalCheckoutPath = BaseFolders.CheckoutPath(r.LocalCheckoutPath, r.FolderPath) };

    private static ProjectSummary ToSummary(Domain.Project p) => new(
        p.Id, p.OrgId, p.Name, p.Slug, p.Key, ParseSettings(p.Settings),
        p.BaseFolder, p.BaseFolderShape);

    public async Task<ProjectSummary> CreateProjectAsync(NewProject project, CancellationToken ct = default)
    {
        var creator = services?.GetService<Features.Projects.ProjectCreator>()
            ?? throw new InvalidOperationException("Project creation is not available in this host.");

        return ToSummary(await creator.CreateAsync(project, description: null, slug: null, key: null, ct));
    }

    public async Task<RepoSummary?> AddDiscoveredRepoAsync(
        Ulid orgId, Ulid projectId, DiscoveredRepoRegistration repo, CancellationToken ct = default)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId && p.OrgId == orgId, ct);
        if (project is null)
            return null;

        var folderPath = BaseFolders.NormalizeFolderPath(repo.FolderPath)
            ?? throw new ArgumentException($"Unsafe folder path: {repo.FolderPath}", nameof(repo));

        var existing = await db.ProjectRepos
            .FirstOrDefaultAsync(r => r.ProjectId == projectId && r.OrgId == orgId && r.FolderPath == folderPath, ct);
        if (existing is not null)
        {
            existing.Project = project;
            return ToSummary(existing);
        }

        var isRemote = RepoUrl.Classify(repo.GitUrl) == RepoLocation.Remote;
        var description = string.IsNullOrWhiteSpace(repo.Description) ? null : repo.Description.Trim();
        if (description is { Length: > 300 })
            description = description[..300];

        var entity = new Domain.ProjectRepo
        {
            OrgId = orgId,
            ProjectId = projectId,
            Name = repo.Name,
            GitUrl = repo.GitUrl,
            DefaultBranch = string.IsNullOrWhiteSpace(repo.DefaultBranch) ? "main" : repo.DefaultBranch,
            // Exactly what the two manual paths write: a remote is detected from its host and
            // starts Pending; a folder starts Cloning while the runner primes its mirror.
            ForgeKind = isRemote ? RepoUrl.DetectForge(repo.GitUrl) : ForgeKind.Other,
            WorkspaceStatus = isRemote ? WorkspaceStatus.Pending : WorkspaceStatus.Cloning,
            CardColor = Features.ProjectRepos.Mappers.RandomCardColor(),
            FolderPath = folderPath,
            Description = description,
            Project = project,
        };

        db.ProjectRepos.Add(entity);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Registered {Kind} repo {RepoId} ({FolderPath}) from the base folder of project {ProjectId}",
            isRemote ? "remote" : "local-folder", entity.Id, folderPath, projectId);

        return ToSummary(entity);
    }

    public async Task SetRepoMissingAsync(Ulid repoId, DateTimeOffset? missingSince, CancellationToken ct = default)
        => await db.ProjectRepos
            .Where(r => r.Id == repoId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.MissingSince, missingSince), ct);

    public async Task SetRepoGitUrlAsync(Ulid repoId, string gitUrl, CancellationToken ct = default)
    {
        var repo = await db.ProjectRepos.FirstOrDefaultAsync(r => r.Id == repoId, ct);
        if (repo is null)
            return;
        repo.GitUrl = gitUrl;
        repo.ForgeKind = RepoUrl.Classify(gitUrl) == RepoLocation.Remote ? RepoUrl.DetectForge(gitUrl) : ForgeKind.Other;
        await db.SaveChangesAsync(ct);
    }

    internal static ProjectSettingsDto? ParseSettings(string? settingsJson)
    {
        if (settingsJson is null)
            return null;

        try
        {
            return JsonSerializer.Deserialize<ProjectSettingsDto>(settingsJson, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
