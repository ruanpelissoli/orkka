namespace Orkka.Runners.Server.Hosting;

using System.Text.Json;
using Orkka.LocalRunner.Contracts;
using Orkka.Runners.Server;

// Wire shapes for the API host's local-folder relay (mirrored by the file-scoped records
// next to the API's LocalRepoEndpoints — kept flat and web-cased; the two processes deploy
// together). Supported=false is the shared "this machine cannot answer" signal — runner
// offline or runner too old — because every caller reacts to those two the same way: hide
// the action rather than fail the request.

public sealed record RelayBrowseRequest(string OrgId, string UserId, string? Path);

public sealed record RelayBrowseEntry(string Name, string FullPath, bool IsGitRepo);

public sealed record RelayBrowseResponse(
    bool Supported, string? Path, string? ParentPath,
    RelayBrowseEntry[] Entries, string? Error);

public sealed record RelayInitRepoRequest(
    string OrgId, string UserId, string ParentPath, string Name);

public sealed record RelayLocalRepoResponse(
    bool Supported, bool Success, string? Path, string? DefaultBranch, string? Error);

/// <summary>InitIfNeeded turns a plain folder — or a repo with no commits — into a usable
/// one, so linking a directory the user never git-inited stays a single call.</summary>
public sealed record RelayPrepareRepoRequest(
    string OrgId, string UserId, string Path, bool InitIfNeeded);

public sealed record RelayRepoPathResponse(
    bool Supported, bool Exists, bool IsGitRepo,
    string? DefaultBranch, string? FolderName, string? Error,
    // What an in-place import kept out of the repository, and where the folder was copied
    // before it started. Only the init branch sets these; a plain validate leaves them null.
    string[]? ExcludedPaths = null, string? SnapshotPath = null);

public sealed record RelayPrMergeRequest(
    string OrgId, string UserId, string RepoId,
    string HeadBranch, string BaseBranch, string CommitMessage);

public sealed record RelayPrDiffRequest(
    string OrgId, string UserId, string RepoId, string TaskId,
    string BaseBranch, string HeadBranch, string? BaseSha = null);

public sealed record RelayPrConflictsRequest(
    string OrgId, string UserId, string RepoId, string HeadBranch, string BaseBranch);

public sealed record RelayPrPublishRequest(
    string OrgId, string UserId, string RepoId, string HeadBranch, string BaseBranch,
    string Title, string Body, string Forge, string? CliAccount, bool Draft = false);

public sealed record RelayPrReadyRequest(
    string OrgId, string UserId, string RepoId, string PullRequestUrl, string Forge, string? CliAccount, bool Undo);

public sealed record RelayPrBodyRequest(
    string OrgId, string UserId, string RepoId, string PullRequestUrl, string Forge, string? CliAccount, string Body);

public sealed record RelayPrStateRequest(
    string OrgId, string UserId, string RepoId, string PullRequestUrl, string Forge, string? CliAccount);

public sealed record RelayEnsureCloneRequest(
    string OrgId, string UserId, string RepoId, string CloneUrlOrPath, string? LocalCheckoutPath = null);

public sealed record RelayDiscoverRequest(string OrgId, string UserId, string Path);

public sealed record RelayDiscoveredRepo(
    string RelativePath, string Name, string? OriginUrl, string? DefaultBranch, string? CurrentBranch,
    bool IsDirty, bool HasUnpushedCommits, bool HasCommits, string? Description, string? Stack);

/// <summary>Supported=false: runner offline, or too old to discover repositories.</summary>
public sealed record RelayDiscoverResponse(
    bool Supported, string? Path, string? Shape, RelayDiscoveredRepo[] Repos, string[] Areas, string? Error);

public sealed record RelayAttachRemoteRequest(
    string OrgId, string UserId, string RepoToken, string RemoteUrl, bool Push);

public sealed record RelayRepoOpRequest(string OrgId, string UserId, string RepoToken);

public sealed record RelayPushRequest(
    string OrgId, string UserId, string RepoToken, string Branch);

public sealed record RelayOpResponse(
    bool Supported, bool Success, string? Value, string? Error);

public sealed record RelaySyncStatusResponse(
    bool Supported, bool IsGitRepo, bool HasRemote, bool HasUpstream,
    string? Branch, int Ahead, int Behind, string? Error, string? RemoteUrl = null);

