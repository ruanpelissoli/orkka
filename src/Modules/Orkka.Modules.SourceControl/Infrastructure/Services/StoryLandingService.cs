namespace Orkka.Modules.SourceControl.Infrastructure.Services;

using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.Modules.SourceControl.Domain;
using Orkka.Modules.Tasks.Contracts;
using Orkka.SharedKernel.Utilities;

/// <summary>
/// Landing a Story member: one branch, one pull request.
///
/// A member's Orkka pull request "merges" by landing on the Story's shared branch — its commits
/// are already there (the runner pushes after every run), so landing is a record: the local pull
/// request is Merged and the task Completed, while the ticket stays open. The first member to
/// land opens the forge pull request as a DRAFT with the developer's CLI; each landing after
/// updates its checklist; the last one (no member left that is neither landed nor archived, no
/// sub-task still pending) makes sure the shared branch merges cleanly into the default branch
/// (a conflicting one goes through the existing resolver first), marks every repository's pull
/// request ready, and stamps the Story ready — which is when the Story and every sub-task move
/// to "PR opened" in the tracker. A linked folder with no origin has no forge pull request: the
/// whole shared branch is squash-merged into the folder at the end.
/// </summary>
public sealed class StoryLandingService(
    SourceControlDbContext db,
    IProjectsApi projectsApi,
    IServiceProvider services,
    ILocalRepoGitOps gitOps,
    ILogger<StoryLandingService> logger) : IStoryPullRequests
{
    private ITasksApi TasksApi => services.GetRequiredService<ITasksApi>();

    public async Task<Result<GitHubPullRequest>> LandAsync(
        LocalPullRequest pr, RepoSummary repo, StoryContext story, CancellationToken ct)
    {
        var member = story.Members.FirstOrDefault(m => m.TaskId == pr.TaskId);
        if (member is null)
            return Result<GitHubPullRequest>.Failure("This task is no longer part of its story.");

        var runnerUserId = member.RunnerUserId;
        var isRemote = RepoUrl.Classify(repo.GitUrl) == RepoLocation.Remote;
        var last = story.PendingChildCount == 0
            && story.Members.Where(m => m.TaskId != member.TaskId).All(m => m.IsArchived || m.IsMerged);

        if (last)
        {
            // The whole story goes for review now: in EVERY repository it touches, the shared
            // branch must merge into that repository's default branch (the check refreshes the
            // mirror from origin first). Nothing is marked ready unless all are clean. A
            // conflict anywhere fails the landing with StoryLandingErrors, which sends it
            // through the existing resolver — for each conflicting repository, in the clone of
            // that repository's last member (TaskOrchestrator) — and then this landing again.
            var readiness = await CheckEveryRepositoryAsync(pr, repo, story, runnerUserId, ct);
            if (readiness is not null)
                return Result<GitHubPullRequest>.Failure(readiness);
        }

        var row = story.Branches.FirstOrDefault(b => b.RepoId == repo.Id);
        var prUrl = row?.ForgePrUrl;
        if (isRemote && prUrl is null)
        {
            var opened = await gitOps.OpenDraftPullRequestAsync(
                pr.OrgId, pr.RepoId, runnerUserId, story.BranchName, pr.BaseBranch,
                StoryTitle(story), Body(story, member.TaskId, null, repo.GitUrl),
                repo.ForgeKind, repo.CliAccount, ct);
            if (opened.IsFailure)
            {
                // Early drafts are a courtesy; the end of the story is not.
                if (last)
                    return Result<GitHubPullRequest>.Failure(opened.Error!);
                await TasksApi.AddTaskMessageAsync(member.TaskId, "system",
                    $"Landed on `{story.BranchName}`, but the story's draft pull request could not be opened: "
                    + $"{opened.Error} It is tried again when the next sub-task lands.",
                    MessageType.SystemEvent, ct: ct);
            }
            else
            {
                prUrl = opened.Value!;
                await TasksApi.SetGroupBranchPullRequestAsync(story.GroupId, repo.Id, story.BranchName, prUrl, draft: true, ct);
                story = story with
                {
                    Branches = [.. story.Branches.Where(b => b.RepoId != repo.Id),
                        new StoryBranchState(repo.Id, story.BranchName, prUrl, true, null, "open")],
                };
                await TasksApi.AddTaskMessageAsync(member.TaskId, "system",
                    $"Opened the story's draft pull request: {prUrl}", MessageType.SystemEvent, ct: ct);
            }
        }

        if (last)
        {
            var finished = await FinishStoryAsync(pr, story, member, ct);
            if (finished.IsFailure)
                return Result<GitHubPullRequest>.Failure(finished.Error!);
        }
        else
        {
            // Tick the landed sub-task in every repository's pull request body. Best effort: a
            // stale checklist is cosmetic.
            await UpdateBodiesAsync(story, member.TaskId, runnerUserId, ct);
        }

        pr.Merged = true;
        pr.MergedAt = DateTimeOffset.UtcNow;
        if (prUrl is not null)
        {
            pr.ForgeUrl = prUrl;
            pr.ForgeState = "open";
            pr.ForgeCheckedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);

        logger.LogInformation("[PR] Story member PR #{Number} landed on {Branch}{Last}",
            pr.Number, story.BranchName, last ? " — story ready" : "");
        return Result<GitHubPullRequest>.Success(new GitHubPullRequest(
            pr.Number, pr.Number, $"orkka-local://pull/{pr.Number}", "closed", ForgeUrl: pr.ForgeUrl));
    }

    /// <summary>Every repository the story touches — its <c>group_branches</c> rows, the members'
    /// repositories and the landing one — except a folder already squash-merged.</summary>
    internal static IReadOnlyList<Ulid> StoryRepoIds(StoryContext story, Ulid landingRepoId)
    {
        var settled = story.Branches.Where(b => b.ForgeState == "merged").Select(b => b.RepoId).ToHashSet();
        return [.. story.Branches.Select(b => b.RepoId)
            .Concat(story.Members.Where(m => !m.IsArchived && m.RepoId is not null).Select(m => m.RepoId!.Value))
            .Append(landingRepoId)
            .Distinct()
            .Where(id => id == landingRepoId || !settled.Contains(id))];
    }

    /// <summary>The default-branch merge check in every repository of the story. Null = all
    /// clean; otherwise why the story cannot go for review yet.</summary>
    private async Task<string?> CheckEveryRepositoryAsync(
        LocalPullRequest pr, RepoSummary landingRepo, StoryContext story, string? runnerUserId, CancellationToken ct)
    {
        var conflicted = new List<(string Repo, string DefaultBranch)>();
        var unknown = new List<string>();
        foreach (var repoId in StoryRepoIds(story, landingRepo.Id))
        {
            var repo = repoId == landingRepo.Id ? landingRepo : await projectsApi.GetRepoAsync(repoId, ct);
            if (repo is null)
                continue;
            var baseBranch = repoId == landingRepo.Id ? pr.BaseBranch : repo.DefaultBranch;
            var clean = await gitOps.WouldMergeCleanlyAsync(pr.OrgId, repoId, runnerUserId, story.BranchName, baseBranch, ct);
            if (clean == false)
                conflicted.Add((repo.Name, baseBranch));
            else if (clean is null)
                unknown.Add(repo.Name);
        }

        if (conflicted.Count > 0)
            return StoryLandingErrors.Conflict(story.BranchName, conflicted);
        if (unknown.Count > 0)
            // Read as environment-blocked by the merge sweep: retried every cycle, never readied
            // on a guess.
            return $"Could not check whether `{story.BranchName}` merges into the default branch in "
                + $"{string.Join(", ", unknown)}: the runner is not connected or could not answer. The story's pull "
                + "requests stay drafts until every repository is checked clean.";
        return null;
    }

    /// <summary>The last member landed: folders get the whole shared branch squash-merged, forge
    /// pull requests are marked ready, and the story is stamped ready (and settled when nothing
    /// is left on a forge).</summary>
    private async Task<Result> FinishStoryAsync(
        LocalPullRequest pr, StoryContext story, StoryMemberContext landing, CancellationToken ct)
    {
        var runnerUserId = landing.RunnerUserId;
        var repoIds = StoryRepoIds(story, pr.RepoId);
        var anyForge = false;

        foreach (var repoId in repoIds)
        {
            var repo = await projectsApi.GetRepoAsync(repoId, ct);
            if (repo is null)
                continue;
            var row = story.Branches.FirstOrDefault(b => b.RepoId == repoId);

            if (RepoUrl.Classify(repo.GitUrl) != RepoLocation.Remote)
            {
                if (row?.ForgeState == "merged")
                    continue; // squashed when the story was first readied
                var merge = await gitOps.SquashMergeAsync(
                    pr.OrgId, repoId, runnerUserId, story.BranchName, repo.DefaultBranch,
                    $"{StoryTitle(story)}\n\n{Body(story, landing.TaskId, null)}\n\nMerged by Orkka", ct);
                if (merge.IsFailure)
                    return merge;
                // Recorded on the repository's row, so a late sub-task never squashes it twice.
                await TasksApi.SetGroupBranchPullRequestAsync(story.GroupId, repoId, story.BranchName, null, false, ct);
                await TasksApi.SetGroupBranchForgeStateAsync(story.GroupId, repoId, "merged", ct);
                continue;
            }

            anyForge = true;
            if (row?.ForgePrUrl is not { } url)
                return Result.Failure($"The story's pull request for {repo.Name} was never opened.");
            var ready = await gitOps.MarkPullRequestReadyAsync(
                pr.OrgId, repoId, runnerUserId, url, undo: false, repo.ForgeKind, repo.CliAccount, ct);
            if (ready.IsFailure)
                return ready;
        }

        await UpdateBodiesAsync(story, landing.TaskId, runnerUserId, ct);
        var now = DateTimeOffset.UtcNow;
        await TasksApi.SetStoryReadyAsync(story.GroupId, now, ct);
        if (!anyForge)
            await TasksApi.SettleStoryAsync(story.GroupId, ct);

        var links = story.Branches.Where(b => b.ForgePrUrl is not null).Select(b => b.ForgePrUrl!).ToList();
        await TasksApi.AddTaskMessageAsync(landing.TaskId, "system",
            anyForge
                ? $"Every sub-task of {story.StoryKey ?? story.Title} landed — the story's pull request is ready for review: "
                  + string.Join(", ", links)
                : $"Every sub-task of {story.StoryKey ?? story.Title} landed — `{story.BranchName}` was squash-merged into your folder.",
            MessageType.SystemEvent, ct: ct);
        return Result.Success();
    }

    public async Task<Result> ReturnToDraftAsync(Ulid groupId, CancellationToken ct = default)
    {
        var story = await TasksApi.GetStoryAsync(groupId, ct);
        if (story is null)
            return Result.Failure("Not a story.");

        var runnerUserId = story.Members.Select(m => m.RunnerUserId).FirstOrDefault(u => u is not null);
        string? error = null;
        foreach (var row in story.Branches.Where(b => b.ForgePrUrl is not null && b.ForgeState is not ("merged" or "closed")))
        {
            var repo = await projectsApi.GetRepoAsync(row.RepoId, ct);
            if (repo is null)
                continue;
            var undo = await gitOps.MarkPullRequestReadyAsync(
                story.OrgId, row.RepoId, runnerUserId, row.ForgePrUrl!, undo: true, repo.ForgeKind, repo.CliAccount, ct);
            if (undo.IsFailure)
                error ??= undo.Error;
        }
        await TasksApi.SetStoryReadyAsync(groupId, null, ct);
        return error is null ? Result.Success() : Result.Failure(error);
    }

    private async Task UpdateBodiesAsync(StoryContext story, Ulid landingTaskId, string? runnerUserId, CancellationToken ct)
    {
        foreach (var row in story.Branches.Where(b => b.ForgePrUrl is not null))
        {
            var repo = await projectsApi.GetRepoAsync(row.RepoId, ct);
            if (repo is null)
                continue;
            var updated = await gitOps.UpdatePullRequestBodyAsync(
                story.OrgId, row.RepoId, runnerUserId, row.ForgePrUrl!, Body(story, landingTaskId, row.RepoId, repo.GitUrl),
                repo.ForgeKind, repo.CliAccount, ct);
            if (updated.IsFailure)
                logger.LogInformation("[PR] Could not update the body of {Url}: {Error}", row.ForgePrUrl, updated.Error);
        }
    }

    internal static string StoryTitle(StoryContext story) => story.Title;

    /// <summary>
    /// The forge pull request's description: the Story, its sub-tasks in order (ticked as they
    /// land), the ticket links, and — for a story spanning repositories — the sibling pull
    /// requests. Ticket text itself stays out: the tracker has it. For GitHub issues in the pull
    /// request's own repository (<paramref name="repoGitUrl"/>), the Story and its sub-issues get a
    /// <c>Closes owner/repo#n</c> line each, so GitHub links them and closes them on merge.
    /// </summary>
    internal static string Body(StoryContext story, Ulid landingTaskId, Ulid? forRepo, string? repoGitUrl = null)
    {
        var sb = new StringBuilder();
        sb.Append("Story: ");
        sb.Append(story.StoryUrl is { } url ? $"[{story.StoryKey ?? story.Title}]({url})" : story.StoryKey ?? story.Title);
        sb.Append("\n\n### Sub-tasks\n\n");
        foreach (var m in story.Members.Where(m => !m.IsArchived).OrderBy(m => m.StoryOrder))
        {
            var done = m.IsMerged || m.TaskId == landingTaskId;
            var label = m.ExternalUrl is { } link && m.ExternalKey is { } key
                ? m.Title.Replace($"[{key}]", $"[{key}]({link})", StringComparison.Ordinal)
                : m.Title;
            sb.Append(done ? "- [x] " : "- [ ] ").Append(label).Append('\n');
        }

        var siblings = story.Branches.Where(b => b.ForgePrUrl is not null && b.RepoId != forRepo).ToList();
        if (siblings.Count > 0 && story.Branches.Count > 1)
        {
            sb.Append("\n### Also part of this story\n\n");
            foreach (var b in siblings)
                sb.Append("- ").Append(b.ForgePrUrl).Append('\n');
        }

        sb.Append("\nOne branch, one pull request: each sub-task was built, reviewed and committed in order on `")
          .Append(story.BranchName).Append("` by Orkka.");

        var closing = GitHubIssueLinks.ClosingReferences(
            [story.StoryUrl, .. story.Members.Where(m => !m.IsArchived).OrderBy(m => m.StoryOrder).Select(m => m.ExternalUrl)],
            repoGitUrl);
        return GitHubIssueLinks.AppendClosing(sb.ToString(), closing);
    }
}
