namespace Orkka.Modules.Runners.Features.LocalRepos;

using Orkka.SharedKernel.Utilities;

using System.Net.Http.Json;
using System.Text.Json;
using FastEndpoints;
using Microsoft.Extensions.Logging;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Runners.Contracts;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.SharedKernel.Security;

// ── Public request/response shapes ───────────────────────────────────────────

public sealed record BrowseDirectoryRequest(string? Path);

public sealed record BrowseEntryResponse(string Name, string FullPath, bool IsGitRepo);

/// <summary>Supported=false means "this user has no machine that can answer right now" —
/// runner offline, or too old for the local-folder ops. The UI hides the flow rather than
/// showing an error for something the user cannot act on from the browser.</summary>
public sealed record BrowseDirectoryResponse(
    bool Supported, string? Path, string? ParentPath,
    BrowseEntryResponse[] Entries, string? Error);

public sealed record InitLocalRepoRequestDto(string ParentPath, string Name);

public sealed record InitLocalRepoResponseDto(string Path, string DefaultBranch);

public sealed record LinkLocalRepoRequest(string Path);

/// <param name="ExcludedPaths">Present only when linking turned a plain folder into a
/// repository: what was deliberately left out of it (dependency folders, build output, .env).
/// Those files are still on the user's disk — they are simply outside the project's history,
/// and the UI says so rather than letting it be discovered later.</param>
/// <param name="SnapshotPath">Where the folder was copied before the import touched it.</param>
public sealed record LinkLocalRepoResponse(
    string Id, string Name, string Path, string DefaultBranch,
    string[]? ExcludedPaths = null, string? SnapshotPath = null);

/// <param name="RemoteUrl">An empty repository the developer created on their own forge.</param>
public sealed record AttachRemoteRequestDto(string? RemoteUrl);

public sealed record AttachRemoteResponseDto(string RemoteUrl);

public sealed record RepoSyncStatusResponse(
    bool Supported, bool IsGitRepo, bool HasRemote, bool HasUpstream,
    string? Branch, int Ahead, int Behind, string? Error, string? RemoteUrl = null);

public sealed record PushRepoRequest(string? Branch);

// Wire shapes mirroring the Worker's LocalFolderRelayEndpoints. File-scoped, like the
// editor endpoints do it: the two processes deploy together, so the duplication is
// deliberate and keeps the API from taking a reference on the Worker.
file sealed record RelayBrowseRequest(string OrgId, string UserId, string? Path);

file sealed record RelayBrowseEntry(string Name, string FullPath, bool IsGitRepo);

file sealed record RelayBrowseResponse(
    bool Supported, string? Path, string? ParentPath,
    RelayBrowseEntry[] Entries, string? Error);

file sealed record RelayInitRepoRequest(
    string OrgId, string UserId, string ParentPath, string Name);

file sealed record RelayLocalRepoResponse(
    bool Supported, bool Success, string? Path, string? DefaultBranch, string? Error);

file sealed record RelayPrepareRepoRequest(
    string OrgId, string UserId, string Path, bool InitIfNeeded);

file sealed record RelayRepoPathResponse(
    bool Supported, bool Exists, bool IsGitRepo,
    string? DefaultBranch, string? FolderName, string? Error,
    string[]? ExcludedPaths = null, string? SnapshotPath = null);

file sealed record RelayEnsureCloneRequest(
    string OrgId, string UserId, string RepoId, string CloneUrlOrPath);

file sealed record RelayAttachRemoteRequest(
    string OrgId, string UserId, string RepoToken, string RemoteUrl, bool Push);

file sealed record RelayRepoOpRequest(string OrgId, string UserId, string RepoToken);

file sealed record RelayPushRequest(
    string OrgId, string UserId, string RepoToken, string Branch);

file sealed record RelayOpResponse(
    bool Supported, bool Success, string? Value, string? Error);

file sealed record RelaySyncStatusResponse(
    bool Supported, bool IsGitRepo, bool HasRemote, bool HasUpstream,
    string? Branch, int Ahead, int Behind, string? Error, string? RemoteUrl = null);

