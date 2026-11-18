namespace Orkka.Modules.Runners.Features.LocalRepos;

using System.Collections.Concurrent;
using FastEndpoints;
using Microsoft.Extensions.Logging;
using Orkka.Git;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Runners.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.SharedKernel.Security;
using Orkka.SharedKernel.Utilities;

// ── Public request/response shapes ───────────────────────────────────────────

public sealed record DiscoverReposRequestDto(string? Path);

/// <param name="Location">"remote" (has an origin: branch push + forge PR) or "local" (no
/// origin: squash-merged into the folder).</param>
public sealed record DiscoveredRepoDto(
    string RelativePath, string Name, string? OriginUrl, string Location, string? Forge, string? Host,
    string? DefaultBranch, string? CurrentBranch, bool IsDirty, bool HasUnpushedCommits, bool HasCommits,
    string? Description, string? Stack);

/// <summary>Supported=false: no runner to ask (offline, or too old — <see cref="Error"/> says
/// which). The UI shows the reason instead of a preview.</summary>
public sealed record DiscoverReposResponseDto(
    bool Supported, string? Path, string? Shape, DiscoveredRepoDto[] Repos, string[] Areas, string? Error);

public sealed record CreateProjectFromFolderRequest(string? Path, string? Name);

public sealed record FolderProjectDto(
    Ulid Id, string Name, string Slug, string Key, string? BaseFolder, string? BaseFolderShape);

public sealed record FolderRepoDto(
    Ulid Id, string Name, string GitUrl, string DefaultBranch, string? FolderPath, string Location,
    string WorkspaceStatus, string? Description);

public sealed record CreateProjectFromFolderResponse(
    FolderProjectDto Project, FolderRepoDto[] Repos, string[] Warnings);

public sealed record RescanRepoStatusDto(
    Ulid RepoId, bool IsDirty, bool HasUnpushedCommits, string? CurrentBranch);

/// <param name="Skipped">An automatic rescan that did not run: one ran for this project in the
/// last ten minutes (its result is returned), or the runner is offline.</param>
public sealed record RescanProjectResponse(
    bool Skipped, bool Offline, string[] Added, string[] Missing, string[] Restored,
    string[] OriginUpdated, string[] Warnings, RescanRepoStatusDto[] Repos)
{
    public static readonly RescanProjectResponse OfflineSkip = new(true, true, [], [], [], [], [], []);
}

// Wire shapes of the loopback relay (mirrors Orkka.Runners.Server's LocalFolderRelayEndpoints).
internal sealed record BaseFolderRelayDiscoverRequest(string OrgId, string UserId, string Path);

internal sealed record BaseFolderRelayDiscoveredRepo(
    string RelativePath, string Name, string? OriginUrl, string? DefaultBranch, string? CurrentBranch,
    bool IsDirty, bool HasUnpushedCommits, bool HasCommits, string? Description, string? Stack);

internal sealed record BaseFolderRelayDiscoverResponse(
    bool Supported, string? Path, string? Shape, BaseFolderRelayDiscoveredRepo[] Repos, string[] Areas, string? Error);

internal sealed record BaseFolderRelayEnsureCloneRequest(
    string OrgId, string UserId, string RepoId, string CloneUrlOrPath, string? LocalCheckoutPath);

internal sealed record BaseFolderRelayOpResponse(bool Supported, bool Success, string? Value, string? Error);

/// <summary>
/// Base-folder projects: the developer points Orkka at ONE folder on their machine — a
/// monorepo, or a folder of repositories — and the runner discovers what is in it
/// (<c>DiscoverRepos</c>, read-only). A repository with an origin is registered as a remote
/// (branch push + forge PR with the developer's CLI), one without as a local folder
/// (squash-merged into it). The developer's checkouts are only ever read: task folders live
/// under <c>~/.orkka</c>, and a remote's mirror is seeded from the checkout.
/// </summary>
internal static class BaseFolderSync
{
    internal const string RunnerOffline =
        "Your runner is not running — start Orkka with `./orkka start` and try again. "
        + "Folders are read on your machine, by your runner.";

