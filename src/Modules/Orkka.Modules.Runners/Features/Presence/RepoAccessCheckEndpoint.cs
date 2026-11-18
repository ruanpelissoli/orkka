namespace Orkka.Modules.Runners.Features.Presence;

using System.Net.Http.Json;
using System.Text.Json;
using FastEndpoints;
using Microsoft.Extensions.Logging;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Runners.Contracts;
using Orkka.SharedKernel.Security;

/// <param name="Ok">True when git on the caller's machine could read this remote. False is
/// the interesting case and always carries <paramref name="Reason"/>.</param>
/// <param name="Advice">What to do about it, already worded for a human — the same sentence
/// a chat turn or a task would have shown after failing, only before it fails.</param>
public sealed record RepoAccessStatus(
    string RepoId, string Name, bool Ok, string? Reason, string? Advice);

/// <summary>Supported=false means no machine could answer — the caller's runner is offline,
/// or too old for the check. The UI hides the check rather than reporting a failure the user
/// cannot act on.</summary>
public sealed record RepoAccessCheckResponse(
    bool Supported, RepoAccessStatus[] Repos, string? Error = null);

/// <summary>
/// "Can my machine actually read this project's code?", asked before anything runs.
///
/// The precondition nobody states: an unmanaged repo is cloned on the CUSTOMER's machine
/// under the CUSTOMER's git credentials — no platform token rides along, which is the whole
/// of the code-stays-local route. A machine without a working credential for the forge
/// therefore fails every task and every chat turn touching that repo, identically and
/// permanently, and before this the only way to find out was to burn a run.
///
/// Note what this deliberately does NOT do: it never falls back to cloud. The question is
/// specifically what THIS user's machine can reach, so an offline runner is "cannot answer",
/// never "fine".
/// </summary>
public sealed class RepoAccessCheckEndpoint(
    IProjectResolver projectResolver,
    IProjectsApi projectsApi,
    ICurrentUser currentUser,
    IRunnerRelayLocator relayLocator,
    IHttpClientFactory httpClientFactory,
    ILogger<RepoAccessCheckEndpoint> logger)
    : EndpointWithoutRequest<RepoAccessCheckResponse>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public override void Configure()
    {
        // A read of the caller's own machine, so it needs no permission beyond project
        // visibility — which ResolveAsync already enforces.
        Get("/api/projects/{projectId}/repos/access-check");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var projectIdOrSlug = Route<string>("projectId")!;
        if (await projectResolver.ResolveAsync(projectIdOrSlug, ct) is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var repos = await projectsApi.ListReposAsync(projectId, ct);
        if (repos.Count == 0)
        {
            await Send.OkAsync(new RepoAccessCheckResponse(true, []), ct);
            return;
        }

        var relay = await relayLocator.LocateAsync(currentUser.OrgId, currentUser.UserId, ct);
        if (relay is null)
        {
            await Send.OkAsync(new RepoAccessCheckResponse(false, []), ct);
            return;
        }

        RelayRepoAccessResponse? result;
        try
        {
            var client = httpClientFactory.CreateClient(RunnerRelay.HttpClientName);
            var response = await client.PostAsJsonAsync(
                $"{relay.BaseUrl}/internal/runner/repo-access",
                new RelayRepoAccessRequest(
                    currentUser.OrgId.ToString(), relay.UserId,
                    [.. repos.Select(r => new RelayRepoAccessProbe(r.Id.ToString(), r.GitUrl))]),
                Json, ct);
            response.EnsureSuccessStatusCode();
            result = await response.Content.ReadFromJsonAsync<RelayRepoAccessResponse>(Json, ct);
        }
        catch (Exception ex)
        {
            // A relay hiccup reads to the caller exactly like an offline runner, and neither
            // is worth a 500 on a page that is only offering to check something.
            logger.LogWarning(ex, "Repo access check relay failed for org {OrgId}", currentUser.OrgId);
            await Send.OkAsync(new RepoAccessCheckResponse(false, []), ct);
            return;
        }

        if (result is not { Supported: true })
        {
            await Send.OkAsync(new RepoAccessCheckResponse(false, []), ct);
            return;
        }

        var byId = result.Results.ToDictionary(r => r.RepoId, StringComparer.Ordinal);
        var statuses = repos.Select(repo =>
        {
            // A repo the runner did not answer for is not evidence of anything — it may have
            // been past the probe cap. Reported as fine rather than invented as broken.
            if (!byId.TryGetValue(repo.Id.ToString(), out var probed) || probed.Ok)
                return new RepoAccessStatus(repo.Id.ToString(), repo.Name, true, null, null);

            // Same classifier and the same sentences a failed run would have produced — the
            // only difference is that nothing has been spent yet. onUserMachine is always
            // true here: the whole question is about the caller's own machine.
            var reason = RepoAccessDiagnostics.Classify(probed.Error);
            return new RepoAccessStatus(
                repo.Id.ToString(), repo.Name, false,
                reason.ToString(),
                RepoAccessDiagnostics.Explain(reason, onUserMachine: true));
        }).ToArray();

        await Send.OkAsync(new RepoAccessCheckResponse(true, statuses, result.Error), ct);
    }
}

// Wire shapes mirroring the Worker's RunnerRelayEndpoints. File-scoped, as the other relay
// callers do it: the two processes deploy together, and the duplication is what keeps the
// API host from taking a reference on the Worker.
file sealed record RelayRepoAccessRequest(
    string OrgId, string UserId, RelayRepoAccessProbe[] Repos);

file sealed record RelayRepoAccessProbe(string RepoId, string GitUrl);

file sealed record RelayRepoAccessResponse(
    bool Supported, RelayRepoAccessResult[] Results, string? Error = null);

file sealed record RelayRepoAccessResult(string RepoId, bool Ok, string? Error);
