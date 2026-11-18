namespace Orkka.Modules.Tasks.Features.CodeReview;

using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.SharedKernel.Security;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.Modules.Agents.Contracts;
using Orkka.SharedKernel.Storage;
using Orkka.SharedKernel.Utilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using Microsoft.EntityFrameworkCore;

/// <summary>A task's pull request: Orkka's own, scoped by the repository's id.</summary>
public sealed record TaskPullRequestContext(
    TaskEntity Task,
    Ulid RepoId,
    int PrNumber);

public static class CodeReviewHelpers
{
    public static async Task<TaskPullRequestContext?> ResolveTaskContextAsync(
        TasksDbContext db, Orkka.Modules.Projects.Contracts.IProjectsApi projectsApi,
        Ulid taskId, Ulid orgId, CancellationToken ct)
        => (await ResolveTaskContextDetailedAsync(db, projectsApi, taskId, orgId, ct)).Context;

    /// <summary>
    /// Resolves a task's pull request, and — unlike the null-collapsing overload the read
    /// endpoints use — says WHICH cause failed. Approve and merge return this message.
    /// </summary>
    public static async Task<(TaskPullRequestContext? Context, bool TaskExists, string? Error)>
        ResolveTaskContextDetailedAsync(
            TasksDbContext db, Orkka.Modules.Projects.Contracts.IProjectsApi projectsApi,
            Ulid taskId, Ulid orgId, CancellationToken ct)
    {
        var task = await db.Tasks
            .FirstOrDefaultAsync(t => t.Id == taskId && t.OrgId == orgId, ct);

        if (task is null)
            return (null, false, "Task not found.");
        if (task.PrUrl is null)
            return (null, true, "This task has no pull request yet. The platform opens one "
                + "automatically once the task's branch is pushed; if it stays missing, check "
                + "the task timeline for the reason.");

        var repo = task.RepoId is { } repoId
            ? await projectsApi.GetRepoAsync(repoId, ct)
            : null;
        if (repo is null)
            return (null, true, "The task's repository is no longer linked to the project.");

        var prNumber = ParsePrNumber(task.PrUrl);
        if (prNumber is null)
            return (null, true, $"The stored pull request URL ({task.PrUrl}) has no PR number "
                + "— the record is corrupt. Reset the PR from the task page or re-create it.");

        return (new TaskPullRequestContext(task, repo.Id, prNumber.Value), true, null);
    }

    public static int? ParsePrNumber(string prUrl)
    {
        // Orkka's own pull request URLs: orkka-local://pull/42
        var lastSlash = prUrl.LastIndexOf('/');
        if (lastSlash < 0) return null;

        var numberStr = prUrl[(lastSlash + 1)..];
        return int.TryParse(numberStr, out var number) ? number : null;
    }
}