/// <summary>
/// Cluster-internal relay for repositories that exist only as a FOLDER on the user's
/// machine.
///
/// Orkka Local reaches these operations through its in-process runner link; a hosted
/// project has no such link — the browser talks to the API host, and the API host has no
/// runner socket of its own. So the request lands here, on the one Worker pod holding that
/// user's WebSocket, exactly like chat and the editor ops.
///
/// Auth is the Worker↔API shared secret (X-Worker-Key). Like the rest of the relay these
/// must never be exposed through the public runner ingress, which routes only /hubs/runner.
/// </summary>
public static class LocalFolderRelayEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapLocalFolderRelay(this WebApplication app)
    {
        app.MapPost("/internal/runner/fs/browse", async (
            HttpContext http,
            MultiTenantRunnerLinkProvider links,
            CancellationToken ct) =>
        {
            if (!IsAuthorized(http, app.Configuration))
                return Results.Unauthorized();

            var req = await http.Request.ReadFromJsonAsync<RelayBrowseRequest>(Json, ct);
            if (req is null || !Ulid.TryParse(req.OrgId, out var orgId))
                return Results.BadRequest();

            var link = links.GetLink(orgId, req.UserId);
            if (!HasLocalFolders(link))
                return Results.Json(new RelayBrowseResponse(false, null, null, [], null), Json);

            try
            {
                var result = await link.InvokeAsync<DirectoryListing>(
                    RunnerProtocol.ListDirectory, [new ListDirectoryRequest(req.Path)], ct);

                return Results.Json(new RelayBrowseResponse(
                    true, result.Path, result.ParentPath,
                    [.. result.Entries.Select(e =>
                        new RelayBrowseEntry(e.Name, e.FullPath, e.IsGitRepo))],
                    result.Error),
                    Json);
            }
            catch (RunnerUnavailableException)
            {
                // Raced the disconnect. Same answer as offline — nothing to show.
                return Results.Json(new RelayBrowseResponse(false, null, null, [], null), Json);
            }
        });

        app.MapPost("/internal/runner/fs/init-repo", async (
            HttpContext http,
            MultiTenantRunnerLinkProvider links,
            CancellationToken ct) =>
        {
            if (!IsAuthorized(http, app.Configuration))
                return Results.Unauthorized();

            var req = await http.Request.ReadFromJsonAsync<RelayInitRepoRequest>(Json, ct);
            if (req is null || !Ulid.TryParse(req.OrgId, out var orgId))
                return Results.BadRequest();

            var link = links.GetLink(orgId, req.UserId);
            if (!HasLocalFolders(link))
                return Results.Json(new RelayLocalRepoResponse(false, false, null, null, null), Json);

            try
            {
                var result = await link.InvokeAsync<InitLocalRepoResult>(
                    RunnerProtocol.InitLocalRepo,
                    [new InitLocalRepoRequest(req.ParentPath, req.Name)], ct);

                return Results.Json(new RelayLocalRepoResponse(
                    true, result.Success, result.Path, result.DefaultBranch, result.Error), Json);
            }
            catch (RunnerUnavailableException ex)
            {
                return Results.Json(
                    new RelayLocalRepoResponse(false, false, null, null, ex.Message), Json);
            }
        });

        app.MapPost("/internal/runner/fs/prepare-repo", async (
            HttpContext http,
            MultiTenantRunnerLinkProvider links,
            CancellationToken ct) =>
        {
            if (!IsAuthorized(http, app.Configuration))
                return Results.Unauthorized();

            var req = await http.Request.ReadFromJsonAsync<RelayPrepareRepoRequest>(Json, ct);
            if (req is null || !Ulid.TryParse(req.OrgId, out var orgId))
                return Results.BadRequest();

            var link = links.GetLink(orgId, req.UserId);
            if (!HasLocalFolders(link))
            {
                return Results.Json(
                    new RelayRepoPathResponse(false, false, false, null, null, null), Json);
            }

            try
            {
                InitLocalRepoResult? imported = null;
                var info = await link.InvokeAsync<RepoPathInfo>(
                    RunnerProtocol.ValidateRepoPath, [new ValidateRepoPathRequest(req.Path)], ct);

                // validate → init → re-validate is three round trips to a laptop. Doing them
                // here, next to the socket, keeps it one from the API's side.
                if (info.Exists && !info.IsGitRepo && req.InitIfNeeded)
                {
                    var init = await link.InvokeAsync<InitLocalRepoResult>(
                        RunnerProtocol.InitRepoInPlace, [new InitRepoInPlaceRequest(req.Path)], ct);

                    if (!init.Success)
                    {
                        return Results.Json(new RelayRepoPathResponse(
                            true, true, false, null, null, init.Error), Json);
                    }

                    imported = init;
                    info = await link.InvokeAsync<RepoPathInfo>(
                        RunnerProtocol.ValidateRepoPath, [new ValidateRepoPathRequest(req.Path)], ct);
                }

                return Results.Json(new RelayRepoPathResponse(
                    true, info.Exists, info.IsGitRepo,
                    info.DefaultBranch, info.FolderName, info.Error,
                    imported?.ExcludedPaths, imported?.SnapshotPath), Json);
            }
            catch (RunnerUnavailableException ex)
            {
                return Results.Json(
                    new RelayRepoPathResponse(false, false, false, null, null, ex.Message), Json);
            }
        });

        app.MapPost("/internal/runner/repo/ensure-clone", async (
            HttpContext http,
            MultiTenantRunnerLinkProvider links,
            CancellationToken ct) =>
        {
            if (!IsAuthorized(http, app.Configuration))
                return Results.Unauthorized();

            var req = await http.Request.ReadFromJsonAsync<RelayEnsureCloneRequest>(Json, ct);
            if (req is null || !Ulid.TryParse(req.OrgId, out var orgId))
                return Results.BadRequest();

            var link = links.GetLink(orgId, req.UserId);
            if (!HasLocalFolders(link))
                return Results.Json(new RelayOpResponse(false, false, null, null), Json);

            try
            {
                var result = await link.InvokeAsync<RunnerOpResult>(
                    RunnerProtocol.EnsureBareClone,
                    [new EnsureBareCloneRequest(req.OrgId, req.RepoId, req.CloneUrlOrPath, req.LocalCheckoutPath)], ct);

                return Results.Json(
                    new RelayOpResponse(true, result.Success, result.Value, result.Error), Json);
            }
            catch (RunnerUnavailableException ex)
            {
                return Results.Json(new RelayOpResponse(false, false, null, ex.Message), Json);
            }
        });

        // Read-only: the repositories in a project's base folder (the folder itself, or the
        // repos up to two levels below it). Never git-inits or writes anything.
        app.MapPost("/internal/runner/fs/discover", async (
            HttpContext http,
            MultiTenantRunnerLinkProvider links,
            CancellationToken ct) =>
        {
            if (!IsAuthorized(http, app.Configuration))
                return Results.Unauthorized();

            var req = await http.Request.ReadFromJsonAsync<RelayDiscoverRequest>(Json, ct);
            if (req is null || !Ulid.TryParse(req.OrgId, out var orgId))
                return Results.BadRequest();

            var link = links.GetLink(orgId, req.UserId);
            var (connected, hello) = link.Status;
            if (!connected)
                return Results.Json(new RelayDiscoverResponse(false, null, null, [], [], null), Json);
            if (hello?.Capabilities?.Contains(RunnerCapabilities.RepoDiscovery) is not true)
            {
                return Results.Json(new RelayDiscoverResponse(
                    false, null, null, [], [],
                    "Creating a project from a folder needs a newer runner — update Orkka (`git pull`, then `./orkka start`) and try again."),
                    Json);
            }

            try
            {
                var result = await link.InvokeAsync<DiscoverReposResult>(
                    RunnerProtocol.DiscoverRepos, [new DiscoverReposRequest(req.Path)], ct);

                return Results.Json(new RelayDiscoverResponse(
                    true, result.Path, result.Shape,
                    [.. result.Repos.Select(r => new RelayDiscoveredRepo(
                        r.RelativePath, r.Name, r.OriginUrl, r.DefaultBranch, r.CurrentBranch,
                        r.IsDirty, r.HasUnpushedCommits, r.HasCommits, r.Description, r.Stack))],
                    result.Areas, result.Error),
                    Json);
            }
            catch (RunnerUnavailableException)
            {
                return Results.Json(new RelayDiscoverResponse(false, null, null, [], [], null), Json);
            }
        });

        app.MapPost("/internal/runner/repo/attach-remote", async (
            HttpContext http,
            MultiTenantRunnerLinkProvider links,
            CancellationToken ct) =>
        {
            if (!IsAuthorized(http, app.Configuration))
                return Results.Unauthorized();

            var req = await http.Request.ReadFromJsonAsync<RelayAttachRemoteRequest>(Json, ct);
            if (req is null || !Ulid.TryParse(req.OrgId, out var orgId))
                return Results.BadRequest();

            var link = links.GetLink(orgId, req.UserId);
            if (!HasLocalFolders(link))
            {
                return Results.Json(new RelayOpResponse(
                    false, false, null,
                    "Connecting this repository to GitHub needs a newer runner — update Orkka " +
                    "on that machine (`git pull`, then `./orkka start`) and try again."),
                    Json);
            }

            try
            {
                var result = await link.InvokeAsync<RunnerOpResult>(
                    RunnerProtocol.AttachRemote,
                    [new AttachRemoteRequest(req.RepoToken, req.RemoteUrl, req.Push)],
                    ct);

                return Results.Json(
                    new RelayOpResponse(true, result.Success, result.Value, result.Error), Json);
            }
            catch (RunnerUnavailableException ex)
            {
                return Results.Json(new RelayOpResponse(false, false, null, ex.Message), Json);
            }
        });

        app.MapPost("/internal/runner/repo/sync-status", async (
            HttpContext http,
            MultiTenantRunnerLinkProvider links,
            CancellationToken ct) =>
        {
            if (!IsAuthorized(http, app.Configuration))
                return Results.Unauthorized();

            var req = await http.Request.ReadFromJsonAsync<RelayRepoOpRequest>(Json, ct);
            if (req is null || !Ulid.TryParse(req.OrgId, out var orgId))
                return Results.BadRequest();

            var link = links.GetLink(orgId, req.UserId);
            if (!HasLocalFolders(link))
            {
                return Results.Json(new RelaySyncStatusResponse(
                    false, false, false, false, null, 0, 0, null), Json);
            }

            try
            {
                var result = await link.InvokeAsync<RunnerRemoteSyncStatus>(
                    RunnerProtocol.GetRemoteSyncStatus,
                    [new RemoteSyncStatusRequest(req.RepoToken)], ct);

                return Results.Json(new RelaySyncStatusResponse(
                    true, result.IsGitRepo, result.HasRemote, result.HasUpstream,
                    result.Branch, result.Ahead, result.Behind, result.Error, result.RemoteUrl), Json);
            }
            catch (RunnerUnavailableException)
            {
                return Results.Json(new RelaySyncStatusResponse(
                    false, false, false, false, null, 0, 0, null), Json);
            }
        });

        app.MapPost("/internal/runner/repo/push", async (
            HttpContext http,
            MultiTenantRunnerLinkProvider links,
            CancellationToken ct) =>
        {
            if (!IsAuthorized(http, app.Configuration))
                return Results.Unauthorized();

            var req = await http.Request.ReadFromJsonAsync<RelayPushRequest>(Json, ct);
            if (req is null || !Ulid.TryParse(req.OrgId, out var orgId))
                return Results.BadRequest();

            var link = links.GetLink(orgId, req.UserId);
            if (!HasLocalFolders(link))
            {
                return Results.Json(new RelayOpResponse(
                    false, false, null,
                    "Your runner is not connected — start it with `./orkka start`."),
                    Json);
            }

            try
            {
                var result = await link.InvokeAsync<RunnerOpResult>(
                    RunnerProtocol.PushToRemote,
                    [new PushToRemoteRequest(req.RepoToken, req.Branch)], ct);

                return Results.Json(
                    new RelayOpResponse(true, result.Success, result.Value, result.Error), Json);
            }
            catch (RunnerUnavailableException ex)
            {
                return Results.Json(new RelayOpResponse(false, false, null, ex.Message), Json);
            }
        });

        // ── Pull requests for folders on a user's machine ────────────────────
        // The three operations a synthetic pull request cannot serve itself, because the
        // repository is a folder on a laptop: merge it, read its diff, and ask whether it
        // would merge cleanly. See LocalPathPullRequestService, whose ILocalRepoGitOps these
        // three back.

        app.MapPost("/internal/runner/pr/merge", async (
            HttpContext http,
            MultiTenantRunnerLinkProvider links,
            CancellationToken ct) =>
        {
            if (!IsAuthorized(http, app.Configuration))
                return Results.Unauthorized();

            var req = await http.Request.ReadFromJsonAsync<RelayPrMergeRequest>(Json, ct);
            if (req is null || !Ulid.TryParse(req.OrgId, out var orgId))
                return Results.BadRequest();

            var link = links.GetLink(orgId, req.UserId);
            if (!HasLocalFolders(link))
            {
                return Results.Json(new RelayOpResponse(
                    false, false, null,
                    "This repository is a folder on your machine, and your runner is not "
                    + "connected — start it with `./orkka start` and merge again."),
                    Json);
            }

            try
            {
                var result = await link.InvokeAsync<RunnerOpResult>(
                    RunnerProtocol.SquashMergeIntoUserRepo,
                    [new SquashMergeRequest(
                        RunnerTokens.UserRepo(req.OrgId, req.RepoId),
                        req.HeadBranch, req.BaseBranch, req.CommitMessage)],
                    ct);

                return Results.Json(
                    new RelayOpResponse(true, result.Success, result.Value, result.Error), Json);
            }
            catch (RunnerUnavailableException ex)
            {
                return Results.Json(new RelayOpResponse(false, false, null, ex.Message), Json);
            }
        });

        // The Repo page of a hybrid project reading a folder on the user's machine: tree,
        // file, commits, branches, a commit's diff. See RepoReadRelay.
        app.MapPost("/internal/runner/repo/read", async (
            HttpContext http,
            MultiTenantRunnerLinkProvider links,
            CancellationToken ct) =>
        {
            if (!IsAuthorized(http, app.Configuration))
                return Results.Unauthorized();

            var req = await http.Request.ReadFromJsonAsync<RelayRepoReadRequest>(Json, ct);
            if (req is null || !Ulid.TryParse(req.OrgId, out var orgId))
                return Results.BadRequest();

            var link = links.GetLink(orgId, req.UserId);
            return Results.Json(await RepoReadRelay.ExecuteAsync(link, req, ct), Json);
        });

        app.MapPost("/internal/runner/pr/diff", async (
            HttpContext http,
            MultiTenantRunnerLinkProvider links,
            CancellationToken ct) =>
        {
            if (!IsAuthorized(http, app.Configuration))
                return Results.Unauthorized();

            var req = await http.Request.ReadFromJsonAsync<RelayPrDiffRequest>(Json, ct);
            if (req is null || !Ulid.TryParse(req.OrgId, out var orgId))
                return Results.BadRequest();

            var link = links.GetLink(orgId, req.UserId);
            if (!HasLocalFolders(link))
                return Results.Json(new RelayOpResponse(false, false, null, "Runner offline."), Json);

            try
            {
                // HeadBranch enables the runner's bare-mirror fallback when the worktree is
                // gone — a review must not silently render "+0 -0" for real changes.
                var result = await link.InvokeAsync<RunnerOpResult>(
                    RunnerProtocol.GetDiff,
                    [new GetDiffRequest(
                        req.OrgId, req.RepoId, req.TaskId, req.BaseBranch, 400_000, req.HeadBranch, req.BaseSha)],
                    ct);

                return Results.Json(
                    new RelayOpResponse(true, result.Success, result.Value, result.Error), Json);
            }
            catch (RunnerUnavailableException ex)
            {
                return Results.Json(new RelayOpResponse(false, false, null, ex.Message), Json);
            }
        });

        // A run in progress, as the Code View shows it: the worktree's diff against its base,
        // read from the developer's machine at the moment someone opens it. Never stored.
        app.MapPost("/internal/runner/pr/working-diff", async (
            HttpContext http,
            MultiTenantRunnerLinkProvider links,
            CancellationToken ct) =>
        {
            if (!IsAuthorized(http, app.Configuration))
                return Results.Unauthorized();

            var req = await http.Request.ReadFromJsonAsync<RelayPrDiffRequest>(Json, ct);
            if (req is null || !Ulid.TryParse(req.OrgId, out var orgId))
                return Results.BadRequest();

            var link = links.GetLink(orgId, req.UserId);
            if (!link.Status.Connected)
                return Results.Json(new RelayOpResponse(false, false, null, "Runner offline."), Json);

            try
            {
                var result = await link.InvokeAsync<RunnerOpResult>(
                    RunnerProtocol.GetWorkingTreeDiff,
                    [new GetWorkingTreeDiffRequest(req.OrgId, req.RepoId, req.TaskId, req.BaseBranch, 400_000, req.BaseSha)],
                    ct);

                return Results.Json(
                    new RelayOpResponse(true, result.Success, result.Value, result.Error), Json);
            }
            catch (RunnerUnavailableException ex)
            {
                return Results.Json(new RelayOpResponse(false, false, null, ex.Message), Json);
            }
        });

        // Publishing a reviewed task of a remote repository: the developer's runner opens the
        // pull request with their own forge CLI.
        app.MapPost("/internal/runner/pr/publish", async (
            HttpContext http,
            MultiTenantRunnerLinkProvider links,
            CancellationToken ct) =>
        {
            if (!IsAuthorized(http, app.Configuration))
                return Results.Unauthorized();

            var req = await http.Request.ReadFromJsonAsync<RelayPrPublishRequest>(Json, ct);
            if (req is null || !Ulid.TryParse(req.OrgId, out var orgId))
                return Results.BadRequest();

            var link = links.GetLink(orgId, req.UserId);
            if (!HasForgeCli(link))
                return Results.Json(new RelayOpResponse(false, false, null,
                    "Your runner is not connected (or too old to open pull requests) — run "
                    + "`git pull`, then `./orkka start`, and try again."), Json);
            // An older runner would ignore Draft and open a pull request ready for review — the
            // team would be asked to review half a story. Refuse instead.
            if (req.Draft && !HasStories(link))
                return Results.Json(new RelayOpResponse(false, false, null,
                    Orkka.Modules.SourceControl.Contracts.StoryForgeOps.UpdateRunner), Json);

            try
            {
                var result = await link.InvokeAsync<RunnerOpResult>(
                    RunnerProtocol.OpenPullRequest,
                    [new OpenPullRequestRequest(req.OrgId, req.RepoId, req.HeadBranch, req.BaseBranch,
                        req.Title, req.Body, req.Forge, req.CliAccount, req.Draft)],
                    ct);
                return Results.Json(new RelayOpResponse(true, result.Success, result.Value, result.Error), Json);
            }
            catch (RunnerUnavailableException ex)
            {
                return Results.Json(new RelayOpResponse(false, false, null, ex.Message), Json);
            }
        });

        // A Story's pull request: draft → ready, or back to draft when a sub-task joins late.
        app.MapPost("/internal/runner/pr/ready", async (
            HttpContext http,
            MultiTenantRunnerLinkProvider links,
            CancellationToken ct) =>
        {
            if (!IsAuthorized(http, app.Configuration))
                return Results.Unauthorized();

            var req = await http.Request.ReadFromJsonAsync<RelayPrReadyRequest>(Json, ct);
            if (req is null || !Ulid.TryParse(req.OrgId, out var orgId))
                return Results.BadRequest();

            var link = links.GetLink(orgId, req.UserId);
            if (!HasForgeCli(link) || !HasStories(link))
                return Results.Json(new RelayOpResponse(false, false, null,
                    Orkka.Modules.SourceControl.Contracts.StoryForgeOps.UpdateRunner), Json);

            try
            {
                var result = await link.InvokeAsync<RunnerOpResult>(
                    RunnerProtocol.MarkPullRequestReady,
                    [new MarkPullRequestReadyRequest(req.OrgId, req.RepoId, req.PullRequestUrl, req.Forge, req.CliAccount, req.Undo)],
                    ct);
                return Results.Json(new RelayOpResponse(true, result.Success, result.Value, result.Error), Json);
            }
            catch (RunnerUnavailableException ex)
            {
                return Results.Json(new RelayOpResponse(false, false, null, ex.Message), Json);
            }
        });

        app.MapPost("/internal/runner/pr/body", async (
            HttpContext http,
            MultiTenantRunnerLinkProvider links,
            CancellationToken ct) =>
        {
            if (!IsAuthorized(http, app.Configuration))
                return Results.Unauthorized();

            var req = await http.Request.ReadFromJsonAsync<RelayPrBodyRequest>(Json, ct);
            if (req is null || !Ulid.TryParse(req.OrgId, out var orgId))
                return Results.BadRequest();

            var link = links.GetLink(orgId, req.UserId);
            if (!HasForgeCli(link) || !HasStories(link))
                return Results.Json(new RelayOpResponse(false, false, null,
                    Orkka.Modules.SourceControl.Contracts.StoryForgeOps.UpdateRunner), Json);

            try
            {
                var result = await link.InvokeAsync<RunnerOpResult>(
                    RunnerProtocol.UpdatePullRequestBody,
                    [new UpdatePullRequestBodyRequest(req.OrgId, req.RepoId, req.PullRequestUrl, req.Forge, req.Body, req.CliAccount)],
                    ct);
                return Results.Json(new RelayOpResponse(true, result.Success, result.Value, result.Error), Json);
            }
            catch (RunnerUnavailableException ex)
            {
                return Results.Json(new RelayOpResponse(false, false, null, ex.Message), Json);
            }
        });

        app.MapPost("/internal/runner/pr/state", async (
            HttpContext http,
            MultiTenantRunnerLinkProvider links,
            CancellationToken ct) =>
        {
            if (!IsAuthorized(http, app.Configuration))
                return Results.Unauthorized();

            var req = await http.Request.ReadFromJsonAsync<RelayPrStateRequest>(Json, ct);
            if (req is null || !Ulid.TryParse(req.OrgId, out var orgId))
                return Results.BadRequest();

            var link = links.GetLink(orgId, req.UserId);
            if (!HasForgeCli(link))
                return Results.Json(new RelayOpResponse(false, false, null, "Runner offline."), Json);

            try
            {
                var result = await link.InvokeAsync<RunnerOpResult>(
                    RunnerProtocol.GetPullRequestState,
                    [new GetPullRequestStateRequest(req.OrgId, req.RepoId, req.PullRequestUrl, req.Forge, req.CliAccount)],
                    ct);
                return Results.Json(new RelayOpResponse(true, result.Success, result.Value, result.Error), Json);
            }
            catch (RunnerUnavailableException ex)
            {
                return Results.Json(new RelayOpResponse(false, false, null, ex.Message), Json);
            }
        });

        app.MapPost("/internal/runner/pr/conflicts", async (
            HttpContext http,
            MultiTenantRunnerLinkProvider links,
            CancellationToken ct) =>
        {
            if (!IsAuthorized(http, app.Configuration))
                return Results.Unauthorized();

            var req = await http.Request.ReadFromJsonAsync<RelayPrConflictsRequest>(Json, ct);
            if (req is null || !Ulid.TryParse(req.OrgId, out var orgId))
                return Results.BadRequest();

            var link = links.GetLink(orgId, req.UserId);
            if (!HasLocalFolders(link))
                return Results.Json(new RelayOpResponse(false, false, null, "Runner offline."), Json);

            try
            {
                var result = await link.InvokeAsync<RunnerOpResult>(
                    RunnerProtocol.GetMergeConflicts,
                    [new GetMergeConflictsRequest(
                        req.OrgId, req.RepoId, req.HeadBranch, req.BaseBranch)],
                    ct);

                return Results.Json(
                    new RelayOpResponse(true, result.Success, result.Value, result.Error), Json);
            }
            catch (RunnerUnavailableException ex)
            {
                return Results.Json(new RelayOpResponse(false, false, null, ex.Message), Json);
            }
        });
    }

    /// <summary>Connected AND new enough for a Story's draft / ready pull requests.</summary>
    private static bool HasStories(IRunnerLink link)
    {
        var (connected, hello) = link.Status;
        return connected && hello?.Capabilities?.Contains(RunnerCapabilities.Stories) is true;
    }

    /// <summary>Connected AND able to drive the developer's forge CLI.</summary>
    private static bool HasForgeCli(IRunnerLink link)
    {
        var (connected, hello) = link.Status;
        return connected
            && hello?.Capabilities?.Contains(RunnerCapabilities.ForgeCli) is true;
    }

    /// <summary>Connected AND new enough for the local-folder ops. Both failures collapse
    /// into Supported=false, which is all any caller needs in order to decide.</summary>
    private static bool HasLocalFolders(IRunnerLink link)
    {
        var (connected, hello) = link.Status;
        return connected
            && hello?.Capabilities?.Contains(RunnerCapabilities.LocalFolders) is true;
    }

    private static bool IsAuthorized(HttpContext http, IConfiguration configuration)
        => InternalRelayAuth.IsAuthorized(http, configuration);
}
