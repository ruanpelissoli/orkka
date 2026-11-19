namespace Orkka.Modules.SourceControl.Infrastructure.Services;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.Modules.SourceControl.Domain;
using Orkka.Modules.Tasks.Contracts;
using Orkka.SharedKernel.Utilities;

/// <summary>
/// <see cref="IPullRequestService"/>: Orkka's own pull requests, for every repository.
///
/// Pull requests are rows here and diffs come from the developer's machine. "Merge" squash-merges
/// the agent's branch into a folder on that machine, or — for a remote — publishes the work as a
/// forge pull request their runner opens with their own CLI. No forge API, no credential.
///
/// The machine is reached across the network through the Worker that holds its socket
/// (<see cref="ILocalRepoGitOps"/>), and it can be asleep. Every path that needs it therefore
/// degrades rather than throws — an unreachable machine yields an empty diff and an unknown
/// mergeability, never a failed task.
///
/// Every lookup is scoped to the repository id the caller names, which is what keeps one org's
/// branch name or pull request number from ever matching another's.
/// </summary>
public sealed class OrkkaPullRequestService(
    SourceControlDbContext db,
    IProjectsApi projectsApi,
    IServiceProvider services,
    ILocalRepoGitOps gitOps,
    ILogger<OrkkaPullRequestService> logger,
    // Optional so tests building the service alone keep compiling; DI supplies the real one.
    StoryLandingService? storyLanding = null) : IPullRequestService
{
    /// <summary>
    /// Resolved at call time, not injected: ITasksApi's own graph reaches this service
    /// (BoardOperations approves and merges through it), so as a constructor parameter it is
    /// a cycle and no host starts.
    /// </summary>
    private ITasksApi TasksApi => services.GetRequiredService<ITasksApi>();

    private static readonly GitHubUser LocalAgentUser = new(1, "orkka-agent", "");

    public async Task<Result<IReadOnlyList<GitHubPrSummary>>> ListAsync(
        Ulid repoId, string state = "all", CancellationToken ct = default)
    {
        var query = db.LocalPullRequests.IgnoreQueryFilters().Where(p => p.RepoId == repoId);
        query = state switch
        {
            "open" => query.Where(p => !p.Merged && p.ClosedAt == null),
            "closed" => query.Where(p => p.Merged || p.ClosedAt != null),
            _ => query,
        };

        var rows = await query.OrderByDescending(p => p.Number).Take(100).ToListAsync(ct);

        return Result<IReadOnlyList<GitHubPrSummary>>.Success(
            [.. rows.Select(p => new GitHubPrSummary(
                p.Number, p.Number, p.Title,
                p.Merged || p.ClosedAt != null ? "closed" : "open", p.Merged, Draft: false,
                p.HeadBranch, p.BaseBranch, HtmlUrl(p),
                LocalAgentUser.Login, p.CreatedAt, p.MergedAt))]);
    }

    public Task<Result<GitHubPullRequest>> CreateAsync(
        Ulid repoId, string head, string baseBranch, string title, string body,
        CancellationToken ct = default)
        => CreateCoreAsync(repoId, null, head, baseBranch, title, body, null, ct);

    public Task<Result<GitHubPullRequest>> CreateForTaskAsync(
        Ulid repoId, Ulid taskId, string head, string baseBranch, string title, string body,
        string? baseSha = null, CancellationToken ct = default)
        => CreateCoreAsync(repoId, taskId, head, baseBranch, title, body, baseSha, ct);

    private async Task<Result<GitHubPullRequest>> CreateCoreAsync(
        Ulid repoId, Ulid? explicitTaskId, string head, string baseBranch, string title, string body,
        string? baseSha, CancellationToken ct)
    {
        var repoSummary = await projectsApi.GetRepoAsync(repoId, ct);
        if (repoSummary is null)
            return Result<GitHubPullRequest>.Failure(
                "Cannot create a pull request: the repository is no longer linked to the project.");

        // Idempotency — re-entering the Developing phase must not mint a second pull request
        // for the same branch. Scoped to the repo, which is also what keeps one org's branch
        // name from ever matching another's.
        // With the task named, idempotency is per task too: a Story's next member opens its own
        // pull request on the shared branch even while the previous one's has not landed yet.
        var existing = await db.LocalPullRequests.IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                p => p.RepoId == repoId && p.HeadBranch == head && !p.Merged && p.ClosedAt == null
                    && (explicitTaskId == null || p.TaskId == explicitTaskId || p.TaskId == null), ct);
        if (existing is not null)
        {
            // A member's review base is recorded once — and again only after a reset, when it
            // re-plans on top of the shared branch as it stands (its earlier commits included):
            // the open pull request follows, or it would keep showing the old attempt.
            var newBase = GitArgs.IsSafeSha(baseSha) ? baseSha : null;
            if (explicitTaskId is not null
                && (existing.TaskId is null || (newBase is not null && existing.BaseSha != newBase)))
            {
                existing.TaskId ??= explicitTaskId;
                existing.BaseSha = newBase ?? existing.BaseSha;
                await db.SaveChangesAsync(ct);
            }
            return Result<GitHubPullRequest>.Success(ToPr(existing));
        }

        var taskId = explicitTaskId ?? await TasksApi.FindTaskIdByBranchAsync(head, [repoId], ct);

        var pr = new LocalPullRequest
        {
            Id = Ulid.NewUlid(),
            OrgId = repoSummary.OrgId,
            Number = await NextNumberAsync(repoSummary.OrgId, ct),
            TaskId = taskId,
            RepoId = repoId,
            Title = title,
            Body = body,
            HeadBranch = head,
            BaseBranch = baseBranch,
            BaseSha = GitArgs.IsSafeSha(baseSha) ? baseSha : null,
        };

        db.LocalPullRequests.Add(pr);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "[PR] Created PR #{Number} for branch {Branch} on repo {RepoId}",
            pr.Number, head, repoId);
        return Result<GitHubPullRequest>.Success(ToPr(pr));
    }

    public async Task<Result<GitHubPullRequest>> MergeAsync(
        Ulid repoId, int prNumber, CancellationToken ct = default)
    {
        var pr = await FindPrAsync(repoId, prNumber, ct);
        if (pr is null)
            return Result<GitHubPullRequest>.Failure($"Pull request #{prNumber} not found.");
        if (pr.Merged)
            return Result<GitHubPullRequest>.Success(ToPr(pr));

        var repoSummary = await projectsApi.GetRepoAsync(pr.RepoId, ct);
        if (repoSummary is null)
            return Result<GitHubPullRequest>.Failure("The pull request's repository no longer exists.");

        // A Story member lands on the Story's shared branch: one branch, one forge pull request,
        // a draft after the first member and ready after the last (StoryLandingService).
        if (pr.TaskId is { } memberTaskId
            && await TasksApi.GetStoryForTaskAsync(memberTaskId, ct) is { } story)
        {
            var landing = storyLanding ?? new StoryLandingService(db, projectsApi, services, gitOps,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<StoryLandingService>.Instance);
            return await landing.LandAsync(pr, repoSummary, story, ct);
        }

        if (RepoUrl.Classify(repoSummary.GitUrl) == RepoLocation.Remote)
            return await PublishAsync(pr, repoSummary, ct);

        var merge = await gitOps.SquashMergeAsync(
            pr.OrgId, pr.RepoId, await RunnerUserForAsync(pr, ct),
            pr.HeadBranch, pr.BaseBranch,
            $"{pr.Title} (#{pr.Number})\n\n{pr.Body}\n\nMerged by Orkka", ct);

        if (merge.IsFailure)
            return Result<GitHubPullRequest>.Failure(merge.Error!);

        pr.Merged = true;
        pr.MergedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("[PR] Merged PR #{Number} into {Base} of {Path}",
            pr.Number, pr.BaseBranch, repoSummary.GitUrl);
        return Result<GitHubPullRequest>.Success(ToPr(pr));
    }

    /// <summary>
    /// A remote repository's reviewed work goes to its forge as a pull request the developer's
    /// runner opens with their own CLI — never merged by Orkka. The branch is already on the
    /// remote (it is pushed after every run). Publishing twice finds the open forge pull
    /// request instead of opening another.
    /// </summary>
    private async Task<Result<GitHubPullRequest>> PublishAsync(
        LocalPullRequest pr, RepoSummary repo, CancellationToken ct)
    {
        // A task imported from a GitHub issue in this same repository says "Closes owner/repo#n",
        // so GitHub links the pull request to the issue and closes it on merge.
        var body = pr.Body ?? "";
        if (pr.TaskId is { } taskId && await TasksApi.GetTaskSnapshotAsync(taskId, ct) is { ExternalUrl: { } issueUrl })
            body = GitHubIssueLinks.AppendClosing(body, GitHubIssueLinks.ClosingReferences([issueUrl], repo.GitUrl));

        var published = await gitOps.OpenPullRequestAsync(
            pr.OrgId, pr.RepoId, await RunnerUserForAsync(pr, ct),
            pr.HeadBranch, pr.BaseBranch, pr.Title, body,
            repo.ForgeKind, repo.CliAccount, ct);

        if (published.IsFailure)
            return Result<GitHubPullRequest>.Failure(published.Error!);

        pr.ForgeUrl = published.Value;
        pr.ForgeState = "open";
        pr.ForgeCheckedAt = DateTimeOffset.UtcNow;
        pr.Merged = true;
        pr.MergedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("[PR] Published PR #{Number} ({Branch}) as {Url}",
            pr.Number, pr.HeadBranch, pr.ForgeUrl);
        return Result<GitHubPullRequest>.Success(ToPr(pr));
    }

    /// <summary>
    /// Closes a pull request without merging. Nothing runs on the user's machine: closing is
    /// purely a state change here, and the head branch is deliberately left in their
    /// repository so the commits stay reachable.
    /// </summary>
    public async Task<Result> CloseAsync(Ulid repoId, int prNumber, CancellationToken ct = default)
    {
        var pr = await FindPrAsync(repoId, prNumber, ct);
        if (pr is null)
            return Result.Failure($"Pull request #{prNumber} not found.");

        // A merged PR reads as closed too, so callers can reach here after a merge.
        // Never rewrite that outcome.
        if (pr.Merged)
            return Result.Success();

        pr.ClosedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("[PR] Closed PR #{Number} without merging", pr.Number);
        return Result.Success();
    }

    public async Task<Result<GitHubPullRequestDetail>> GetAsync(
        Ulid repoId, int prNumber, CancellationToken ct = default)
    {
        var pr = await FindPrAsync(repoId, prNumber, ct);
        if (pr is null)
            return Result<GitHubPullRequestDetail>.Failure($"Pull request #{prNumber} not found.");

        var runnerUserId = await RunnerUserForAsync(pr, ct);
        var (additions, deletions, files) = await DiffStatsAsync(pr, runnerUserId, ct);

        // Real mergeability via a dry-run merge on the user's machine, so the poller can route
        // a conflicted branch into automatic resolution instead of failing the squash merge.
        // Unknown (machine asleep, older runner) stays UNKNOWN — null, as GitHub reports a PR
        // it has not computed yet. It used to read as true, and "true" is an answer the merge
        // sweep acts on: two seconds after a runner dropped mid-resolution it cleared the
        // task's conflict notice as stale and gave the conflict back to a dev pass (prod,
        // 2026-09-17). The sweep still attempts the merge on unknown and reads what it says.
        bool? mergeable = pr.Merged
            ? false
            : await gitOps.WouldMergeCleanlyAsync(
                pr.OrgId, pr.RepoId, runnerUserId, pr.HeadBranch, pr.BaseBranch, ct);

        return Result<GitHubPullRequestDetail>.Success(new GitHubPullRequestDetail(
            Id: pr.Number,
            Number: pr.Number,
            HtmlUrl: HtmlUrl(pr),
            State: pr.Merged || pr.ClosedAt != null ? "closed" : "open",
            Title: pr.Title,
            Body: pr.Body,
            Mergeable: mergeable,
            Draft: false,
            Additions: additions,
            Deletions: deletions,
            ChangedFiles: files.Count,
            HeadSha: "local",
            BaseBranch: pr.BaseBranch,
            HeadBranch: pr.HeadBranch,
            Merged: pr.Merged,
            ForgeUrl: pr.ForgeUrl));
    }

    public async Task<Result<IReadOnlyList<GitHubPrFile>>> GetFilesAsync(
        Ulid repoId, int prNumber, CancellationToken ct = default)
    {
        var pr = await FindPrAsync(repoId, prNumber, ct);
        if (pr is null)
            return Result<IReadOnlyList<GitHubPrFile>>.Failure($"Pull request #{prNumber} not found.");

        var (_, _, files) = await DiffStatsAsync(pr, await RunnerUserForAsync(pr, ct), ct);
        return Result<IReadOnlyList<GitHubPrFile>>.Success(files);
    }

    public async Task<Result<IReadOnlyList<GitHubReviewComment>>> GetCommentsAsync(
        Ulid repoId, int prNumber, CancellationToken ct = default)
    {
        var pr = await FindPrAsync(repoId, prNumber, ct);
        if (pr is null)
            return Result<IReadOnlyList<GitHubReviewComment>>.Failure($"Pull request #{prNumber} not found.");

        var comments = await db.LocalPrComments.IgnoreQueryFilters()
            .Where(c => c.PullRequestId == pr.Id)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);

        return Result<IReadOnlyList<GitHubReviewComment>>.Success(
            [.. comments.Select(c => new GitHubReviewComment(
                StableId(c.Id), c.Body, c.Path, c.Line, c.Line, "local",
                c.CreatedAt.ToString("O"), c.UpdatedAt.ToString("O"),
                new GitHubUser(1, c.Author, "")))]);
    }

    public async Task<Result<IReadOnlyList<GitHubPrReview>>> GetReviewsAsync(
        Ulid repoId, int prNumber, CancellationToken ct = default)
    {
        var pr = await FindPrAsync(repoId, prNumber, ct);
        if (pr is null)
            return Result<IReadOnlyList<GitHubPrReview>>.Failure($"Pull request #{prNumber} not found.");

        var reviews = await db.LocalPrReviews.IgnoreQueryFilters()
            .Where(r => r.PullRequestId == pr.Id)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync(ct);

        return Result<IReadOnlyList<GitHubPrReview>>.Success(
            [.. reviews.Select(r => new GitHubPrReview(
                StableId(r.Id), r.State, r.Body, r.CreatedAt.ToString("O"),
                new GitHubUser(1, r.Author, "")))]);
    }

    public async Task<Result<GitHubPrReview>> CreateReviewAsync(
        Ulid repoId, int prNumber, string eventType, string? body,
        IReadOnlyList<GitHubNewReviewComment>? comments, CancellationToken ct = default)
    {
        var pr = await FindPrAsync(repoId, prNumber, ct);
        if (pr is null)
            return Result<GitHubPrReview>.Failure($"Pull request #{prNumber} not found.");

        var state = eventType switch
        {
            "APPROVE" => "APPROVED",
            "REQUEST_CHANGES" => "CHANGES_REQUESTED",
            _ => "COMMENTED",
        };

        var review = new LocalPrReview
        {
            Id = Ulid.NewUlid(),
            OrgId = pr.OrgId,
            PullRequestId = pr.Id,
            State = state,
            Body = body,
            Author = "orkka-agent",
        };
        db.LocalPrReviews.Add(review);

        foreach (var comment in comments ?? [])
        {
            db.LocalPrComments.Add(new LocalPrComment
            {
                Id = Ulid.NewUlid(),
                OrgId = pr.OrgId,
                PullRequestId = pr.Id,
                Path = comment.Path,
                Line = comment.Line,
                Body = comment.Body,
                Author = "orkka-agent",
            });
        }

        await db.SaveChangesAsync(ct);

        return Result<GitHubPrReview>.Success(new GitHubPrReview(
            StableId(review.Id), state, body, DateTimeOffset.UtcNow.ToString("O"), LocalAgentUser));
    }

    public async Task<Result<GitHubReviewComment>> CreateCommentAsync(
        Ulid repoId, int prNumber, string commitSha, string path, int line, string body,
        CancellationToken ct = default)
    {
        var pr = await FindPrAsync(repoId, prNumber, ct);
        if (pr is null)
            return Result<GitHubReviewComment>.Failure($"Pull request #{prNumber} not found.");

        var comment = new LocalPrComment
        {
            Id = Ulid.NewUlid(),
            OrgId = pr.OrgId,
            PullRequestId = pr.Id,
            Path = path,
            Line = line,
            Body = body,
            Author = "you",
        };
        db.LocalPrComments.Add(comment);
        await db.SaveChangesAsync(ct);

        return Result<GitHubReviewComment>.Success(new GitHubReviewComment(
            StableId(comment.Id), body, path, line, line, commitSha,
            DateTimeOffset.UtcNow.ToString("O"), DateTimeOffset.UtcNow.ToString("O"),
            new GitHubUser(2, "you", "")));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>Orkka's own address for the pull request. Always this, even once published:
    /// callers read the number back out of it, and the forge's number is a different one.</summary>
    private static string HtmlUrl(LocalPullRequest pr) => $"orkka-local://pull/{pr.Number}";

    private static GitHubPullRequest ToPr(LocalPullRequest pr)
        => new(pr.Number, pr.Number, HtmlUrl(pr),
            pr.Merged || pr.ClosedAt != null ? "closed" : "open", ForgeUrl: pr.ForgeUrl);

    /// <summary>Stable long id derived from the ULID so the UI's keys and comment threading
    /// stay consistent across requests.</summary>
    private static long StableId(Ulid id)
        => BitConverter.ToInt64(id.ToByteArray(), 8) & long.MaxValue;

    /// <summary>Next number for the org. Concurrent sweeps racing here collide on the unique
    /// (OrgId, Number) index rather than silently minting duplicates.</summary>
    private async Task<int> NextNumberAsync(Ulid orgId, CancellationToken ct)
        => (await db.LocalPullRequests.IgnoreQueryFilters()
            .Where(p => p.OrgId == orgId)
            .Select(p => (int?)p.Number)
            .MaxAsync(ct) ?? 0) + 1;

    /// <summary>
    /// The pull request, scoped to the repository the caller named. A number alone is unique
    /// per org, but never trust it across orgs: these rows are keyed by (OrgId, Number), and
    /// the repo id is the only tenant evidence some call sites carry.
    /// </summary>
    private Task<LocalPullRequest?> FindPrAsync(Ulid repoId, int prNumber, CancellationToken ct)
        => db.LocalPullRequests.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.RepoId == repoId && p.Number == prNumber, ct);

    /// <summary>
    /// Whose machine holds the work. The task carries it: every task runs on its owner's
    /// runner, and that machine has the worktree, the folder and the CLI that publishes. Null
    /// falls back to whatever the ops implementation can resolve on its own.
    /// </summary>
    private async Task<string?> RunnerUserForAsync(LocalPullRequest pr, CancellationToken ct)
    {
        if (pr.TaskId is not { } taskId)
            return null;

        var task = await TasksApi.GetTaskSnapshotAsync(taskId, ct);
        return task?.RunnerUserId ?? task?.CreatedByUserId;
    }

    private async Task<(int Additions, int Deletions, List<GitHubPrFile> Files)> DiffStatsAsync(
        LocalPullRequest pr, string? runnerUserId, CancellationToken ct)
    {
        if (pr.TaskId is null)
            return (0, 0, []);

        // A Story member's pull request shows its own change (BaseSha..head), not the whole
        // shared branch — the forge pull request is where the whole story is reviewed.
        var diff = pr.BaseSha is { } baseSha
            ? await gitOps.GetDiffAsync(
                pr.OrgId, pr.RepoId, pr.TaskId.Value, runnerUserId, pr.BaseBranch, pr.HeadBranch, baseSha, ct)
            : await gitOps.GetDiffAsync(
                pr.OrgId, pr.RepoId, pr.TaskId.Value, runnerUserId, pr.BaseBranch, pr.HeadBranch, ct);

        if (diff.IsFailure)
        {
            // An unreachable machine is an ordinary state — the user closed their laptop.
            // Rendering "+0 -0" beats failing the page that was going to show the review.
            logger.LogInformation(
                "[PR] Diff unavailable for PR #{Number} ({Branch}): {Error}",
                pr.Number, pr.HeadBranch, diff.Error);
            return (0, 0, []);
        }

        return UnifiedDiffParser.Parse(diff.Value ?? "");
    }
}
