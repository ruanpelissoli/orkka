namespace Orkka.Modules.Tasks.Features.CodeChanges;

using Orkka.Modules.SourceControl.Contracts;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using FastEndpoints;

/// <summary>
/// A snapshot's diff — read live from the task owner's runner, never from Orkka's storage
/// (Orkka keeps no code; the snapshot row holds paths and counts only).
///
/// A run in progress shows its worktree as it is; a finished snapshot shows its branch as it is
/// now against the base, flagged <c>CurrentBranch</c>, since the exact historical diff is not
/// kept anywhere but the developer's own git. An offline runner or a branch that is gone comes
/// back as an empty diff with the reason in <c>Unavailable</c> — the list view still has the
/// files and counts.
/// </summary>
public sealed class GetCodeChangeSnapshotDiffEndpoint(
    TasksDbContext db,
    ILocalRepoGitOps gitOps,
    ICurrentUser currentUser)
    : EndpointWithoutRequest<CodeChangeSnapshotDiffResponse>
{
    public override void Configure()
    {
        Get("/api/tasks/{taskId:ulid}/code-changes/{snapshotId:ulid}");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var taskId = Ulid.Parse(Route<string>("taskId"));
        var snapshotId = Ulid.Parse(Route<string>("snapshotId"));

        var snapshot = await db.CodeChangeSnapshots
            .AsNoTracking()
            .FirstOrDefaultAsync(s =>
                s.Id == snapshotId && s.TaskId == taskId && s.OrgId == currentUser.OrgId, ct);
        if (snapshot is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var task = await db.Tasks.AsNoTracking()
            .Where(t => t.Id == taskId)
            .Select(t => new { Owner = t.RunnerUserId ?? t.CreatedByUserId, t.ReviewBaseSha })
            .FirstOrDefaultAsync(ct);

        var diff = await ReadDiffAsync(gitOps, snapshot.OrgId, snapshot.RepoId, taskId, task?.Owner,
            snapshot.IsLive, snapshot.BaseBranch, snapshot.BranchName, task?.ReviewBaseSha, ct);

        var text = diff.IsSuccess ? diff.Value ?? "" : "";
        await Send.OkAsync(new CodeChangeSnapshotDiffResponse(
            snapshot.Id, snapshot.IsLive ? snapshot.UpdatedAt : snapshot.CreatedAt,
            snapshot.BranchName, snapshot.BaseBranch,
            IsTruncated: text.Length >= LiveDiffMaxChars,
            Diff: text,
            IsLive: snapshot.IsLive,
            Revision: snapshot.Revision,
            CurrentBranch: !snapshot.IsLive,
            Unavailable: diff.IsSuccess ? null : UnavailableReason(diff.Error)), ct);
    }

    /// <summary>The relay's cap on a diff read live from a runner.</summary>
    internal const int LiveDiffMaxChars = 400_000;

    /// <summary>
    /// A snapshot's diff, live (the worktree as it is) or finished. A Story member's is its own change — its review base
    /// (<c>ReviewBaseSha</c>, where it started on the shared branch) up to the branch — not the
    /// whole shared branch against the default branch, which carried every earlier sub-task's
    /// work into the next one's "Code changes" (found by the e2e harness). Everything else is the
    /// branch against its base, as before.
    /// </summary>
    internal static Task<Orkka.SharedKernel.Utilities.Result<string>> ReadDiffAsync(
        ILocalRepoGitOps gitOps, Ulid orgId, Ulid repoId, Ulid taskId, string? owner,
        bool isLive, string baseBranch, string branchName, string? reviewBaseSha, CancellationToken ct)
        => isLive
            ? gitOps.GetWorkingTreeDiffAsync(orgId, repoId, taskId, owner, baseBranch, reviewBaseSha, ct)
            : gitOps.GetDiffAsync(orgId, repoId, taskId, owner, baseBranch, branchName, reviewBaseSha, ct);

    internal static string UnavailableReason(string? error) =>
        "The diff is read from the developer's machine and Orkka keeps no copy of it. "
        + (string.IsNullOrWhiteSpace(error) ? "It could not be read right now." : $"It could not be read right now: {error}");
}