/// <summary>
/// Shared plumbing for the local-folder endpoints: they all forward to the caller's OWN
/// runner, because a folder path is only meaningful on the machine that holds it and
/// nobody may enumerate a colleague's disk.
/// </summary>
internal static class LocalRepoRelay
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal const string RunnerOffline =
        "Your runner is not connected — start it on your machine with `./orkka start` " +
        "to work with folders that live there.";

    public static async Task<TResponse?> PostAsync<TRequest, TResponse>(
        IHttpClientFactory httpClientFactory, RunnerRelayTarget relay,
        string route, TRequest body, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(RunnerRelay.HttpClientName);
        var response = await client.PostAsJsonAsync(
            $"{relay.BaseUrl}{route}", body, Json, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TResponse>(Json, ct);
    }

    /// <summary>Null when the repo is not the caller's to touch (unknown / other org /
    /// other project) or the caller has no online runner holding it.</summary>
    public static async Task<(RepoSummary Repo, RunnerRelayTarget Relay)?> ResolveRepoAsync(
        string projectIdOrSlug, Ulid repoId, IProjectResolver projectResolver,
        IProjectsApi projectsApi, ICurrentUser currentUser, IRunnerRelayLocator relayLocator,
        CancellationToken ct)
    {
        if (await projectResolver.ResolveAsync(projectIdOrSlug, ct) is not { } projectId)
            return null;

        var repo = await projectsApi.GetRepoAsync(repoId, ct);
        if (repo is null || repo.OrgId != currentUser.OrgId || repo.ProjectId != projectId)
            return null;

        var relay = await relayLocator.LocateAsync(currentUser.OrgId, currentUser.UserId, ct);
        return relay is null ? null : (repo, relay);
    }
}

/// <summary>
/// Lists directories on the CALLER's machine so the browser can offer a folder picker for a
/// hosted project. Orkka Local serves this from its in-process runner link; here it travels
/// API → Worker pod holding the socket → runner.
/// </summary>
public sealed class BrowseDirectoryEndpoint(
    ICurrentUser currentUser,
    IRunnerRelayLocator relayLocator,
    IHttpClientFactory httpClientFactory,
    ILogger<BrowseDirectoryEndpoint> logger)
    : Endpoint<BrowseDirectoryRequest, BrowseDirectoryResponse>
{
    public override void Configure()
    {
        Post("/api/runners/fs/browse");
    }

    public override async Task HandleAsync(BrowseDirectoryRequest req, CancellationToken ct)
    {
        var relay = await relayLocator.LocateAsync(currentUser.OrgId, currentUser.UserId, ct);
        if (relay is null)
        {
            await Send.OkAsync(new BrowseDirectoryResponse(false, null, null, [], null), ct);
            return;
        }

        try
        {
            var result = await LocalRepoRelay.PostAsync<RelayBrowseRequest, RelayBrowseResponse>(
                httpClientFactory, relay, "/internal/runner/fs/browse",
                new RelayBrowseRequest(currentUser.OrgId.ToString(), relay.UserId, req.Path), ct);

            await Send.OkAsync(new BrowseDirectoryResponse(
                result?.Supported ?? false, result?.Path, result?.ParentPath,
                [.. (result?.Entries ?? []).Select(e =>
                    new BrowseEntryResponse(e.Name, e.FullPath, e.IsGitRepo))],
                result?.Error),
                ct);
        }
        catch (Exception ex)
        {
            // A relay hiccup is indistinguishable from an offline runner as far as the
            // picker is concerned, and neither is worth a 500.
            logger.LogWarning(ex, "Directory browse relay failed for org {OrgId}", currentUser.OrgId);
            await Send.OkAsync(new BrowseDirectoryResponse(false, null, null, [], null), ct);
        }
    }
}

/// <summary>Creates a brand-new folder + git repository on the caller's machine, for the
/// "start something from scratch" path in the folder picker. Linking it to a project is a
/// separate call, so the picker can also use this before a project exists.</summary>
public sealed class InitLocalRepoEndpoint(
    ICurrentUser currentUser,
    IRunnerRelayLocator relayLocator,
    IHttpClientFactory httpClientFactory)
    : Endpoint<InitLocalRepoRequestDto, InitLocalRepoResponseDto>
{
    public override void Configure()
    {
        Post("/api/runners/fs/init-repo");
    }

    public override async Task HandleAsync(InitLocalRepoRequestDto req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.ParentPath) || string.IsNullOrWhiteSpace(req.Name))
        {
            ThrowError("A parent folder and a name are required.", 400);
            return;
        }

        var relay = await relayLocator.LocateAsync(currentUser.OrgId, currentUser.UserId, ct);
        if (relay is null)
        {
            ThrowError(LocalRepoRelay.RunnerOffline, 422);
            return;
        }

        var result = await LocalRepoRelay.PostAsync<RelayInitRepoRequest, RelayLocalRepoResponse>(
            httpClientFactory, relay, "/internal/runner/fs/init-repo",
            new RelayInitRepoRequest(
                currentUser.OrgId.ToString(), relay.UserId, req.ParentPath, req.Name),
            ct);

        if (result is not { Supported: true })
        {
            ThrowError(
                "Creating a repository needs a newer runner on that machine — update Orkka " +
                "(`git pull`, then `./orkka start`) and try again.", 422);
            return;
        }
        if (!result.Success)
        {
            ThrowError(result.Error ?? "Could not create the repository.", 422);
            return;
        }

        await Send.OkAsync(new InitLocalRepoResponseDto(
            result.Path!, result.DefaultBranch ?? "main"), ct);
    }
}