    internal const string RunnerTooOld =
        "Creating a project from a folder needs a newer runner — update Orkka (`git pull`, then `./orkka start`) and try again.";

    /// <summary>The runner's answer, or null when no runner could be reached at all.</summary>
    public static async Task<DiscoverReposResponseDto?> DiscoverAsync(
        IHttpClientFactory httpClientFactory, RunnerRelayTarget relay, string path, CancellationToken ct)
    {
        var result = await LocalRepoRelay.PostAsync<BaseFolderRelayDiscoverRequest, BaseFolderRelayDiscoverResponse>(
            httpClientFactory, relay, "/internal/runner/fs/discover",
            new BaseFolderRelayDiscoverRequest(relay.OrgId.ToString(), relay.UserId, path), ct);
        if (result is null)
            return null;

        return new DiscoverReposResponseDto(
            result.Supported, result.Path, result.Shape,
            [.. result.Repos.Select(ToDto)], result.Areas ?? [], result.Error);
    }

    private static DiscoveredRepoDto ToDto(BaseFolderRelayDiscoveredRepo r)
    {
        var isRemote = r.OriginUrl is { Length: > 0 } origin && RepoUrl.Classify(origin) == RepoLocation.Remote;
        return new DiscoveredRepoDto(
            r.RelativePath, r.Name, r.OriginUrl,
            isRemote ? "remote" : "local",
            isRemote ? RepoUrl.DetectForge(r.OriginUrl!).ToString().ToLowerInvariant() : null,
            isRemote ? RepoUrl.Host(r.OriginUrl) : null,
            r.DefaultBranch, r.CurrentBranch, r.IsDirty, r.HasUnpushedCommits, r.HasCommits,
            r.Description, r.Stack);
    }

    /// <summary>What a discovered repository is registered as: its origin when it has one (a
    /// remote), else its absolute folder (a local-folder repo). Null when it cannot be
    /// registered, with the reason.</summary>
    public static (string? GitUrl, string? FolderPath, string? Refusal) Registration(
        string baseFolder, DiscoveredRepoDto repo)
    {
        var folderPath = BaseFolders.NormalizeFolderPath(repo.RelativePath);
        if (folderPath is null || (folderPath.Length > 0 && !RepoRelativePath.IsSafeRelative(folderPath)))
            return (null, null, $"Skipped {repo.Name}: its path inside the folder is not usable ({repo.RelativePath}).");

        if (!repo.HasCommits)
            return (null, null,
                $"Skipped {repo.Name}: it has no commits yet. Commit once, then rescan the project.");

        if (repo.Location == "remote")
        {
            if (RepoUrl.HasEmbeddedCredential(repo.OriginUrl!))
                return (null, null,
                    $"Skipped {repo.Name}: its origin URL carries a credential. Remove it from the URL "
                    + "(your git credential manager or ssh key signs in instead) and rescan.");
            return (repo.OriginUrl, folderPath, null);
        }

        return (BaseFolders.CheckoutPath(baseFolder, folderPath), folderPath, null);
    }

