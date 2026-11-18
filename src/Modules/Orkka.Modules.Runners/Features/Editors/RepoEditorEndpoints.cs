namespace Orkka.Modules.Runners.Features.Editors;

using System.Net.Http.Json;
using System.Text.Json;
using FastEndpoints;
using Microsoft.Extensions.Logging;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Runners.Contracts;
using Orkka.SharedKernel.Security;

public sealed record OpenRepoInEditorRequest(string? Editor = null);

public sealed record OpenRepoInEditorResponse(string OpenedWith);

// Wire shapes mirroring the Worker's relay endpoints (same contract the task-scoped
// endpoints use; RepoToken is the additive field that selects the repo itself).
file sealed record RelayEditorsRequest(string OrgId, string UserId);

file sealed record RelayEditorsResponse(bool Supported, RelayEditorOption[] Editors);

file sealed record RelayEditorOption(string Key, string DisplayName);

file sealed record RelayOpenInEditorRequest(
    string OrgId, string UserId, string WorktreeToken, string? Editor, string? RepoToken);

file sealed record RelayOpenInEditorResponse(string? OpenedWith, string? Error);

/// <summary>
/// The repository itself, on its default branch, in a desktop editor on the CALLER's machine.
/// Where a task's worktree is "finish what the agent started", this is "just give me the
/// code": the runner opens the user's working folder (Orkka Local) or its persistent main
/// checkout of the hosted repo — the same folder the Run button starts the app in, so a
/// developer can add the .env, install dependencies, or poke at what shipped.
///
/// Always the caller's own runner: unlike a task, a repo has no "machine that ran it", and
/// nobody may launch a process on a colleague's desktop.
/// </summary>
internal static class RepoEditorAccess
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Null when the repo is not the caller's to open (unknown / other org /
    /// other project) or the caller has no online runner to open it on.</summary>
    public static async Task<(RepoSummary Repo, RunnerRelayTarget Relay)?> ResolveAsync(
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

/// <summary>Editors installed on the caller's machine — for the button/picker on the repo
/// card. Supported=false hides it (no runner, runner too old, relay unavailable).</summary>
public sealed class ListRepoEditorsEndpoint(
    IProjectResolver projectResolver,
    IProjectsApi projectsApi,
    ICurrentUser currentUser,
    IRunnerRelayLocator relayLocator,
    IHttpClientFactory httpClientFactory,
    ILogger<ListRepoEditorsEndpoint> logger)
    : EndpointWithoutRequest<TaskEditorsResponse>
{
    public override void Configure()
    {
        Get("/api/projects/{projectId}/repos/{repoId:ulid}/editors");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var resolved = await RepoEditorAccess.ResolveAsync(
            Route<string>("projectId")!, Ulid.Parse(Route<string>("repoId")),
            projectResolver, projectsApi, currentUser, relayLocator, ct);

        if (resolved is not { } target)
        {
            await Send.OkAsync(new TaskEditorsResponse(false, []), ct);
            return;
        }

        try
        {
            var client = httpClientFactory.CreateClient(RunnerRelay.HttpClientName);
            var response = await client.PostAsJsonAsync(
                $"{target.Relay.BaseUrl}/internal/runner/editors",
                new RelayEditorsRequest(currentUser.OrgId.ToString(), target.Relay.UserId),
                RepoEditorAccess.Json, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<RelayEditorsResponse>(
                RepoEditorAccess.Json, ct);

            await Send.OkAsync(new TaskEditorsResponse(
                result?.Supported ?? false,
                [.. (result?.Editors ?? []).Select(e => new EditorOption(e.Key, e.DisplayName))]),
                ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Editor list relay failed for repo {RepoId}", target.Repo.Id);
            await Send.OkAsync(new TaskEditorsResponse(false, []), ct);
        }
    }
}

public sealed class OpenRepoInEditorEndpoint(
    IProjectResolver projectResolver,
    IProjectsApi projectsApi,
    ICurrentUser currentUser,
    IRunnerRelayLocator relayLocator,
    IHttpClientFactory httpClientFactory)
    : Endpoint<OpenRepoInEditorRequest, OpenRepoInEditorResponse>
{
    public override void Configure()
    {
        Post("/api/projects/{projectId}/repos/{repoId:ulid}/open-in-editor");
    }

    public override async Task HandleAsync(OpenRepoInEditorRequest req, CancellationToken ct)
    {
        var resolved = await RepoEditorAccess.ResolveAsync(
            Route<string>("projectId")!, Ulid.Parse(Route<string>("repoId")),
            projectResolver, projectsApi, currentUser, relayLocator, ct);

        if (resolved is not { } target)
        {
            ThrowError(
                "Your runner is not connected — start it on your machine with `./orkka start` " +
                "to open repositories in your editor.", 422);
            return;
        }

        if (req.Editor is { Length: > 0 } requested
            && !RunnerEditors.All.Contains(requested, StringComparer.OrdinalIgnoreCase))
        {
            ThrowError($"Unknown editor '{requested}'.", 400);
            return;
        }

        var repoToken = RunnerTokens.UserRepo(
            currentUser.OrgId.ToString(), target.Repo.Id.ToString());

        // The relay path is shared with the task flow; a stale worker without RepoToken
        // support opens nothing and reports the token error, which we surface as-is.
        var client = httpClientFactory.CreateClient(RunnerRelay.HttpClientName);
        var response = await client.PostAsJsonAsync(
            $"{target.Relay.BaseUrl}/internal/runner/open-in-editor",
            new RelayOpenInEditorRequest(
                currentUser.OrgId.ToString(), target.Relay.UserId,
                WorktreeToken: "", req.Editor, repoToken),
            RepoEditorAccess.Json, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<RelayOpenInEditorResponse>(
            RepoEditorAccess.Json, ct);

        if (result?.Error is { } error)
        {
            ThrowError(error, 422);
            return;
        }

        await Send.OkAsync(new OpenRepoInEditorResponse(result?.OpenedWith ?? "your editor"), ct);
    }
}
