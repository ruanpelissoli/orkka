namespace Orkka.Modules.Runners.PublicApi;

using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Runners.Contracts;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.SharedKernel.Utilities;

/// <summary>
/// The hosted <see cref="ILocalRepoReader"/>: the Repo page's reads of a folder on the user's
/// machine, relayed API → the Worker pod holding that user's runner socket → the runner. The
/// Worker's <c>/internal/runner/repo/read</c> answers with the runner's own result record as
/// JSON, deserialised here into the same wire types.
///
/// <see cref="CanReach"/> is "a relay is configured for this deployment": the Local edition
/// composes no relay and keeps its in-process reader, which already is the runner.
/// </summary>
public sealed class RelayLocalRepoReader(
    IRunnerRelayLocator relayLocator,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<RelayLocalRepoReader> logger) : ILocalRepoReader
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public const string NoRunner =
        "This repository is a folder on your machine, and your runner is not connected — "
        + "start it with `./orkka start` and try again.";

    private sealed record RelayOpResponse(bool Supported, bool Success, string? Value, string? Error);

    public bool CanReach => !string.IsNullOrWhiteSpace(configuration["Runners:RelayUrlTemplate"]);

    public async Task<Result> EnsureMirrorAsync(
        Ulid orgId, Ulid repoId, string runnerUserId, string folderPath, CancellationToken ct = default)
    {
        var response = await PostAsync("/internal/runner/repo/ensure-clone", orgId, runnerUserId, new
        {
            OrgId = orgId.ToString(),
            UserId = runnerUserId,
            RepoId = repoId.ToString(),
            CloneUrlOrPath = folderPath,
        }, ct);

        return response is { Supported: true, Success: true }
            ? Result.Success()
            : Result.Failure(response?.Error ?? NoRunner);
    }

    public async Task<Result<List<RepoTreeEntry>>> ListTreeAsync(
        Ulid orgId, Ulid repoId, string runnerUserId, string branch, string? dirPath, CancellationToken ct = default)
    {
        var read = await ReadAsync<RunnerTreeResult>(orgId, repoId, runnerUserId, "tree", branch, path: dirPath, ct: ct);
        return read.IsFailure
            ? Result<List<RepoTreeEntry>>.Failure(read.Error!)
            : Result<List<RepoTreeEntry>>.Success((read.Value!.Entries ?? [])
                .Select(e => new RepoTreeEntry(e.Name, e.Path, e.IsDirectory)).ToList());
    }

    public async Task<Result<List<FileTreeNode>>> GetFileTreeAsync(
        Ulid orgId, Ulid repoId, string runnerUserId, string branch, CancellationToken ct = default)
    {
        var read = await ReadAsync<RunnerFileTreeResult>(orgId, repoId, runnerUserId, "file-tree", branch, ct: ct);
        return read.IsFailure
            ? Result<List<FileTreeNode>>.Failure(read.Error!)
            : Result<List<FileTreeNode>>.Success((read.Value!.Paths ?? []).Select(p => new FileTreeNode(p)).ToList());
    }

    public async Task<Result<string>> GetFileContentAsync(
        Ulid orgId, Ulid repoId, string runnerUserId, string branch, string filePath, CancellationToken ct = default)
    {
        var read = await ReadAsync<RunnerOpResult>(orgId, repoId, runnerUserId, "file", branch, path: filePath, ct: ct);
        return read.IsFailure
            ? Result<string>.Failure(read.Error!)
            : Result<string>.Success(read.Value!.Value ?? "");
    }

    public async Task<Result<List<GitCommitInfo>>> GetCommitLogAsync(
        Ulid orgId, Ulid repoId, string runnerUserId, string branch, int skip, int take, string? filePath,
        CancellationToken ct = default)
    {
        var read = await ReadAsync<RunnerCommitLogResult>(
            orgId, repoId, runnerUserId, "commits", branch, path: filePath, skip: skip, take: take, ct: ct);
        return read.IsFailure
            ? Result<List<GitCommitInfo>>.Failure(read.Error!)
            : Result<List<GitCommitInfo>>.Success((read.Value!.Commits ?? [])
                .Select(c => new GitCommitInfo(c.Sha, c.Author, c.Date, c.Subject)).ToList());
    }

    public async Task<Result<List<GitBranchInfo>>> ListBranchesAsync(
        Ulid orgId, Ulid repoId, string runnerUserId, CancellationToken ct = default)
    {
        var read = await ReadAsync<RunnerBranchListResult>(orgId, repoId, runnerUserId, "branches", null, ct: ct);
        return read.IsFailure
            ? Result<List<GitBranchInfo>>.Failure(read.Error!)
            : Result<List<GitBranchInfo>>.Success((read.Value!.Branches ?? [])
                .Select(b => new GitBranchInfo(b.Name, b.Sha, b.LastCommitSubject, b.LastCommitDate, b.IsRemote)).ToList());
    }

    public async Task<Result<string>> GetCommitDiffAsync(
        Ulid orgId, Ulid repoId, string runnerUserId, string sha, int maxChars, CancellationToken ct = default)
    {
        var read = await ReadAsync<RunnerOpResult>(
            orgId, repoId, runnerUserId, "commit-diff", null, sha: sha, maxChars: maxChars, ct: ct);
        return read.IsFailure
            ? Result<string>.Failure(read.Error!)
            : Result<string>.Success(read.Value!.Value ?? "");
    }

    private async Task<Result<T>> ReadAsync<T>(
        Ulid orgId, Ulid repoId, string runnerUserId, string op, string? branch,
        string? path = null, string? sha = null, int skip = 0, int take = 50, int maxChars = 200_000,
        CancellationToken ct = default)
    {
        var response = await PostAsync("/internal/runner/repo/read", orgId, runnerUserId, new
        {
            OrgId = orgId.ToString(),
            UserId = runnerUserId,
            RepoId = repoId.ToString(),
            Op = op,
            Branch = branch,
            Path = path,
            Sha = sha,
            Skip = skip,
            Take = take,
            MaxChars = maxChars,
        }, ct);

        if (response is not { Supported: true })
            return Result<T>.Failure(NoRunner);
        if (!response.Success)
            return Result<T>.Failure(response.Error ?? "The runner could not read the repository.");

        try
        {
            var value = string.IsNullOrEmpty(response.Value)
                ? default
                : JsonSerializer.Deserialize<T>(response.Value, Json);
            return value is null
                ? Result<T>.Failure("The runner returned an empty answer.")
                : Result<T>.Success(value);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Unreadable relay answer for {Op} on local repo {RepoId}", op, repoId);
            return Result<T>.Failure("The runner's answer could not be read.");
        }
    }

    /// <summary>Null when there is no machine to ask — offline, or no relay.</summary>
    private async Task<RelayOpResponse?> PostAsync<TBody>(
        string route, Ulid orgId, string runnerUserId, TBody body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(runnerUserId))
            return null;

        try
        {
            var relay = await relayLocator.LocateAsync(orgId, runnerUserId, ct);
            if (relay is null)
                return null;

            var client = httpClientFactory.CreateClient(RunnerRelay.HttpClientName);
            var http = await client.PostAsJsonAsync($"{relay.BaseUrl}{route}", body, Json, ct);
            http.EnsureSuccessStatusCode();
            return await http.Content.ReadFromJsonAsync<RelayOpResponse>(Json, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "Local-repo read relay {Route} failed for org {OrgId}", route, orgId);
            return null;
        }
    }
}