    /// <summary>Primes the runner's mirror (seeded from the developer's checkout) so the repo
    /// browser and the first task are ready; a failure is recorded, never fatal — the mirror is
    /// retried on first use.</summary>
    public static async Task PrimeAsync(
        IHttpClientFactory httpClientFactory, RunnerRelayTarget relay, IProjectsApi projectsApi,
        RepoSummary repo, ILogger logger, CancellationToken ct)
    {
        BaseFolderRelayOpResponse? clone = null;
        try
        {
            clone = await LocalRepoRelay.PostAsync<BaseFolderRelayEnsureCloneRequest, BaseFolderRelayOpResponse>(
                httpClientFactory, relay, "/internal/runner/repo/ensure-clone",
                new BaseFolderRelayEnsureCloneRequest(
                    relay.OrgId.ToString(), relay.UserId, repo.Id.ToString(), repo.GitUrl, repo.LocalCheckoutPath),
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Priming the mirror of repo {RepoId} failed", repo.Id);
        }

        await projectsApi.SetRepoWorkspaceStatusAsync(
            repo.Id, clone is { Success: true } ? WorkspaceStatus.Ready : WorkspaceStatus.Error, ct);
        if (clone is not { Success: true })
            logger.LogWarning("Repo {RepoId} registered but its mirror is not ready yet: {Error}", repo.Id, clone?.Error);
    }

    /// <summary>A name not yet taken in the project: the folder name, else the relative path
    /// with dashes (two repos named "api" under different parents).</summary>
    public static string UniqueName(DiscoveredRepoDto repo, ISet<string> taken)
    {
        var name = repo.Name;
        if (taken.Contains(name) && repo.RelativePath.Length > 0)
            name = repo.RelativePath.Replace('/', '-');
        var candidate = name;
        for (var i = 2; taken.Contains(candidate); i++)
            candidate = $"{name}-{i}";
        taken.Add(candidate);
        return candidate;
    }

    public static string LocationOf(string gitUrl) =>
        RepoUrl.Classify(gitUrl) == RepoLocation.LocalFolder ? "local" : "remote";

    public static bool SameUrl(string left, string right)
    {
        static string Norm(string url)
        {
            var value = url.Trim().TrimEnd('/', '\\');
            if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                value = value[..^4];
            return value;
        }

        return string.Equals(Norm(left), Norm(right), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Preview of what a base folder holds, for the create-from-folder page. Asks the
/// CALLER's runner — a folder path only means something on the machine that has it.</summary>
public sealed class DiscoverReposEndpoint(
    ICurrentUser currentUser,
    IRunnerRelayLocator relayLocator,
    IHttpClientFactory httpClientFactory,
    ILogger<DiscoverReposEndpoint> logger)
    : Endpoint<DiscoverReposRequestDto, DiscoverReposResponseDto>
{
    public override void Configure()
    {
        Post("/api/runners/fs/discover");
    }

    public override async Task HandleAsync(DiscoverReposRequestDto req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Path))
        {
            ThrowError("A folder path is required.", 400);
            return;
        }

        var offline = new DiscoverReposResponseDto(false, null, null, [], [], BaseFolderSync.RunnerOffline);
        var relay = await relayLocator.LocateAsync(currentUser.OrgId, currentUser.UserId, ct);
        if (relay is null)
        {
            await Send.OkAsync(offline, ct);
            return;
        }

        try
        {
            var result = await BaseFolderSync.DiscoverAsync(httpClientFactory, relay, req.Path.Trim(), ct);
            await Send.OkAsync(result is null
                ? offline
                : result with { Error = result.Error ?? (result.Supported ? null : BaseFolderSync.RunnerOffline) },
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Repo discovery relay failed for org {OrgId}", currentUser.OrgId);
            await Send.OkAsync(offline, ct);
        }
    }
}

/// <summary>
/// Creates a project from a base folder: discovers its repositories on the caller's runner,
/// creates the project exactly as <c>POST /api/projects</c> does (so the agent roster is
/// seeded), records the base folder and its shape, registers every repository — a remote
/// for one with an origin, a local folder for one without, through the same Projects-module
/// paths the manual "add repo" flows use — and primes each mirror from the local checkout.
/// </summary>
public sealed class CreateProjectFromFolderEndpoint(
    ICurrentUser currentUser,
    IRunnerRelayLocator relayLocator,
    IHttpClientFactory httpClientFactory,
    IProjectsApi projectsApi,
    ILogger<CreateProjectFromFolderEndpoint> logger)
    : Endpoint<CreateProjectFromFolderRequest, CreateProjectFromFolderResponse>
{
    public override void Configure()
    {
        Post("/api/projects/from-folder");
    }

    public override async Task HandleAsync(CreateProjectFromFolderRequest req, CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.ProjectsCreate))
            return;

        if (string.IsNullOrWhiteSpace(req.Path))
        {
            ThrowError("A folder path is required.", 400);
            return;
        }
        if (req.Name is { Length: > 200 })
        {
            ThrowError("The project name must be 200 characters or fewer.", 400);
            return;
        }

        var relay = await relayLocator.LocateAsync(currentUser.OrgId, currentUser.UserId, ct);
        if (relay is null)
        {
            ThrowError(BaseFolderSync.RunnerOffline, 422);
            return;
        }

        var discovered = await BaseFolderSync.DiscoverAsync(httpClientFactory, relay, req.Path.Trim(), ct);
        if (discovered is not { Supported: true })
        {
            ThrowError(discovered?.Error ?? BaseFolderSync.RunnerOffline, 422);
            return;
        }
        if (discovered.Error is not null || discovered.Path is null || discovered.Shape is null)
        {
            ThrowError(discovered.Error ?? "That folder could not be read.", 422);
            return;
        }

        var warnings = new List<string>();
        var registrations = new List<(DiscoveredRepoDto Repo, string GitUrl, string FolderPath)>();
        foreach (var repo in discovered.Repos)
        {
            var (gitUrl, folderPath, refusal) = BaseFolderSync.Registration(discovered.Path, repo);
            if (refusal is not null)
                warnings.Add(refusal);
            else
                registrations.Add((repo, gitUrl!, folderPath!));
        }

        if (registrations.Count == 0)
        {
            ThrowError(warnings.Count > 0
                ? string.Join(" ", warnings)
                : "No git repositories were found in that folder or the two levels below it.", 422);
            return;
        }

        var name = string.IsNullOrWhiteSpace(req.Name)
            ? Path.GetFileName(discovered.Path.TrimEnd('\\', '/')) is { Length: > 0 } folderName ? folderName : "Project"
            : req.Name.Trim();
        var shape = discovered.Shape == DiscoveredShapes.Monorepo ? BaseFolderShape.Monorepo : BaseFolderShape.MultiRepo;

        var project = await projectsApi.CreateProjectAsync(
            new NewProject(currentUser.OrgId, currentUser.UserId, currentUser.Locale, name,
                discovered.Path, shape), ct);

        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var repos = new List<RepoSummary>(registrations.Count);
        foreach (var (repo, gitUrl, folderPath) in registrations)
        {
            var added = await projectsApi.AddDiscoveredRepoAsync(
                currentUser.OrgId, project.Id,
                new DiscoveredRepoRegistration(
                    BaseFolderSync.UniqueName(repo, taken), gitUrl,
                    repo.DefaultBranch ?? repo.CurrentBranch ?? "main", folderPath, repo.Description),
                ct);
            if (added is null)
                continue;

            await BaseFolderSync.PrimeAsync(httpClientFactory, relay, projectsApi, added, logger, ct);
            repos.Add(added);

            if (repo.IsDirty && added.Location() == "local")
                warnings.Add($"{added.Name} has uncommitted changes — finished tasks cannot land in it until it is clean.");
        }

        logger.LogInformation(
            "Created project {ProjectId} from base folder {BaseFolder} ({Shape}, {Count} repos)",
            project.Id, discovered.Path, discovered.Shape, repos.Count);

        var final = await projectsApi.ListReposAsync(project.Id, ct);
        await Send.ResponseAsync(new CreateProjectFromFolderResponse(
            new FolderProjectDto(project.Id, project.Name, project.Slug, project.Key, discovered.Path, discovered.Shape),
            [.. final.Select(r => new FolderRepoDto(
                r.Id, r.Name, r.GitUrl, r.DefaultBranch, r.FolderPath, BaseFolderSync.LocationOf(r.GitUrl),
                r.WorkspaceStatus.ToString().ToLowerInvariant(), r.Description))],
            [.. warnings]), 201, ct);
    }
}

/// <summary>
/// Re-reads a base-folder project's folder: new repositories are added, vanished ones get
/// <c>MissingSince</c> (their in-flight tasks get a note — nothing is stopped), reappearing ones
/// are cleared, and a changed origin is followed — unless a task on that repository is in
/// flight, which gets a warning instead. <c>?auto=true</c> is what opening the project sends:
/// at most one real rescan per project per ten minutes (the last result is returned meanwhile),
/// and an offline runner is a skip, not an error.
/// </summary>
public sealed class RescanProjectEndpoint(
    ICurrentUser currentUser,
    IProjectResolver projectResolver,
    IProjectsApi projectsApi,
    ITasksApi tasksApi,
    IRunnerRelayLocator relayLocator,
    IHttpClientFactory httpClientFactory,
    ILogger<RescanProjectEndpoint> logger)
    : EndpointWithoutRequest<RescanProjectResponse>
{
    internal static readonly TimeSpan AutoDebounce = TimeSpan.FromMinutes(10);

    private static readonly ConcurrentDictionary<Ulid, (DateTimeOffset At, RescanProjectResponse Result)> LastRescan = new();

    /// <summary>Forgets the debounce state — integration tests share one process.</summary>
    internal static void ResetDebounce() => LastRescan.Clear();

    public override void Configure()
    {
        Post("/api/projects/{projectId}/rescan");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.ReposManage))
            return;