/// <summary>
/// Links a folder on the caller's machine to a hosted project as a repository.
///
/// A plain directory is fine: the runner git-inits it and commits what is already there, so
/// "I have a folder, not a repo" is a supported starting point rather than a prerequisite the
/// user has to satisfy in a terminal first. What lands in the database is
/// Provider=LocalPath with the absolute path as GitUrl — the same shape Orkka Local uses,
/// which the execution pipeline already understands end to end.
/// </summary>
public sealed class LinkLocalRepoEndpoint(
    IProjectResolver projectResolver,
    IProjectsApi projectsApi,
    ICurrentUser currentUser,
    IRunnerRelayLocator relayLocator,
    IHttpClientFactory httpClientFactory,
    ILogger<LinkLocalRepoEndpoint> logger)
    : Endpoint<LinkLocalRepoRequest, LinkLocalRepoResponse>
{
    public override void Configure()
    {
        Post("/api/projects/{projectId}/repos/local");
    }

    public override async Task HandleAsync(LinkLocalRepoRequest req, CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.ReposManage))
            return;

        if (string.IsNullOrWhiteSpace(req.Path))
        {
            ThrowError("A folder path is required.", 400);
            return;
        }

        if (await projectResolver.ResolveAsync(Route<string>("projectId")!, ct) is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var relay = await relayLocator.LocateAsync(currentUser.OrgId, currentUser.UserId, ct);
        if (relay is null)
        {
            ThrowError(LocalRepoRelay.RunnerOffline, 422);
            return;
        }

        // One hop: validate, git-init if it isn't a repo yet, and re-validate — all on the
        // pod next to the socket rather than three round trips from here.
        var info = await LocalRepoRelay.PostAsync<RelayPrepareRepoRequest, RelayRepoPathResponse>(
            httpClientFactory, relay, "/internal/runner/fs/prepare-repo",
            new RelayPrepareRepoRequest(
                currentUser.OrgId.ToString(), relay.UserId, req.Path, InitIfNeeded: true),
            ct);

        if (info is not { Supported: true })
        {
            ThrowError(
                "Linking a folder needs a newer runner on that machine — update Orkka " +
                "(`git pull`, then `./orkka start`) and try again.", 422);
            return;
        }
        if (!info.Exists)
        {
            ThrowError(
                info.Error ?? $"That folder does not exist on your machine: {req.Path}", 422);
            return;
        }
        if (!info.IsGitRepo)
        {
            ThrowError(
                info.Error ?? $"Could not make {req.Path} a git repository.", 422);
            return;
        }

        var name = info.FolderName
            ?? req.Path.TrimEnd('\\', '/').Split('\\', '/').LastOrDefault()
            ?? "repository";

        var repo = await projectsApi.AddLocalRepoAsync(
            currentUser.OrgId, projectId, req.Path, name, info.DefaultBranch ?? "main", ct);

        if (repo is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // Prime the runner's bare mirror so the repo browser and agents are ready. A failure
        // here is recorded, not fatal: the link is real and the mirror is retried on use.
        var clone = await LocalRepoRelay.PostAsync<RelayEnsureCloneRequest, RelayOpResponse>(
            httpClientFactory, relay, "/internal/runner/repo/ensure-clone",
            new RelayEnsureCloneRequest(
                currentUser.OrgId.ToString(), relay.UserId, repo.Id.ToString(), req.Path),
            ct);

        await projectsApi.SetRepoWorkspaceStatusAsync(
            repo.Id,
            clone is { Success: true } ? WorkspaceStatus.Ready : WorkspaceStatus.Error,
            ct);

        if (clone is not { Success: true })
        {
            logger.LogWarning(
                "Local repo {RepoId} linked but the runner could not prepare it: {Error}",
                repo.Id, clone?.Error);
        }

        logger.LogInformation(
            "Linked local folder as repo {RepoId} on project {ProjectId}", repo.Id, projectId);

        await Send.OkAsync(new LinkLocalRepoResponse(
            repo.Id.ToString(), repo.Name, repo.GitUrl, repo.DefaultBranch,
            info.ExcludedPaths, info.SnapshotPath), ct);
    }
}

