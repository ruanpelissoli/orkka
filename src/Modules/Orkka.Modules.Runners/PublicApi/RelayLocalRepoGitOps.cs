namespace Orkka.Modules.Runners.PublicApi;

using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Orkka.Modules.Runners.Contracts;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.SharedKernel.Utilities;

/// <summary>
/// Reaches a folder on a user's machine from a hosted process: API or Worker → the Worker pod
/// holding that user's runner socket → the runner → their git.
///
/// The same route every other local-folder feature takes (see the fs/* and repo/* relay
/// endpoints); these three are what a synthetic pull request needs — merge it, read its diff,
/// ask whether it would merge cleanly. Note that even the Worker goes through the relay rather
/// than its own link provider: the pod running the orchestrator is not necessarily the pod
/// holding that user's socket, and the locator is what knows which one is.
///
/// Nothing here throws. A machine that is asleep is the ordinary case, not a fault, and every
/// caller has a sensible answer for "cannot reach it right now".
/// </summary>
internal sealed class RelayLocalRepoGitOps(
    IRunnerRelayLocator relayLocator,
    IHttpClientFactory httpClientFactory,
    ILogger<RelayLocalRepoGitOps> logger) : ILocalRepoGitOps
{
    /// <summary>Its own HTTP client, not the 30-second editor-launch one. A squash merge is
    /// a clone, a merge and a push on the user's laptop; one that ran past 30 s while the
    /// machine was busy installing the app's dependencies came back as "runner not
    /// connected" and parked the task for a cycle (prod, 2026-09-16, StoreFront #15).</summary>
    public const string HttpClientName = "runner-relay-local-git";

    /// <summary>The relay hop's ceiling for one git operation on the user's machine — the
    /// runner link's own per-op budget, so the hop never gives up before the runner would.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromMinutes(4);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string NoRunner =
        "This repository is worked on your machine, and your runner is not connected — "
        + "start it with `./orkka start` and try again.";

    private sealed record RelayOpResponse(bool Supported, bool Success, string? Value, string? Error);

    public async Task<Result> SquashMergeAsync(
        Ulid orgId, Ulid repoId, string? runnerUserId,
        string headBranch, string baseBranch, string commitMessage, CancellationToken ct = default)
    {
        var response = await PostAsync("/internal/runner/pr/merge", orgId, runnerUserId, new
        {
            OrgId = orgId.ToString(),
            UserId = runnerUserId ?? "",
            RepoId = repoId.ToString(),
            HeadBranch = headBranch,
            BaseBranch = baseBranch,
            CommitMessage = commitMessage,
        }, ct);

        if (response is null)
            return Result.Failure(NoRunner);

        // Supported=false is "no machine can answer right now"; Success=false carries the
        // runner's own words, which are written for the user (uncommitted changes, and so on)
        // and are what the merge page shows them.
        return response is { Supported: true, Success: true }
            ? Result.Success()
            : Result.Failure(response.Error ?? NoRunner);
    }

    public Task<Result<string>> GetDiffAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string? runnerUserId,
        string baseBranch, string headBranch, CancellationToken ct = default)
        => GetDiffAsync(orgId, repoId, taskId, runnerUserId, baseBranch, headBranch, null, ct);

    public async Task<Result<string>> GetDiffAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string? runnerUserId,
        string baseBranch, string headBranch, string? baseSha, CancellationToken ct = default)
    {
        var response = await PostAsync("/internal/runner/pr/diff", orgId, runnerUserId, new
        {
            OrgId = orgId.ToString(),
            UserId = runnerUserId ?? "",
            RepoId = repoId.ToString(),
            TaskId = taskId.ToString(),
            BaseBranch = baseBranch,
            HeadBranch = headBranch,
            BaseSha = baseSha,
        }, ct);

        return response is { Supported: true, Success: true }
            ? Result<string>.Success(response.Value ?? "")
            : Result<string>.Failure(response?.Error ?? NoRunner);
    }

    public Task<Result<string>> GetWorkingTreeDiffAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string? runnerUserId,
        string baseBranch, CancellationToken ct = default)
        => GetWorkingTreeDiffAsync(orgId, repoId, taskId, runnerUserId, baseBranch, null, ct);

    public async Task<Result<string>> GetWorkingTreeDiffAsync(
        Ulid orgId, Ulid repoId, Ulid taskId, string? runnerUserId,
        string baseBranch, string? baseSha, CancellationToken ct = default)
    {
        var response = await PostAsync("/internal/runner/pr/working-diff", orgId, runnerUserId, new
        {
            OrgId = orgId.ToString(),
            UserId = runnerUserId ?? "",
            RepoId = repoId.ToString(),
            TaskId = taskId.ToString(),
            BaseBranch = baseBranch,
            HeadBranch = "",
            BaseSha = baseSha,
        }, ct);

        return response is { Supported: true, Success: true }
            ? Result<string>.Success(response.Value ?? "")
            : Result<string>.Failure(response?.Error ?? NoRunner);
    }

    public async Task<bool?> WouldMergeCleanlyAsync(
        Ulid orgId, Ulid repoId, string? runnerUserId,
        string headBranch, string baseBranch, CancellationToken ct = default)
    {
        var response = await PostAsync("/internal/runner/pr/conflicts", orgId, runnerUserId, new
        {
            OrgId = orgId.ToString(),
            UserId = runnerUserId ?? "",
            RepoId = repoId.ToString(),
            HeadBranch = headBranch,
            BaseBranch = baseBranch,
        }, ct);

        if (response is not { Supported: true, Success: true } || string.IsNullOrEmpty(response.Value))
            return null;

        try
        {
            var report = JsonSerializer.Deserialize<MergeConflictReport>(
                response.Value, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return report is null ? null : !report.HasConflicts;
        }
        catch (JsonException ex)
        {
            logger.LogDebug(ex, "Unreadable conflict report for local repo {RepoId}", repoId);
            return null;
        }
    }

    public async Task<Result<string>> OpenPullRequestAsync(
        Ulid orgId, Ulid repoId, string? runnerUserId,
        string headBranch, string baseBranch, string title, string body,
        ForgeKind forge, string? cliAccount, CancellationToken ct = default)
    {
        var response = await PostAsync("/internal/runner/pr/publish", orgId, runnerUserId, new
        {
            OrgId = orgId.ToString(),
            UserId = runnerUserId ?? "",
            RepoId = repoId.ToString(),
            HeadBranch = headBranch,
            BaseBranch = baseBranch,
            Title = title,
            Body = body,
            Forge = forge.ToString(),
            CliAccount = cliAccount,
        }, ct);

        return response is { Supported: true, Success: true } && !string.IsNullOrWhiteSpace(response.Value)
            ? Result<string>.Success(response.Value.Trim())
            : Result<string>.Failure(response?.Error ?? NoRunner);
    }

    public async Task<Result<string>> OpenDraftPullRequestAsync(
        Ulid orgId, Ulid repoId, string? runnerUserId,
        string headBranch, string baseBranch, string title, string body,
        ForgeKind forge, string? cliAccount, CancellationToken ct = default)
    {
        var response = await PostAsync("/internal/runner/pr/publish", orgId, runnerUserId, new
        {
            OrgId = orgId.ToString(),
            UserId = runnerUserId ?? "",
            RepoId = repoId.ToString(),
            HeadBranch = headBranch,
            BaseBranch = baseBranch,
            Title = title,
            Body = body,
            Forge = forge.ToString(),
            CliAccount = cliAccount,
            Draft = true,
        }, ct);

        return response is { Supported: true, Success: true } && !string.IsNullOrWhiteSpace(response.Value)
            ? Result<string>.Success(response.Value.Trim())
            : Result<string>.Failure(response?.Error ?? NoRunner);
    }

    public async Task<Result> MarkPullRequestReadyAsync(
        Ulid orgId, Ulid repoId, string? runnerUserId,
        string pullRequestUrl, bool undo, ForgeKind forge, string? cliAccount, CancellationToken ct = default)
    {
        var response = await PostAsync("/internal/runner/pr/ready", orgId, runnerUserId, new
        {
            OrgId = orgId.ToString(),
            UserId = runnerUserId ?? "",
            RepoId = repoId.ToString(),
            PullRequestUrl = pullRequestUrl,
            Forge = forge.ToString(),
            CliAccount = cliAccount,
            Undo = undo,
        }, ct);
        return response is { Supported: true, Success: true } ? Result.Success() : Result.Failure(response?.Error ?? NoRunner);
    }

    public async Task<Result> UpdatePullRequestBodyAsync(
        Ulid orgId, Ulid repoId, string? runnerUserId,
        string pullRequestUrl, string body, ForgeKind forge, string? cliAccount, CancellationToken ct = default)
    {
        var response = await PostAsync("/internal/runner/pr/body", orgId, runnerUserId, new
        {
            OrgId = orgId.ToString(),
            UserId = runnerUserId ?? "",
            RepoId = repoId.ToString(),
            PullRequestUrl = pullRequestUrl,
            Forge = forge.ToString(),
            CliAccount = cliAccount,
            Body = body,
        }, ct);
        return response is { Supported: true, Success: true } ? Result.Success() : Result.Failure(response?.Error ?? NoRunner);
    }

    public async Task<string?> GetPullRequestStateAsync(
        Ulid orgId, Ulid repoId, string? runnerUserId,
        string pullRequestUrl, ForgeKind forge, string? cliAccount, CancellationToken ct = default)
    {
        var response = await PostAsync("/internal/runner/pr/state", orgId, runnerUserId, new
        {
            OrgId = orgId.ToString(),
            UserId = runnerUserId ?? "",
            RepoId = repoId.ToString(),
            PullRequestUrl = pullRequestUrl,
            Forge = forge.ToString(),
            CliAccount = cliAccount,
        }, ct);

        return response is { Supported: true, Success: true } ? response.Value?.Trim() : null;
    }

    /// <summary>Null when there is no machine to ask — offline, or no user to ask for.</summary>
    private async Task<RelayOpResponse?> PostAsync<TBody>(
        string route, Ulid orgId, string? runnerUserId, TBody body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(runnerUserId))
            return null;

        try
        {
            var relay = await relayLocator.LocateAsync(orgId, runnerUserId, ct);
            if (relay is null)
                return null;

            var client = httpClientFactory.CreateClient(HttpClientName);
            var http = await client.PostAsJsonAsync($"{relay.BaseUrl}{route}", body, Json, ct);
            http.EnsureSuccessStatusCode();
            return await http.Content.ReadFromJsonAsync<RelayOpResponse>(Json, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex,
                "Local-repo relay call {Route} failed for org {OrgId}", route, orgId);
            return null;
        }
    }
}

/// <summary>The runner's conflict report, of which only the verdict is needed here.</summary>
internal sealed record MergeConflictReport(bool HasConflicts);