        if (await projectResolver.ResolveAsync(Route<string>("projectId")!, ct) is not { } projectId
            || await projectsApi.GetProjectSummaryAsync(projectId, ct) is not { } project)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (project.BaseFolder is not { Length: > 0 } baseFolder)
        {
            ThrowError("This project was not created from a folder, so there is nothing to rescan.", 422);
            return;
        }

        var auto = Query<bool?>("auto", isRequired: false) ?? false;
        var now = DateTimeOffset.UtcNow;
        if (auto && LastRescan.TryGetValue(projectId, out var last) && now - last.At < AutoDebounce)
        {
            await Send.OkAsync(last.Result with { Skipped = true }, ct);
            return;
        }

        var relay = await relayLocator.LocateAsync(currentUser.OrgId, currentUser.UserId, ct);
        DiscoverReposResponseDto? discovered = null;
        if (relay is not null)
        {
            try
            {
                discovered = await BaseFolderSync.DiscoverAsync(httpClientFactory, relay, baseFolder, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Rescan relay failed for project {ProjectId}", projectId);
            }
        }

        if (relay is null || discovered is not { Supported: true })
        {
            if (auto)
            {
                await Send.OkAsync(RescanProjectResponse.OfflineSkip, ct);
                return;
            }
            ThrowError(discovered?.Error ?? BaseFolderSync.RunnerOffline, 422);
            return;
        }

        // The folder itself could not be read (unplugged drive, renamed folder): change nothing
        // rather than mark every repository missing on what may be a passing condition.
        if (discovered.Error is not null && discovered.Repos.Length == 0)
        {
            var unreadable = new RescanProjectResponse(false, false, [], [], [], [],
                [$"{baseFolder}: {discovered.Error}"], []);
            if (auto)
                LastRescan[projectId] = (now, unreadable);
            await Send.OkAsync(unreadable, ct);
            return;
        }

        var result = await ApplyAsync(project, baseFolder, discovered, relay!, now, ct);
        LastRescan[projectId] = (now, result);
        await Send.OkAsync(result, ct);
    }