/// <summary>
/// Gives a linked local folder a remote and pushes it there.
///
/// The developer creates the empty repository on their own forge first; Orkka creates nothing
/// and holds no forge credential. The push runs on their runner with their own git credentials
/// (https through their credential manager, ssh through their keys). The repository stays a
/// folder: the folder remains the working copy, the remote is where it is published.
/// </summary>
public sealed class AttachRepoRemoteEndpoint(
    IProjectResolver projectResolver,
    IProjectsApi projectsApi,
    ICurrentUser currentUser,
    IRunnerRelayLocator relayLocator,
    IHttpClientFactory httpClientFactory,
    ILogger<AttachRepoRemoteEndpoint> logger)
    : Endpoint<AttachRemoteRequestDto, AttachRemoteResponseDto>
{
    public override void Configure()
    {
        Post("/api/projects/{projectId}/repos/{repoId:ulid}/attach-remote");
    }

    public override async Task HandleAsync(AttachRemoteRequestDto req, CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.ReposManage))
            return;

        var resolved = await LocalRepoRelay.ResolveRepoAsync(
            Route<string>("projectId")!, Ulid.Parse(Route<string>("repoId")),
            projectResolver, projectsApi, currentUser, relayLocator, ct);

        if (resolved is not { } target)
        {
            ThrowError(LocalRepoRelay.RunnerOffline, 422);
            return;
        }

        if (!TryNormalizeRemoteUrl(req.RemoteUrl, out var normalized))
        {
            ThrowError(
                "Enter the URL of an empty repository you created — https "
                + "(https://github.com/you/your-repo) or ssh (git@github.com:you/your-repo.git).", 400);
            return;
        }

        var repoToken = RunnerTokens.UserRepo(
            currentUser.OrgId.ToString(), target.Repo.Id.ToString());

        var result = await LocalRepoRelay.PostAsync<RelayAttachRemoteRequest, RelayOpResponse>(
            httpClientFactory, target.Relay, "/internal/runner/repo/attach-remote",
            new RelayAttachRemoteRequest(
                currentUser.OrgId.ToString(), target.Relay.UserId,
                repoToken, normalized, Push: true),
            ct);

        if (result is not { Supported: true })
        {
            ThrowError(
                result?.Error
                ?? "Connecting this repository to a remote needs a newer runner — update Orkka "
                   + "on that machine (`git pull`, then `./orkka start`) and try again.", 422);
            return;
        }
        if (!result.Success)
        {
            ThrowError(result.Error ?? "Could not connect the repository to its remote.", 422);
            return;
        }

        logger.LogInformation("Repo {RepoId} attached to {Remote}", target.Repo.Id, normalized);
        await Send.OkAsync(new AttachRemoteResponseDto(result.Value ?? normalized), ct);
    }

    /// <summary>
    /// Accepts what a developer actually pastes — an https clone URL, an ssh or scp-style
    /// remote, or GitHub's owner/repo shorthand — and returns it as git will use it. Rejects
    /// anything carrying credentials (they would be written into the folder's git config) and
    /// anything that is not a remote at all.
    /// </summary>
    internal static bool TryNormalizeRemoteUrl(string? input, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var value = input.Trim();

        // Bare "owner/repo" is the shorthand people type when they mean github.com.
        if (!value.Contains("://", StringComparison.Ordinal) && !value.Contains(':')
            && value.Trim('/').Split('/') is [{ Length: > 0 }, { Length: > 0 }])
        {
            value = $"https://github.com/{value.Trim('/')}";
        }

        if (!RepoUrl.IsRemote(value))
            return false;

        if (value.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
                return false;

            if (RepoUrl.HasEmbeddedCredential(value))
                return false;

            var isHttp = uri.Scheme is "https" or "http";

            if (isHttp)
            {
                // Deep links (a file, a branch, a PR) are a common copy-paste mistake on the
                // big forges; the repository is the first two segments there.
                var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length < 2)
                    return false;
                if (RepoUrl.DetectForge(value) is ForgeKind.GitHub or ForgeKind.GitLab
                    && segments.Length > 2 && segments[2] is "tree" or "blob" or "pull" or "-")
                {
                    var name = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
                        ? segments[1][..^4]
                        : segments[1];
                    value = $"{uri.Scheme}://{uri.Authority}/{segments[0]}/{name}.git";
                }
            }
        }

        normalized = value;
        return true;
    }
}

