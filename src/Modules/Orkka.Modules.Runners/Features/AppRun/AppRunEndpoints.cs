namespace Orkka.Modules.Runners.Features.AppRun;

using System.Net.Http.Json;
using System.Text.Json;
using FastEndpoints;
using Microsoft.Extensions.Logging;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Runners.Contracts;
using Orkka.SharedKernel.Security;

public static class AppRunRelay
{
    /// <summary>Named client for app-run relay ops. Separate from the editor client because
    /// Start legitimately runs for minutes (staggered stack spawns) and needs a timeout to
    /// match, which a stuck editor launch must not inherit.</summary>
    public const string HttpClientName = "runner-relay-apprun";
}

// ── Wire records (mirror RunnerRelayEndpoints on the Worker; the two deploy together) ──

public sealed record RelayAppRunTarget(
    string RunId, string Key, string Label, string Kind, string RepoId,
    string Command, string? WorkingDirectory, int? Port, string? Url, string[] DependsOn,
    string? InstallCommand = null, string? StopCommand = null, string? PreviewPath = null,
    bool RestartOnMerge = false);

public sealed record RelayAppRunAttachRequest(string OrgId, string UserId, string ProjectId);

public sealed record RelayAppRunStartRequest(
    string OrgId, string UserId, string ProjectId,
    RelayAppRunTarget[] Targets, bool ForceFreePorts);

public sealed record RelayAppRunStopRequest(
    string OrgId, string UserId, string ProjectId, string? RunId);

public sealed record RelayAppRunInputRequest(
    string OrgId, string UserId, string RunId, string DataBase64);

public sealed record RelayAppRunResizeRequest(
    string OrgId, string UserId, string RunId, int Cols, int Rows);

/// <summary>Snapshot as the browser sees it — same shape the Local edition's hub sends, so
/// the two Run docks stay interchangeable.</summary>
public sealed record AppRunSnapshotDto(
    string RunId,
    string Key,
    string Label,
    string Kind,
    string RepoId,
    int? Port,
    string? Url,
    string Status,
    int? ExitCode,
    string? Error,
    bool PortConflict,
    DateTimeOffset StartedAt,
    string? Scrollback,
    bool Ready = true,
    string? PreviewUrl = null);

public sealed record RelayAppRunResponse(
    bool Supported, AppRunSnapshotDto[] Snapshots, string? Error = null);

public sealed record AppRunResponse(
    bool Supported, AppRunSnapshotDto[] Snapshots, string? Error = null);

public sealed record StartAppRunApiRequest(string[]? TargetKeys, bool ForceFreePorts);

public sealed record StopAppRunApiRequest(string? RunId);

public sealed record AppRunInputApiRequest(string RunId, string DataBase64);

public sealed record AppRunResizeApiRequest(string RunId, int Cols, int Rows);

/// <summary>
/// Shared plumbing: every endpoint resolves the project, locates the CALLER's runner (runs
/// execute on the machine of whoever pressed the button — never a colleague's), and forwards
/// through the worker relay. A missing relay target means "runner offline", which the UI
/// renders as its disabled state rather than an error.
/// </summary>
internal static class AppRunRelayCalls
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public const string RunnerOfflineMessage =
        "Your runner is not connected — start it on your machine with `./orkka start`.";

    public static async Task<TResponse?> PostAsync<TResponse>(
        IHttpClientFactory httpClientFactory, RunnerRelayTarget relay, string path,
        object body, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(AppRunRelay.HttpClientName);
        var response = await client.PostAsJsonAsync($"{relay.BaseUrl}{path}", body, Json, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TResponse>(Json, ct);
    }

    /// <summary>For ops whose relay answers with an empty 200 (input, resize).</summary>
    public static async Task PostAsync(
        IHttpClientFactory httpClientFactory, RunnerRelayTarget relay, string path,
        object body, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(AppRunRelay.HttpClientName);
        var response = await client.PostAsJsonAsync($"{relay.BaseUrl}{path}", body, Json, ct);
        response.EnsureSuccessStatusCode();
    }
}

