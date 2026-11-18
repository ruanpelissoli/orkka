namespace Orkka.LocalRunner;

using Microsoft.Extensions.Logging.Abstractions;
using Orkka.Git;
using Orkka.LocalRunner.Contracts;

/// <summary>
/// Whether THIS machine can reach a set of remotes with the credentials a real clone would use:
/// the developer's own — their credential manager or <c>gh</c> login for https, their ssh keys
/// for ssh. What fails here is what a clone would fail on: no sign-in for that host, a key the
/// forge does not know, the remote unreachable from this network, git missing.
/// </summary>
public static class RepoAccessProbing
{
    /// <summary>A preflight runs while someone waits on a page, once per repo, so it gets a
    /// tighter budget than a real clone. A refusal is immediate; anything approaching this is a
    /// network problem, and saying so beats hanging.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Bounded so a large project cannot turn one click into a minutes-long scan.</summary>
    public const int MaxRepos = 25;

    /// <summary>The engine the probe runs git through; the runner sets its own.</summary>
    public static GitProcess Git { get; set; } = new(NullLogger<GitProcess>.Instance);

    /// <summary>
    /// Probes each remote in turn. <c>ls-remote</c> rather than <c>clone</c>: it performs the
    /// same authentication handshake, transfers no objects, and costs a fraction of a second.
    /// Note the absence of <c>--exit-code</c>, which reports "no matching refs" (2) for a
    /// legitimately empty repository — an empty repo is reachable, which is what this asks.
    /// </summary>
    public static async Task<CheckRepoAccessResponse> ProbeAsync(
        IReadOnlyList<RepoAccessProbe> repos, TimeSpan? timeout = null)
    {
        var results = new List<RepoAccessResult>(repos.Count);

        foreach (var repo in repos.Take(MaxRepos))
        {
            if (string.IsNullOrWhiteSpace(repo.GitUrl))
            {
                // A local-folder repo has no remote to reach, so there is no credential
                // question to answer — reporting it as broken would be inventing a problem.
                results.Add(new RepoAccessResult(repo.RepoId, true, null));
                continue;
            }

            var cleanUrl = GitUrls.Clean(repo.GitUrl);

            var result = await Git.RunAsync(new GitCommand(
                // Working directory is irrelevant to ls-remote against a URL, but it must be a
                // directory that exists — the runner's own cwd may have been deleted.
                Path.GetTempPath(), ["ls-remote", cleanUrl],
                Timeout: timeout ?? DefaultTimeout),
                CancellationToken.None);

            results.Add(new RepoAccessResult(repo.RepoId, result.Succeeded,
                result.Succeeded ? null : result.Describe()));
        }

        return new CheckRepoAccessResponse([.. results]);
    }
}
