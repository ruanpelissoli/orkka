namespace Orkka.Modules.Tasks.Infrastructure.Services;

using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Infrastructure;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>The ordering chain of a Story's members (see TasksApi.Stories).</summary>
internal static class StoryChain
{
    /// <summary>
    /// The routing answer for a Story member named several repositories: exactly as intake does
    /// for a sub-task placed in several repositories, it becomes one member per repository in
    /// story order. The answered member takes the first; one new member per further repository
    /// is inserted right after it (same sub-task, <c>{ExternalRef}@{repo}</c>, the Story's
    /// branch), the later members move down, the chain is re-linked through the new members, and
    /// every repository gets its <c>group_branches</c> row. Tracked; the caller saves. Returns
    /// the new members.
    /// </summary>
    public static async Task<List<TaskEntity>> SpanReposAsync(
        TasksDbContext db, ITaskNumberService numbers, TaskEntity member, IReadOnlyList<Ulid> repoIds, CancellationToken ct)
    {
        if (member.GroupId is not { } groupId || member.StoryOrder is not { } order || repoIds.Count == 0)
            return [];
        var group = await db.TaskGroups.IgnoreQueryFilters().Include(g => g.Branches)
            .FirstOrDefaultAsync(g => g.Id == groupId && g.Kind == TaskGroupKind.Story, ct);
        if (group is null)
            return [];

        member.RepoId = repoIds[0];
        var extra = repoIds.Skip(1).Distinct().Where(r => r != repoIds[0]).ToList();

        var created = new List<TaskEntity>();
        if (extra.Count > 0)
        {
            // Make room after the answered member.
            var later = await db.Tasks.IgnoreQueryFilters()
                .Where(t => t.GroupId == groupId && t.StoryOrder > order)
                .ToListAsync(ct);
            foreach (var t in later)
                t.StoryOrder += extra.Count;

            // Whatever waited on the answered member waits on the last new one instead.
            var dependents = await db.TaskDependencies.IgnoreQueryFilters()
                .Where(d => d.DependsOnTaskId == member.Id && d.Task.GroupId == groupId)
                .ToListAsync(ct);

            var last = await numbers.ReserveRangeAsync(member.ProjectId, extra.Count, ct);
            var number = last - extra.Count + 1;
            var previous = member;
            foreach (var repoId in extra)
            {
                var next = new TaskEntity
                {
                    Id = Ulid.NewUlid(),
                    OrgId = member.OrgId,
                    ProjectId = member.ProjectId,
                    MilestoneId = member.MilestoneId,
                    Number = number++,
                    RepoId = repoId,
                    GroupId = groupId,
                    StoryOrder = previous.StoryOrder + 1,
                    BranchName = group.BranchName ?? member.BranchName,
                    Title = member.Title,
                    Description = member.Description,
                    Priority = member.Priority,
                    Type = member.Type,
                    CurrentLane = Lane.Drafting,
                    Status = TaskStatus.Draft,
                    Tags = member.Tags,
                    ExternalRef = member.ExternalRef is { } externalRef ? $"{externalRef}@{repoId}" : null,
                    ExternalKey = member.ExternalKey,
                    ExternalUrl = member.ExternalUrl,
                    ExternalEpic = member.ExternalEpic,
                    WorkingSubpath = member.WorkingSubpath,
                    CreatedByUserId = member.CreatedByUserId,
                    RunnerUserId = member.RunnerUserId,
                };
                db.Tasks.Add(next);
                db.TaskDependencies.Add(new TaskDependency
                {
                    Id = Ulid.NewUlid(),
                    OrgId = member.OrgId,
                    TaskId = next.Id,
                    DependsOnTaskId = previous.Id,
                    Reason = "Story order: builds on the previous sub-task's code on the shared branch.",
                    Source = DependencySource.Manual,
                });
                created.Add(next);
                previous = next;
            }

            foreach (var edge in dependents)
            {
                db.TaskDependencies.Remove(edge);
                db.TaskDependencies.Add(new TaskDependency
                {
                    Id = Ulid.NewUlid(),
                    OrgId = edge.OrgId,
                    TaskId = edge.TaskId,
                    DependsOnTaskId = previous.Id,
                    Reason = edge.Reason,
                    Source = edge.Source,
                });
            }
        }

        if (group.BranchName is { } branch)
        {
            foreach (var repoId in repoIds.Distinct().Where(r => group.Branches.All(b => b.RepoId != r)))
            {
                var row = new GroupBranch { Id = Ulid.NewUlid(), OrgId = group.OrgId, GroupId = groupId, RepoId = repoId, BranchName = branch };
                group.Branches.Add(row);
                db.GroupBranches.Add(row);
            }
        }

        return created;
    }

    /// <summary>
    /// A Story member leaving the chain (archived, retired): whatever waited on it now waits on
    /// ITS predecessor instead, so the order still holds — an archived blocker counts as met, and
    /// without the rewire the next member would jump the queue. Tracked; the caller saves.
    /// </summary>
    public static async Task RewireDependentsAsync(TasksDbContext db, TaskEntity member, CancellationToken ct)
    {
        if (member.GroupId is not { } groupId || member.StoryOrder is null)
            return;
        var isStory = await db.TaskGroups.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(g => g.Id == groupId && g.Kind == TaskGroupKind.Story, ct);
        if (!isStory)
            return;

        var predecessorIds = await db.TaskDependencies.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.TaskId == member.Id && d.DependsOnTask.GroupId == groupId)
            .Select(d => d.DependsOnTaskId)
            .ToListAsync(ct);
        var dependents = await db.TaskDependencies.IgnoreQueryFilters()
            .Where(d => d.DependsOnTaskId == member.Id && d.Task.GroupId == groupId)
            .ToListAsync(ct);

        foreach (var edge in dependents)
        {
            db.TaskDependencies.Remove(edge);
            foreach (var predecessor in predecessorIds)
            {
                var exists = await db.TaskDependencies.IgnoreQueryFilters()
                    .AnyAsync(d => d.TaskId == edge.TaskId && d.DependsOnTaskId == predecessor, ct);
                if (!exists && predecessor != edge.TaskId)
                    db.TaskDependencies.Add(new TaskDependency
                    {
                        Id = Ulid.NewUlid(),
                        OrgId = edge.OrgId,
                        TaskId = edge.TaskId,
                        DependsOnTaskId = predecessor,
                        Reason = edge.Reason,
                        Source = edge.Source,
                    });
            }
        }
    }
}