    private async Task<RescanProjectResponse> ApplyAsync(
        ProjectSummary project, string baseFolder, DiscoverReposResponseDto discovered,
        RunnerRelayTarget relay, DateTimeOffset now, CancellationToken ct)
    {
        var added = new List<string>();
        var missing = new List<string>();
        var restored = new List<string>();
        var originUpdated = new List<string>();
        var warnings = new List<string>();
        var statuses = new List<RescanRepoStatusDto>();

        var repos = await projectsApi.ListReposAsync(project.Id, ct);
        var byFolder = repos
            .Where(r => r.FolderPath is not null)
            .GroupBy(r => r.FolderPath!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var taken = new HashSet<string>(repos.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<ActiveTaskInfo>? active = null;
        async Task<List<Ulid>> InFlightOn(Ulid repoId)
        {
            active ??= await tasksApi.ListActiveTasksAsync(project.Id, ct);
            return [.. active.Where(t => t.RepoId == repoId).Select(t => t.Id)];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var repo in discovered.Repos)
        {
            var (gitUrl, folderPath, refusal) = BaseFolderSync.Registration(baseFolder, repo);
            if (refusal is not null || gitUrl is null || folderPath is null)
            {
                if (refusal is not null && (folderPath is null || !byFolder.ContainsKey(folderPath)))
                    warnings.Add(refusal);
                if (BaseFolders.NormalizeFolderPath(repo.RelativePath) is { } known)
                    seen.Add(known);
                continue;
            }

            seen.Add(folderPath);

            if (!byFolder.TryGetValue(folderPath, out var existing))
            {
                var registered = await projectsApi.AddDiscoveredRepoAsync(
                    project.OrgId, project.Id,
                    new DiscoveredRepoRegistration(
                        BaseFolderSync.UniqueName(repo, taken), gitUrl,
                        repo.DefaultBranch ?? repo.CurrentBranch ?? "main", folderPath, repo.Description),
                    ct);
                if (registered is not null)
                {
                    await BaseFolderSync.PrimeAsync(httpClientFactory, relay, projectsApi, registered, logger, ct);
                    added.Add(registered.Name);
                    statuses.Add(new RescanRepoStatusDto(registered.Id, repo.IsDirty, repo.HasUnpushedCommits, repo.CurrentBranch));
                }
                continue;
            }

            statuses.Add(new RescanRepoStatusDto(existing.Id, repo.IsDirty, repo.HasUnpushedCommits, repo.CurrentBranch));

            if (existing.MissingSince is not null)
            {
                await projectsApi.SetRepoMissingAsync(existing.Id, null, ct);
                restored.Add(existing.Name);
            }

            if (!BaseFolderSync.SameUrl(existing.GitUrl, gitUrl))
            {
                var inFlight = await InFlightOn(existing.Id);
                if (inFlight.Count > 0)
                {
                    warnings.Add(
                        $"{existing.Name}: its origin is now {gitUrl}, but {inFlight.Count} task(s) on it are still in "
                        + "flight, so Orkka keeps using " + existing.GitUrl + " until they finish. Rescan afterwards.");
                }
                else
                {
                    await projectsApi.SetRepoGitUrlAsync(existing.Id, gitUrl, ct);
                    originUpdated.Add(existing.Name);
                    logger.LogInformation("Repo {RepoId} now follows {GitUrl}", existing.Id, gitUrl);
                }
            }
        }

        foreach (var (folderPath, repo) in byFolder)
        {
            if (seen.Contains(folderPath) || repo.MissingSince is not null)
                continue;

            await projectsApi.SetRepoMissingAsync(repo.Id, now, ct);
            missing.Add(repo.Name);

            var checkout = repo.LocalCheckoutPath ?? BaseFolders.CheckoutPath(baseFolder, folderPath);
            foreach (var taskId in await InFlightOn(repo.Id))
            {
                await tasksApi.AddTaskMessageAsync(taskId, "system",
                    $"The repository {repo.Name} is no longer at {checkout}. This task keeps running on Orkka's own "
                    + "copy; put the folder back (or rescan after moving it) before it lands.",
                    MessageType.SystemEvent, ct: ct);
            }
        }

        return new RescanProjectResponse(
            false, false, [.. added], [.. missing], [.. restored], [.. originUpdated], [.. warnings], [.. statuses]);
    }
}

internal static class RepoSummaryLocation
{
    public static string Location(this RepoSummary repo) => BaseFolderSync.LocationOf(repo.GitUrl);
}
