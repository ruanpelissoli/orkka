namespace Orkka.Runners.Server.Hosting;

using System.Collections.Concurrent;
using System.Diagnostics;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Utilities;
using Orkka.Runners.Server;

/// <summary>
/// Warms a freshly connected runner: clones (or fetches) the bare copy of every remote repo in
/// the org's projects onto the user's machine, in the background, right after Hello.
///
/// Nothing clones a repo onto a runner until something needs it — the first chat turn, the
/// first task — and that first need then pays for the whole clone inside the turn. On a big
/// project with many repositories that is minutes of a spinner before the agent says a word,
/// and it used to be a dead turn once the op budget ran out (2026-09-07). Doing it here, once
/// per connection, sequentially, with the clone budget, moves that cost to the moment the
/// runner comes up, when nobody is waiting on it. Every op is idempotent on the runner (a
/// present repo is a fetch), so reconnecting costs one fetch per repo, not a clone.
///
/// Best-effort throughout: a repo that cannot be cloned is logged and skipped (the turn or
/// task that needs it will report it properly), a runner that drops mid-way ends the sweep,
/// and a second Hello for the same user while one sweep runs is ignored.
/// </summary>
public sealed class RunnerRepoWarmer(
    MultiTenantRunnerLinkProvider links,
    IServiceScopeFactory scopeFactory,
    ILogger<RunnerRepoWarmer> logger)
{
    /// <summary>An org with more remote repos than this is warmed only this far; the rest
    /// clone on first use as before. Keeps a pathological org from occupying a runner's
    /// network for an hour on every connect.</summary>
    private const int MaxRepos = 100;

    private readonly ConcurrentDictionary<(Ulid OrgId, string UserId), byte> _inFlight = new();

    public async Task WarmAsync(Ulid orgId, string userId, CancellationToken ct)
    {
        if (!_inFlight.TryAdd((orgId, userId), 0))
            return;

        var sw = Stopwatch.StartNew();
        int ready = 0, skipped = 0, total = 0;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var projects = scope.ServiceProvider.GetRequiredService<IProjectsApi>();
            var link = links.GetLink(orgId, userId);

            var repos = new List<RepoSummary>();
            foreach (var projectId in await projects.ListProjectIdsAsync(orgId, ct))
                repos.AddRange(await projects.ListReposAsync(projectId, ct));

            var forgeRepos = repos
                .Where(r => RepoUrl.IsRemote(r.GitUrl))
                .DistinctBy(r => r.Id)
                .Take(MaxRepos)
                .ToList();
            total = forgeRepos.Count;
            if (total == 0)
                return;

            logger.LogInformation(
                "[RunnerWarmup] {User}: warming {Count} repo(s) on the runner", userId, total);

            foreach (var repo in forgeRepos)
            {
                if (!link.IsConnected)
                {
                    logger.LogInformation("[RunnerWarmup] {User}: runner went away — stopping after {Ready}/{Total}",
                        userId, ready, total);
                    return;
                }

                var result = await CloneAsync(link, orgId, repo, ct);
                if (result.Success)
                {
                    ready++;
                    continue;
                }

                skipped++;
                logger.LogInformation(
                    "[RunnerWarmup] {User}: {Repo} not warmed — {Error}", userId, repo.Name, result.Error);
            }
        }
        catch (RunnerUnavailableException)
        {
            // The connection that triggered the sweep is gone; the next Hello starts over.
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[RunnerWarmup] {User}: sweep failed after {Ready}/{Total}", userId, ready, total);
        }
        finally
        {
            _inFlight.TryRemove((orgId, userId), out _);
            if (total > 0)
                logger.LogInformation(
                    "[RunnerWarmup] {User}: {Ready} repo(s) ready, {Skipped} skipped, {Seconds:F0}s",
                    userId, ready, skipped, sw.Elapsed.TotalSeconds);
        }
    }

    /// <summary>The developer's own credentials, like every runner clone — their credential
    /// manager for https, their keys for ssh. A repository they cannot reach is left for the
    /// task or turn that needs it to report.</summary>
    private static Task<RunnerOpResult> CloneAsync(
        IRunnerLink link, Ulid orgId, RepoSummary repo, CancellationToken ct)
        => link.InvokeAsync<RunnerOpResult>(
            RunnerProtocol.EnsureBareClone,
            [new EnsureBareCloneRequest(orgId.ToString(), repo.Id.ToString(), repo.GitUrl, repo.LocalCheckoutPath)],
            ct, RunnerOpBudgets.Clone);
}