/// <summary>What is already running for this project on the caller's machine — including
/// anything started before this tab existed. Also the "is Run available at all" probe.</summary>
public sealed class AttachAppRunEndpoint(
    IProjectResolver projectResolver,
    ICurrentUser currentUser,
    IRunnerRelayLocator relayLocator,
    IHttpClientFactory httpClientFactory,
    ILogger<AttachAppRunEndpoint> logger)
    : EndpointWithoutRequest<AppRunResponse>
{
    public override void Configure()
    {
        Get("/api/projects/{projectId}/app-run");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (await projectResolver.ResolveAsync(Route<string>("projectId")!, ct) is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var relay = await relayLocator.LocateAsync(currentUser.OrgId, currentUser.UserId, ct);
        if (relay is null)
        {
            await Send.OkAsync(new AppRunResponse(false, []), ct);
            return;
        }

        try
        {
            var result = await AppRunRelayCalls.PostAsync<RelayAppRunResponse>(
                httpClientFactory, relay, "/internal/runner/apprun/attach",
                new RelayAppRunAttachRequest(
                    currentUser.OrgId.ToString(), currentUser.UserId, projectId.ToString()),
                ct);
            await Send.OkAsync(
                new AppRunResponse(result?.Supported ?? false, result?.Snapshots ?? []), ct);
        }
        catch (Exception ex)
        {
            // A read that decides whether to show the panel — degrade to "unavailable".
            logger.LogWarning(ex, "App-run attach relay failed for project {ProjectId}", projectId);
            await Send.OkAsync(new AppRunResponse(false, []), ct);
        }
    }
}

/// <summary>
/// Starts the selected targets on the caller's machine. The command comes from the
/// project's STORED run config, never off the wire — a client can only select target keys,
/// exactly like the Local edition's hub. An empty selection means every auto-select target.
/// </summary>
public sealed class StartAppRunEndpoint(
    IProjectResolver projectResolver,
    IRunConfigDetector runConfig,
    IProjectsApi projectsApi,
    ICurrentUser currentUser,
    IRunnerRelayLocator relayLocator,
    IHttpClientFactory httpClientFactory)
    : Endpoint<StartAppRunApiRequest, AppRunResponse>
{
    public override void Configure()
    {
        Post("/api/projects/{projectId}/app-run/start");
    }

    public override async Task HandleAsync(StartAppRunApiRequest req, CancellationToken ct)
    {
        if (await projectResolver.ResolveAsync(Route<string>("projectId")!, ct) is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var relay = await relayLocator.LocateAsync(currentUser.OrgId, currentUser.UserId, ct);
        if (relay is null)
        {
            await Send.OkAsync(new AppRunResponse(false, [], AppRunRelayCalls.RunnerOfflineMessage), ct);
            return;
        }

        var stored = await runConfig.GetStoredAsync(currentUser.OrgId, projectId, ct);
        // A config the repo's own orkka.json wrote is re-read for free — running yesterday's
        // commands against today's manifest is the mistake the manifest exists to prevent.
        if (stored?.Source == RunConfigSource.Manifest)
            stored = await runConfig.GetAsync(currentUser.OrgId, projectId, ct) ?? stored;
        if (stored is null || stored.Targets.Length == 0)
        {
            ThrowError("Orkka has not worked out how to run this project yet — press Detect first.");
            return;
        }

        var selected = (req.TargetKeys is { Length: > 0 }
            ? stored.Targets.Where(t =>
                req.TargetKeys.Contains(t.Key, StringComparer.OrdinalIgnoreCase))
            : stored.Targets.Where(t => t.AutoSelect)).ToList();
        if (selected.Count == 0)
        {
            ThrowError("Nothing selected to run.");
            return;
        }

        // The stored config can outlive the repo it points at (removed from the project,
        // re-linked with a new id) — a stale target must fail here, not as a confusing
        // "repository not found" from the runner.
        var repos = await projectsApi.ListReposAsync(projectId, ct);
        var validRepos = repos.Select(r => r.Id.ToString()).ToHashSet(StringComparer.Ordinal);
        foreach (var target in selected)
        {
            if (!validRepos.Contains(target.RepoId))
            {
                ThrowError(
                    $"'{target.Label}' points at a repository that is no longer in this project — " +
                    "run Detect again.");
                return;
            }
        }

        var planned = selected.Select(t => new RelayAppRunTarget(
            Ulid.NewUlid().ToString(),
            t.Key,
            t.Label,
            t.Kind,
            t.RepoId,
            t.Command,
            t.WorkingDirectory,
            t.Port,
            t.Url,
            t.DependsOn ?? [],
            t.InstallCommand,
            t.StopCommand,
            t.PreviewPath,
            t.RestartOnMerge)).ToArray();

        var result = await AppRunRelayCalls.PostAsync<RelayAppRunResponse>(
            httpClientFactory, relay, "/internal/runner/apprun/start",
            new RelayAppRunStartRequest(
                currentUser.OrgId.ToString(), currentUser.UserId, projectId.ToString(),
                planned, req.ForceFreePorts),
            ct);

        await Send.OkAsync(new AppRunResponse(
            result?.Supported ?? false, result?.Snapshots ?? [], result?.Error), ct);
    }
}

/// <summary>Stops one target, or everything the caller has running for this project.</summary>
public sealed class StopAppRunEndpoint(
    IProjectResolver projectResolver,
    ICurrentUser currentUser,
    IRunnerRelayLocator relayLocator,
    IHttpClientFactory httpClientFactory)
    : Endpoint<StopAppRunApiRequest, AppRunResponse>
{
    public override void Configure()
    {
        Post("/api/projects/{projectId}/app-run/stop");
    }

    public override async Task HandleAsync(StopAppRunApiRequest req, CancellationToken ct)
    {
        if (await projectResolver.ResolveAsync(Route<string>("projectId")!, ct) is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var relay = await relayLocator.LocateAsync(currentUser.OrgId, currentUser.UserId, ct);
        if (relay is null)
        {
            // Runner gone: its processes died with it — nothing left to stop.
            await Send.OkAsync(new AppRunResponse(false, []), ct);
            return;
        }

        var result = await AppRunRelayCalls.PostAsync<RelayAppRunResponse>(
            httpClientFactory, relay, "/internal/runner/apprun/stop",
            new RelayAppRunStopRequest(
                currentUser.OrgId.ToString(), currentUser.UserId, projectId.ToString(),
                req.RunId),
            ct);
        await Send.OkAsync(
            new AppRunResponse(result?.Supported ?? false, result?.Snapshots ?? []), ct);
    }
}

/// <summary>Keystrokes for a running target — dev servers that reload on `r` are the reason
/// the log pane is a terminal rather than a tail. The worker verifies the run belongs to
/// this caller before writing to it.</summary>
public sealed class AppRunInputEndpoint(
    IProjectResolver projectResolver,
    ICurrentUser currentUser,
    IRunnerRelayLocator relayLocator,
    IHttpClientFactory httpClientFactory)
    : Endpoint<AppRunInputApiRequest>
{
    public override void Configure()
    {
        Post("/api/projects/{projectId}/app-run/input");
    }

    public override async Task HandleAsync(AppRunInputApiRequest req, CancellationToken ct)
    {
        if (await projectResolver.ResolveAsync(Route<string>("projectId")!, ct) is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var relay = await relayLocator.LocateAsync(currentUser.OrgId, currentUser.UserId, ct);
        if (relay is not null)
        {
            await AppRunRelayCalls.PostAsync(
                httpClientFactory, relay, "/internal/runner/apprun/input",
                new RelayAppRunInputRequest(
                    currentUser.OrgId.ToString(), currentUser.UserId,
                    req.RunId, req.DataBase64),
                ct);
        }
        await Send.NoContentAsync(ct);
    }
}

public sealed class AppRunResizeEndpoint(
    IProjectResolver projectResolver,
    ICurrentUser currentUser,
    IRunnerRelayLocator relayLocator,
    IHttpClientFactory httpClientFactory)
    : Endpoint<AppRunResizeApiRequest>
{
    public override void Configure()
    {
        Post("/api/projects/{projectId}/app-run/resize");
    }

    public override async Task HandleAsync(AppRunResizeApiRequest req, CancellationToken ct)
    {
        if (await projectResolver.ResolveAsync(Route<string>("projectId")!, ct) is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var relay = await relayLocator.LocateAsync(currentUser.OrgId, currentUser.UserId, ct);
        if (relay is not null)
        {
            await AppRunRelayCalls.PostAsync(
                httpClientFactory, relay, "/internal/runner/apprun/resize",
                new RelayAppRunResizeRequest(
                    currentUser.OrgId.ToString(), currentUser.UserId,
                    req.RunId, req.Cols, req.Rows),
                ct);
        }
        await Send.NoContentAsync(ct);
    }
}