/// <summary>How a linked folder stands against its own origin — what the push button shows
/// (and whether it is offered at all).</summary>
public sealed class RepoSyncStatusEndpoint(
    IProjectResolver projectResolver,
    IProjectsApi projectsApi,
    ICurrentUser currentUser,
    IRunnerRelayLocator relayLocator,
    IHttpClientFactory httpClientFactory,
    ILogger<RepoSyncStatusEndpoint> logger)
    : EndpointWithoutRequest<RepoSyncStatusResponse>
{
    public override void Configure()
    {
        Get("/api/projects/{projectId}/repos/{repoId:ulid}/sync-status");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var offline = new RepoSyncStatusResponse(false, false, false, false, null, 0, 0, null);

        var resolved = await LocalRepoRelay.ResolveRepoAsync(
            Route<string>("projectId")!, Ulid.Parse(Route<string>("repoId")),
            projectResolver, projectsApi, currentUser, relayLocator, ct);

        if (resolved is not { } target)
        {
            await Send.OkAsync(offline, ct);
            return;
        }

        try
        {
            var repoToken = RunnerTokens.UserRepo(
                currentUser.OrgId.ToString(), target.Repo.Id.ToString());

            var result = await LocalRepoRelay.PostAsync<RelayRepoOpRequest, RelaySyncStatusResponse>(
                httpClientFactory, target.Relay, "/internal/runner/repo/sync-status",
                new RelayRepoOpRequest(
                    currentUser.OrgId.ToString(), target.Relay.UserId, repoToken),
                ct);

            await Send.OkAsync(result is null
                ? offline
                : new RepoSyncStatusResponse(
                    result.Supported, result.IsGitRepo, result.HasRemote, result.HasUpstream,
                    result.Branch, result.Ahead, result.Behind, result.Error, result.RemoteUrl),
                ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Sync status relay failed for repo {RepoId}", target.Repo.Id);
            await Send.OkAsync(offline, ct);
        }
    }
}

/// <summary>Pushes the linked folder's current branch to its origin with the developer's own
/// git credentials — the manual counterpart to what an agent's task push does.</summary>
public sealed class PushRepoEndpoint(
    IProjectResolver projectResolver,
    IProjectsApi projectsApi,
    ICurrentUser currentUser,
    IRunnerRelayLocator relayLocator,
    IHttpClientFactory httpClientFactory)
    : Endpoint<PushRepoRequest>
{
    public override void Configure()
    {
        Post("/api/projects/{projectId}/repos/{repoId:ulid}/push");
    }

    public override async Task HandleAsync(PushRepoRequest req, CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.ReposManage))
            return;

        var resolved = await LocalRepoRelay.ResolveRepoAsync(
            Route<string>("projectId")!, Ulid.Parse(Route<string>("repoId")),
            projectResolver, projectsApi, currentUser, relayLocator, ct);

        if (resolved is not { } target)
        {
            ThrowError(LocalRepoRelay.RunnerOffline, 422);
            return;
        }

        var repoToken = RunnerTokens.UserRepo(
            currentUser.OrgId.ToString(), target.Repo.Id.ToString());

        var status = await LocalRepoRelay.PostAsync<RelayRepoOpRequest, RelaySyncStatusResponse>(
            httpClientFactory, target.Relay, "/internal/runner/repo/sync-status",
            new RelayRepoOpRequest(currentUser.OrgId.ToString(), target.Relay.UserId, repoToken), ct);
        if (status is not { Supported: true, HasRemote: true })
        {
            ThrowError("This folder has no remote yet — connect it to a repository first.", 422);
            return;
        }

        var result = await LocalRepoRelay.PostAsync<RelayPushRequest, RelayOpResponse>(
            httpClientFactory, target.Relay, "/internal/runner/repo/push",
            new RelayPushRequest(
                currentUser.OrgId.ToString(), target.Relay.UserId,
                repoToken, req.Branch ?? target.Repo.DefaultBranch),
            ct);

        if (result is not { Supported: true })
        {
            ThrowError(result?.Error ?? LocalRepoRelay.RunnerOffline, 422);
            return;
        }
        if (!result.Success)
        {
            ThrowError(result.Error ?? "Could not push to the remote.", 422);
            return;
        }

        await Send.NoContentAsync(ct);
    }
}
