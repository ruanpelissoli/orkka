namespace Orkka.Runners.Server.Hosting;

using System.Text.Json;
using Orkka.LocalRunner.Contracts;
using Orkka.Runners.Server;

/// <summary>One read of a local-folder repository, relayed from the API host. <c>Op</c> is
/// one of <see cref="RepoReadRelay.Ops"/>; the other fields are what that op needs.</summary>
public sealed record RelayRepoReadRequest(
    string OrgId, string UserId, string RepoId, string Op,
    string? Branch = null, string? Path = null, string? Sha = null,
    int Skip = 0, int Take = 50, int MaxChars = 200_000);

/// <summary>
/// The read half of the local-folder relay: the API host's Repo page (tree, file, commits,
/// branches, a commit's diff) for a repository that is a folder on the user's machine. Each
/// op is the runner's own browse op; the runner's result record travels back verbatim as
/// JSON in <see cref="RelayOpResponse.Value"/>, so the API side deserialises the same wire
/// types the runner produced.
///
/// Kept apart from the endpoint so the op switch is testable against a fake link.
/// </summary>
public static class RepoReadRelay
{
    public static class Ops
    {
        public const string Tree = "tree";
        public const string FileTree = "file-tree";
        public const string File = "file";
        public const string Commits = "commits";
        public const string Branches = "branches";
        public const string CommitDiff = "commit-diff";
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<RelayOpResponse> ExecuteAsync(
        IRunnerLink link, RelayRepoReadRequest req, CancellationToken ct)
    {
        var (connected, hello) = link.Status;
        if (!connected || hello?.Capabilities?.Contains(RunnerCapabilities.LocalFolders) is not true)
            return new RelayOpResponse(false, false, null, "Runner offline.");

        var branch = string.IsNullOrEmpty(req.Branch) ? "main" : req.Branch;
        try
        {
            switch (req.Op)
            {
                case Ops.Tree:
                {
                    var result = await link.InvokeAsync<RunnerTreeResult>(
                        RunnerProtocol.ListTree,
                        [new ListTreeRequest(req.OrgId, req.RepoId, branch, req.Path)], ct);
                    return Answer(result.Success, result, result.Error);
                }
                case Ops.FileTree:
                {
                    var result = await link.InvokeAsync<RunnerFileTreeResult>(
                        RunnerProtocol.GetFileTree,
                        [new GetFileTreeRequest(req.OrgId, req.RepoId, branch)], ct);
                    return Answer(result.Success, result, result.Error);
                }
                case Ops.File:
                {
                    if (string.IsNullOrEmpty(req.Path))
                        return new RelayOpResponse(true, false, null, "A file path is required.");
                    var result = await link.InvokeAsync<RunnerOpResult>(
                        RunnerProtocol.GetFileContent,
                        [new GetFileContentRequest(req.OrgId, req.RepoId, branch, req.Path)], ct);
                    return Answer(result.Success, result, result.Error);
                }
                case Ops.Commits:
                {
                    var result = await link.InvokeAsync<RunnerCommitLogResult>(
                        RunnerProtocol.GetCommitLog,
                        [new GetCommitLogRequest(req.OrgId, req.RepoId, branch, req.Skip, req.Take, req.Path)], ct);
                    return Answer(result.Success, result, result.Error);
                }
                case Ops.Branches:
                {
                    var result = await link.InvokeAsync<RunnerBranchListResult>(
                        RunnerProtocol.ListBranches,
                        [new ListBranchesRequest(req.OrgId, req.RepoId)], ct);
                    return Answer(result.Success, result, result.Error);
                }
                case Ops.CommitDiff:
                {
                    if (string.IsNullOrEmpty(req.Sha))
                        return new RelayOpResponse(true, false, null, "A commit sha is required.");
                    var result = await link.InvokeAsync<RunnerOpResult>(
                        RunnerProtocol.GetCommitDiff,
                        [new GetCommitDiffRequest(req.OrgId, req.RepoId, req.Sha, req.MaxChars)], ct);
                    return Answer(result.Success, result, result.Error);
                }
                default:
                    return new RelayOpResponse(true, false, null, $"Unknown repository read '{req.Op}'.");
            }
        }
        catch (RunnerUnavailableException ex)
        {
            return new RelayOpResponse(false, false, null, ex.Message);
        }
    }

    private static RelayOpResponse Answer<T>(bool success, T result, string? error)
        => new(true, success, success ? JsonSerializer.Serialize(result, Json) : null, error);
}
