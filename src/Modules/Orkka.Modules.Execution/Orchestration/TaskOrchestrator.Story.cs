namespace Orkka.Modules.Execution.Orchestration;

using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Trackers.Contracts;
using Orkka.SharedKernel.Ai;

/// <summary>
/// The Story context every prompt of a Story member carries (PM review, planning, developing,
/// code review and the interactive turn): the Story's own text, its sub-tasks in order with
/// which are done, which is this one and which are pending, and — for each sub-task already
/// done — its approved plan summary, its commit range and the files it changed (paths and
/// counts from its snapshot; no code is stored). The code itself is in the worktree: the member
/// cloned the shared branch the previous ones pushed.
///
/// A sub-task worked on its own (its Story is someone else's, or the Story's pull request is
/// already closed) carries the lighter "Parent story context": the Story's title and description.
/// </summary>
public sealed partial class TaskOrchestrator
{
    private const int MaxStoryFilesPerMember = 40;

    private async Task<string> BuildStoryContextSectionAsync(
        IServiceScope scope, ITasksApi tasksApi, TaskSnapshot task, CancellationToken ct)
    {
        try
        {
            return await LoadStoryContextSectionAsync(
                tasksApi, scope.ServiceProvider.GetService<ITrackersApi>(), task, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Degrades to the task's own description.
            logger.LogWarning(ex, "Could not load the story context for task {TaskId}", task.Id);
            return "";
        }
    }

    /// <summary>The Story context section for a member, the Parent story context for a sub-task
    /// worked on its own, else empty. Shared with the Discuss chat (<see cref="TaskStoryContextSource"/>).</summary>
    internal static async Task<string> LoadStoryContextSectionAsync(
        ITasksApi tasksApi, ITrackersApi? trackers, TaskSnapshot task, CancellationToken ct)
    {
        if (task.GroupId is not null && task.StoryOrder is not null
            && await tasksApi.GetStoryForTaskAsync(task.Id, ct) is { } story)
            return BuildStoryContextSection(story, task.Id);

        if (task.ExternalKey is not null && trackers is not null
            && await trackers.GetTicketContextAsync(task.Id, ct) is { ParentTitle: { Length: > 0 } parentTitle } ticket)
            return BuildParentStorySection(ticket.ParentKey, parentTitle, ticket.ParentDescription);

        return "";
    }

    internal static string BuildStoryContextSection(StoryContext story, Ulid currentTaskId)
    {
        var members = story.Members.Where(m => !m.IsArchived).OrderBy(m => m.StoryOrder).ToList();
        var current = members.FirstOrDefault(m => m.TaskId == currentTaskId);
        var position = current is null ? 0 : members.IndexOf(current) + 1;

        var sb = new StringBuilder();
        sb.AppendLine($"## Story context — {story.StoryKey ?? story.Title}").AppendLine();
        sb.AppendLine(
            $"This task is sub-task {position} of {members.Count} of a Story. The sub-tasks are built in order on ONE shared "
            + $"branch, `{story.BranchName}`, and the whole story becomes one pull request: build only this sub-task, on top of "
            + "what the earlier ones did, and leave the later ones to their own turn.")
          .AppendLine();
        sb.AppendLine("Story title:");
        sb.AppendLine(PromptSafety.Wrap("story-title", story.Title));
        if (!string.IsNullOrWhiteSpace(story.Description))
        {
            sb.AppendLine("Story description:");
            sb.AppendLine(PromptSafety.Wrap("story-description", story.Description));
        }
        sb.AppendLine();

        sb.AppendLine("Sub-tasks, in order (✓ done, ▶ this one, ○ pending):");
        foreach (var m in members)
        {
            var marker = m.TaskId == currentTaskId ? "▶" : Landed(m) ? "✓" : "○";
            sb.AppendLine($"{marker} {m.StoryOrder}. {m.Title}");
        }
        sb.AppendLine();

        var done = members.Where(m => m.TaskId != currentTaskId && Landed(m)).ToList();
        if (done.Count > 0)
        {
            sb.AppendLine("### What the earlier sub-tasks did").AppendLine();
            foreach (var m in done)
            {
                sb.AppendLine($"#### {m.Title}");
                if (!string.IsNullOrWhiteSpace(m.PlanSummary))
                    sb.AppendLine($"Approved plan: {m.PlanSummary.Trim()}");
                if (m.ReviewBaseSha is { } from)
                {
                    var next = members.SkipWhile(x => x.TaskId != m.TaskId).Skip(1).FirstOrDefault();
                    var to = next?.ReviewBaseSha is { } nextBase ? Short(nextBase) : $"origin/{story.BranchName}";
                    sb.AppendLine($"Commits: {Short(from)}..{to}");
                }
                if (m.Files.Count > 0)
                {
                    sb.AppendLine("Changed files:");
                    foreach (var f in m.Files.Take(MaxStoryFilesPerMember))
                        sb.AppendLine($"- {f.Path} (+{f.Additions} -{f.Deletions})");
                    if (m.Files.Count > MaxStoryFilesPerMember)
                        sb.AppendLine($"- … and {m.Files.Count - MaxStoryFilesPerMember} more");
                }
                sb.AppendLine();
            }
        }

        sb.AppendLine("The previous sub-tasks' code is already in your working copy — use `git log`/`git show` to read it.");
        return sb.ToString();

        // Passed review with its commits on the shared branch.
        static bool Landed(StoryMemberContext m) => m.IsMerged || m.Status == TaskStatus.Completed;
        static string Short(string sha) => sha.Length > 10 ? sha[..10] : sha;
    }

    /// <summary>
    /// The end of a multi-repository Story: before its pull requests are marked ready, the shared
    /// branch must merge into the default branch of EVERY repository it touches
    /// (<c>StoryLandingService</c> checks them all and fails the landing on any conflict). Each
    /// other repository that conflicts is resolved with the existing resolver — base sync,
    /// resolver agent, verification, build check, push — in a clone of that repository's last
    /// member (its pull request already landed, so re-landing it is a no-op). Then, when this
    /// member's own repository merges cleanly, the landing is retried here and returns true;
    /// false hands this member's own conflict to the normal resolution flow.
    /// </summary>
    private async Task<bool> ResolveStoryRepositoriesAsync(
        IServiceScope scope, ITasksApi tasksApi, IPullRequestService pullRequests, IProjectsApi projectsApi,
        TaskSnapshot task, RepoSummary repo, ExecutionRoute route, CancellationToken ct)
    {
        var story = await tasksApi.GetStoryForTaskAsync(task.Id, ct);
        var gitOps = scope.ServiceProvider.GetService<ILocalRepoGitOps>();
        if (story is null || gitOps is null)
            return false;
        var runnerUserId = task.RunnerUserId ?? task.CreatedByUserId;
        var settled = story.Branches.Where(b => b.ForgeState == "merged").Select(b => b.RepoId).ToHashSet();
        var otherRepoIds = story.Branches.Select(b => b.RepoId)
            .Concat(story.Members.Where(m => !m.IsArchived && m.RepoId is not null).Select(m => m.RepoId!.Value))
            .Distinct()
            .Where(id => id != repo.Id && !settled.Contains(id))
            .ToList();

        var conflicted = new List<(RepoSummary Repo, StoryMemberContext Member)>();
        foreach (var repoId in otherRepoIds)
        {
            var other = await projectsApi.GetRepoAsync(repoId, ct);
            if (other is null
                || await gitOps.WouldMergeCleanlyAsync(task.OrgId, repoId, runnerUserId, story.BranchName, other.DefaultBranch, ct) != false)
                continue;
            var member = story.Members.Where(m => !m.IsArchived && m.RepoId == repoId)
                .OrderByDescending(m => m.StoryOrder).FirstOrDefault();
            if (member is not null)
                conflicted.Add((other, member));
        }

        if (conflicted.Count > 0)
        {
            await tasksApi.AddTaskMessageAsync(task.Id, "system",
                $"`{story.BranchName}` no longer merges into the default branch of "
                + string.Join(", ", conflicted.Select(c => $"{c.Repo.Name} (`{c.Repo.DefaultBranch}`)"))
                + ". Resolving it there before the story goes for review — every pull request of the story stays a "
                + "draft until each repository is clean.",
                MessageType.SystemEvent, ct: ct);
        }

        foreach (var (other, member) in conflicted)
        {
            var memberTask = await tasksApi.GetTaskSnapshotAsync(member.TaskId, ct);
            if (memberTask is null)
                continue;
            var memberRoute = executionRouter.Resolve(memberTask);
            var clone = await memberRoute.Git.EnsureBareCloneAsync(task.OrgId, other.Id, other.GitUrl, other.LocalCheckoutPath, ct);
            var worktree = clone.IsFailure
                ? clone
                : await memberRoute.Git.CreateWorktreeAsync(
                    task.OrgId, other.Id, member.TaskId, story.BranchName, story.BranchName, worktreeName: null, ct);
            if (worktree.IsFailure)
            {
                await MergeFailureNotifications.NotifyMergeFailureAsync(tasksApi, taskNotifier, task.Id,
                    $"The story's conflict in {other.Name} could not be resolved: {worktree.Error}", ct);
                return true;
            }

            logger.LogInformation("Story {Story}: resolving the default-branch conflict in {Repo} in {Member}'s clone",
                story.StoryKey ?? story.Title, other.Name, member.TaskId);
            await ResolveConflictsCoreAsync(
                scope, tasksApi, pullRequests, memberTask with { MergeResolutionStrategy = null }, other, memberRoute,
                worktree.Value!, other.DefaultBranch, member.TaskId, ct);
        }

        var ownClean = await gitOps.WouldMergeCleanlyAsync(
            task.OrgId, repo.Id, runnerUserId, story.BranchName, task.BaseBranch ?? repo.DefaultBranch, ct);
        if (ownClean == false)
            return false;
        if (conflicted.Count == 0 && otherRepoIds.Count == 0)
            return false; // a single-repository story: the normal flow, as before

        // Land again: readies every pull request when all repositories are clean now, or fails
        // again (in its own words) for the sweep to pick up.
        if (ParsePrNumber(task.PrUrl) is not { } prNumber)
            return true;
        var landed = await pullRequests.MergeAsync(repo.Id, prNumber, ct);
        if (landed.IsSuccess)
        {
            if (landed.Value!.ForgeUrl is { } forgeUrl)
                await tasksApi.SetTaskPublishedPrUrlAsync(task.Id, forgeUrl, ct);
            await tasksApi.MarkTaskMergedAsync(task.Id, landed.Value.MergeCommitSha, ct);
            await route.Git.CleanupWorktreeAsync(task.OrgId, repo.Id, task.Id);
        }
        else
        {
            await MergeFailureNotifications.NotifyMergeFailureAsync(tasksApi, taskNotifier, task.Id,
                landed.Error ?? "The story could not be marked ready.", ct);
        }
        return true;
    }

    internal static string BuildParentStorySection(string? parentKey, string parentTitle, string? parentDescription)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## Parent story context{(parentKey is null ? "" : $" — {parentKey}")}").AppendLine();
        sb.AppendLine(
            "This ticket is a sub-task of the Story below, which is worked elsewhere. Use it to understand what the "
            + "sub-task is for; build only the sub-task.")
          .AppendLine();
        sb.AppendLine("Story title:");
        sb.AppendLine(PromptSafety.Wrap("story-title", parentTitle));
        if (!string.IsNullOrWhiteSpace(parentDescription))
        {
            sb.AppendLine("Story description:");
            sb.AppendLine(PromptSafety.Wrap("story-description", parentDescription));
        }
        return sb.ToString();
    }
}
