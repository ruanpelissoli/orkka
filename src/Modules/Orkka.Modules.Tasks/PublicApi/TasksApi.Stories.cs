namespace Orkka.Modules.Tasks.PublicApi;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using TaskStatus = Orkka.Modules.Tasks.Contracts.TaskStatus;

/// <summary>
/// Tracker Stories: a <see cref="TaskGroup"/> of <see cref="TaskGroupKind.Story"/> whose members
/// are the Story's sub-tasks, worked strictly in order on ONE shared branch per repository. Each
/// member depends on the one before it (met at Completed — same group — or when the blocker is
/// archived), so it plans and builds on the code its predecessor already pushed.
/// </summary>
public sealed partial class TasksApi
{
    public async Task<TrackerStoryResult> CreateTrackerStoryAsync(TrackerStorySpec spec, CancellationToken ct = default)
    {
        var existing = await _db.TaskGroups.IgnoreQueryFilters()
            .FirstOrDefaultAsync(g => g.OrgId == spec.OrgId && g.ProjectId == spec.ProjectId
                && g.Kind == TaskGroupKind.Story && g.ExternalKey == spec.StoryKey, ct);
        if (existing is not null)
            return new TrackerStoryResult(existing.Id, await MemberRefsAsync(existing.Id, ct), Created: false);

        var group = new TaskGroup
        {
            Id = Ulid.NewUlid(),
            OrgId = spec.OrgId,
            ProjectId = spec.ProjectId,
            Title = TrimTitle(spec.Title),
            Request = spec.Description is { Length: > 10000 } long_ ? long_[..10000] : spec.Description,
            Kind = TaskGroupKind.Story,
            BranchName = spec.BranchName,
            ExternalKey = spec.StoryKey,
            ExternalUrl = spec.StoryUrl,
            Status = TaskGroupStatus.Active,
            // Members land one by one on the shared branch; the forge pull request is the joint
            // landing, so the cross-repo gate has nothing to hold.
            MergePolicy = TaskGroupMergePolicy.Independent,
            CreatedByUserId = spec.OwnerUserId,
        };
        _db.TaskGroups.Add(group);

        var created = new List<TaskEntity>();
        var refs = await AddMembersAsync(group, spec.OwnerUserId, spec.Epic, spec.SubTasks, previous: null, startOrder: 1, created, ct);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A concurrent report of the same Story won — hand back the winner.
            _db.ChangeTracker.Clear();
            var winner = await _db.TaskGroups.IgnoreQueryFilters().AsNoTracking()
                .FirstAsync(g => g.OrgId == spec.OrgId && g.ProjectId == spec.ProjectId
                    && g.Kind == TaskGroupKind.Story && g.ExternalKey == spec.StoryKey, ct);
            return new TrackerStoryResult(winner.Id, await MemberRefsAsync(winner.Id, ct), Created: false);
        }

        await BroadcastCreatedAsync(spec.ProjectId, created, ct);
        logger.LogInformation("Story {Key}: group {GroupId} with {Count} member(s) on {Branch}",
            spec.StoryKey, group.Id, refs.Count, spec.BranchName);
        return new TrackerStoryResult(group.Id, refs, Created: true);
    }

    public async Task<StoryAppendResult> AppendStoryMemberAsync(
        Ulid groupId, StorySubTaskSpec subTask, CancellationToken ct = default)
    {
        var group = await _db.TaskGroups.IgnoreQueryFilters()
            .Include(g => g.Branches)
            .FirstOrDefaultAsync(g => g.Id == groupId && g.Kind == TaskGroupKind.Story, ct);
        if (group is null)
            return new StoryAppendResult([], false, Closed: true);

        var members = await _db.Tasks.IgnoreQueryFilters()
            .Where(t => t.GroupId == groupId)
            .ToListAsync(ct);
        var already = members.Where(m => string.Equals(m.ExternalKey, subTask.Key, StringComparison.OrdinalIgnoreCase)).ToList();
        if (already.Count > 0)
            return new StoryAppendResult(
                [.. already.Select(m => new StoryMemberRef(m.Id, subTask.Key, m.RepoId, m.StoryOrder ?? 0, false))],
                false, false);

        var prFinished = group.SettledAt is not null
            || group.Branches.Any(b => b.ForgePrUrl is not null && b.ForgeState is "merged" or "closed");
        if (prFinished)
            return new StoryAppendResult([], false, Closed: true);

        var previous = members.Where(m => !m.IsArchived).OrderByDescending(m => m.StoryOrder ?? 0).FirstOrDefault();
        var nextOrder = (members.Max(m => m.StoryOrder) ?? 0) + 1;
        var owner = group.CreatedByUserId ?? previous?.RunnerUserId ?? "";
        var epic = members.FirstOrDefault()?.ExternalEpic;

        var created = new List<TaskEntity>();
        var refs = await AddMembersAsync(group, owner, epic, [subTask], previous, nextOrder, created, ct);

        // A linked folder of a mixed story was squash-merged when the story was first readied
        // (its row: no forge pull request, state "merged"). A late member in that folder brings
        // new commits, so the end of the story must squash the shared branch into it again —
        // without this the folder never received the late sub-task's work.
        var lateRepos = refs.Where(r => r.Created && r.RepoId is not null).Select(r => r.RepoId!.Value).ToHashSet();
        foreach (var squashed in group.Branches.Where(b => b.ForgePrUrl is null && b.ForgeState == "merged" && lateRepos.Contains(b.RepoId)))
            squashed.ForgeState = null;

        var wasReady = group.ReadyAt is not null;
        if (wasReady)
        {
            // Moves never go backwards in the tracker, but the pull request does: it is not done
            // until the new member lands too.
            group.ReadyAt = null;
            group.Status = TaskGroupStatus.Active;
            foreach (var branch in group.Branches)
            {
                branch.ReadyAt = null;
                if (branch.ForgePrUrl is not null)
                    branch.ForgePrDraft = true;
            }
        }

        await _db.SaveChangesAsync(ct);
        await BroadcastCreatedAsync(group.ProjectId, created, ct);
        return new StoryAppendResult(refs, wasReady, false);
    }

    /// <summary>Adds one member per sub-task and repository, in order, each depending on the one
    /// before; repository rows for the shared branch are created as repositories appear.</summary>
    private async Task<List<StoryMemberRef>> AddMembersAsync(
        TaskGroup group, string ownerUserId, string? epic, IReadOnlyList<StorySubTaskSpec> subTasks,
        TaskEntity? previous, int startOrder, List<TaskEntity> created, CancellationToken ct)
    {
        var slots = subTasks.SelectMany(s => s.RepoIds.Count == 0
            ? [(Sub: s, Repo: (Ulid?)null, Index: 0)]
            : s.RepoIds.Distinct().Select((r, i) => (Sub: s, Repo: (Ulid?)r, Index: i))).ToList();
        if (slots.Count == 0)
            return [];

        var refs = new List<StoryMemberRef>();
        var externalRefs = slots.Select(s => SlotRef(s.Sub, s.Repo, s.Index)).ToList();
        var existingByRef = await _db.Tasks.IgnoreQueryFilters()
            .Where(t => t.OrgId == group.OrgId && t.ProjectId == group.ProjectId
                && t.ExternalRef != null && externalRefs.Contains(t.ExternalRef))
            .ToDictionaryAsync(t => t.ExternalRef!, ct);

        var newCount = slots.Count(s => !existingByRef.ContainsKey(SlotRef(s.Sub, s.Repo, s.Index)));
        // ReserveRangeAsync returns the LAST number of the range [last - count + 1, last].
        var number = newCount > 0 ? await taskNumbers.ReserveRangeAsync(group.ProjectId, newCount, ct) - newCount + 1 : 0;
        var order = startOrder;
        var knownRepos = group.Branches.Select(b => b.RepoId).ToHashSet();

        foreach (var (sub, repo, index) in slots)
        {
            var externalRef = SlotRef(sub, repo, index);
            TaskEntity member;
            var isNew = false;
            if (existingByRef.TryGetValue(externalRef, out var adopt))
            {
                // Imported on its own earlier (before its Story was the developer's). Untouched
                // work joins the story; anything already started stays standalone.
                if (adopt.GroupId is not null || !NotStarted(adopt))
                    continue;
                member = adopt;
                member.GroupId = group.Id;
                member.BranchName = group.BranchName;
                member.StoryOrder = order;
            }
            else
            {
                member = new TaskEntity
                {
                    Id = Ulid.NewUlid(),
                    OrgId = group.OrgId,
                    ProjectId = group.ProjectId,
                    Number = number++,
                    RepoId = repo,
                    GroupId = group.Id,
                    StoryOrder = order,
                    BranchName = group.BranchName,
                    Title = TrimTitle(sub.Title),
                    Description = sub.Description,
                    Priority = sub.Priority,
                    Type = sub.Type,
                    CurrentLane = Lane.Drafting,
                    Status = TaskStatus.Draft,
                    Tags = JsonSerializer.Serialize(sub.Tags),
                    ExternalRef = externalRef,
                    ExternalKey = sub.Key,
                    ExternalUrl = sub.Url,
                    ExternalEpic = epic,
                    WorkingSubpath = sub.Subpath,
                    CreatedByUserId = ownerUserId,
                    RunnerUserId = ownerUserId,
                };
                _db.Tasks.Add(member);
                created.Add(member);
                isNew = true;
            }

            if (previous is not null)
            {
                _db.TaskDependencies.Add(new TaskDependency
                {
                    Id = Ulid.NewUlid(),
                    OrgId = group.OrgId,
                    TaskId = member.Id,
                    DependsOnTaskId = previous.Id,
                    Reason = "Story order: builds on the previous sub-task's code on the shared branch.",
                    Source = DependencySource.Manual,
                });
            }

            if (repo is { } repoId && group.BranchName is { } branch && knownRepos.Add(repoId))
            {
                var row = new GroupBranch
                {
                    Id = Ulid.NewUlid(), OrgId = group.OrgId, GroupId = group.Id, RepoId = repoId, BranchName = branch,
                };
                group.Branches.Add(row);
                _db.GroupBranches.Add(row);
            }

            refs.Add(new StoryMemberRef(member.Id, sub.Key, repo, order, isNew));
            previous = member;
            order++;
        }

        return refs;

        static string SlotRef(StorySubTaskSpec sub, Ulid? repo, int index)
            => index == 0 || repo is null ? sub.ExternalRef : $"{sub.ExternalRef}@{repo}";
    }

    private async Task<List<StoryMemberRef>> MemberRefsAsync(Ulid groupId, CancellationToken ct)
        => [.. (await _db.Tasks.IgnoreQueryFilters().AsNoTracking()
                .Where(t => t.GroupId == groupId)
                .OrderBy(t => t.StoryOrder)
                .Select(t => new { t.Id, t.ExternalKey, t.RepoId, t.StoryOrder })
                .ToListAsync(ct))
            .Select(t => new StoryMemberRef(t.Id, t.ExternalKey ?? "", t.RepoId, t.StoryOrder ?? 0, false))];

    private async Task BroadcastCreatedAsync(Ulid projectId, IReadOnlyList<TaskEntity> created, CancellationToken ct)
    {
        if (created.Count == 0)
            return;
        try
        {
            var project = await projectsApi.GetProjectSummaryAsync(projectId, ct);
            foreach (var task in created)
            {
                var repo = task.RepoId is { } rid ? await projectsApi.GetRepoAsync(rid, ct) : null;
                await taskNotifier.SendTaskCreatedAsync(projectId, new TaskCreatedEvent(
                    task.Id, task.Number, $"{project!.Key}-{task.Number}", task.Title,
                    task.Priority.ToString(), task.Type.ToString(), task.CurrentLane.ToString(), task.Status.ToString(),
                    task.RepoId, repo?.Name, repo?.CardColor,
                    JsonSerializer.Deserialize<List<string>>(task.Tags ?? "[]") ?? [], task.CreatedAt));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Board broadcast failed for story members in project {ProjectId}", projectId);
        }
    }

    public async Task<IReadOnlyList<TrackerGroupState>> GetTrackerGroupStatesAsync(
        IReadOnlyCollection<Ulid> groupIds, CancellationToken ct = default)
    {
        if (groupIds.Count == 0)
            return [];

        var groups = await _db.TaskGroups.IgnoreQueryFilters().AsNoTracking()
            .Include(g => g.Branches)
            .Where(g => groupIds.Contains(g.Id))
            .ToListAsync(ct);
        var members = await MemberStatesAsync([.. groups.Select(g => g.Id)], ct);

        return [.. groups.Select(g => new TrackerGroupState(
            g.Id, g.Kind, g.ExternalKey, g.BranchName, g.ReadyAt, g.SettledAt is not null && g.Status == TaskGroupStatus.Merged,
            g.PendingChildCount,
            members.TryGetValue(g.Id, out var list) ? list : [],
            [.. g.Branches.OrderBy(b => b.CreatedAt).Select(ToBranchState)]))];
    }

    private async Task<Dictionary<Ulid, List<StoryMemberState>>> MemberStatesAsync(
        IReadOnlyCollection<Ulid> groupIds, CancellationToken ct)
    {
        // One query per group: no collection parameter over the converted Ulid? column (see
        // FindTaskIdByBranchAsync), and a sync touches a handful of stories at most.
        var rows = new List<MemberRow>();
        foreach (var groupId in groupIds.Distinct())
            rows.AddRange(await _db.Tasks.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.GroupId == groupId)
            .Select(t => new MemberRow
            {
                GroupId = groupId,
                Id = t.Id,
                ExternalKey = t.ExternalKey,
                StoryOrder = t.StoryOrder,
                RepoId = t.RepoId,
                Number = t.Number,
                Title = t.Title,
                CurrentLane = t.CurrentLane,
                Status = t.Status,
                IsArchived = t.IsArchived,
                IsMerged = t.IsMerged,
                Planning = t.StartedAt != null || t.HasAgentWorking
                    || (t.CurrentLane != Lane.Drafting && t.CurrentLane != Lane.Ready),
                Developing = t.CurrentLane == Lane.Developing || t.CurrentLane == Lane.Testing
                    || t.CurrentLane == Lane.CodeReview || t.CurrentLane == Lane.Done
                    || t.Executions.Any(e => e.Lane == Lane.Developing),
                InReview = t.CurrentLane == Lane.CodeReview || t.CurrentLane == Lane.Done
                    || t.Executions.Any(e => e.Lane == Lane.CodeReview),
            })
            .ToListAsync(ct));

        return rows.GroupBy(r => r.GroupId).ToDictionary(
            g => g.Key,
            g => g.OrderBy(r => r.StoryOrder ?? int.MaxValue).ThenBy(r => r.Number)
                .Select(r => new StoryMemberState(
                    r.Id, r.ExternalKey, r.StoryOrder ?? 0, r.RepoId,
                    r.Planning, r.Developing, r.InReview, r.IsMerged, r.IsArchived,
                    r.Number, r.Title, r.CurrentLane.ToString(), r.Status.ToString()))
                .ToList());
    }

    private sealed class MemberRow
    {
        public Ulid GroupId { get; init; }
        public Ulid Id { get; init; }
        public string? ExternalKey { get; init; }
        public int? StoryOrder { get; init; }
        public Ulid? RepoId { get; init; }
        public int Number { get; init; }
        public string Title { get; init; } = "";
        public Lane CurrentLane { get; init; }
        public TaskStatus Status { get; init; }
        public bool IsArchived { get; init; }
        public bool IsMerged { get; init; }
        public bool Planning { get; init; }
        public bool Developing { get; init; }
        public bool InReview { get; init; }
    }

    private static StoryBranchState ToBranchState(GroupBranch b)
        => new(b.RepoId, b.BranchName, b.ForgePrUrl, b.ForgePrDraft, b.ReadyAt, b.ForgeState);

    public async Task SetStoryPendingChildrenAsync(Ulid groupId, int count, CancellationToken ct = default)
    {
        var group = await _db.TaskGroups.IgnoreQueryFilters().FirstOrDefaultAsync(g => g.Id == groupId, ct);
        if (group is null || group.PendingChildCount == count)
            return;
        group.PendingChildCount = count;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<StoryContext?> GetStoryForTaskAsync(Ulid taskId, CancellationToken ct = default)
    {
        var groupId = await _db.Tasks.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.Id == taskId)
            .Select(t => t.GroupId)
            .FirstOrDefaultAsync(ct);
        return groupId is { } gid ? await GetStoryAsync(gid, ct) : null;
    }

    public async Task<StoryContext?> GetStoryAsync(Ulid groupId, CancellationToken ct = default)
    {
        var group = await _db.TaskGroups.IgnoreQueryFilters().AsNoTracking()
            .Include(g => g.Branches)
            .FirstOrDefaultAsync(g => g.Id == groupId && g.Kind == TaskGroupKind.Story, ct);
        if (group is null)
            return null;

        var tasks = await _db.Tasks.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.GroupId == groupId)
            .OrderBy(t => t.StoryOrder).ThenBy(t => t.Number)
            .Select(t => new
            {
                t.Id, t.Number, t.Title, t.StoryOrder, t.RepoId, t.Status, t.IsMerged, t.IsArchived, t.ReviewBaseSha,
                t.ExternalKey, t.ExternalUrl, RunnerUserId = t.RunnerUserId ?? t.CreatedByUserId,
            })
            .ToListAsync(ct);
        var ids = tasks.Select(t => t.Id).ToList();

        var plans = (await _db.TaskPlans.IgnoreQueryFilters().AsNoTracking()
                .Where(p => ids.Contains(p.TaskId) && p.Status == PlanStatus.Approved)
                .Select(p => new { p.TaskId, p.Version, p.Content })
                .ToListAsync(ct))
            .GroupBy(p => p.TaskId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.Version).First().Content?.Summary);

        // The newest committed (non-live) snapshot per member: paths and counts only, no code.
        var snapshots = (await _db.CodeChangeSnapshots.IgnoreQueryFilters().AsNoTracking()
                .Where(s => ids.Contains(s.TaskId) && !s.IsLive)
                .Select(s => new { s.TaskId, s.CreatedAt, s.FilesJson })
                .ToListAsync(ct))
            .GroupBy(s => s.TaskId)
            .ToDictionary(g => g.Key, g => ParseSnapshotFiles(g.OrderByDescending(s => s.CreatedAt).First().FilesJson));

        return new StoryContext(
            group.Id, group.OrgId, group.ProjectId, group.Title, group.Request,
            group.ExternalKey, group.ExternalUrl, group.BranchName ?? "",
            group.ReadyAt, group.SettledAt is not null && group.Status == TaskGroupStatus.Merged,
            group.PendingChildCount,
            [.. tasks.Select(t => new StoryMemberContext(
                t.Id, t.Number, t.Title, t.StoryOrder ?? 0, t.RepoId, t.Status, t.IsMerged, t.IsArchived,
                t.ReviewBaseSha, plans.GetValueOrDefault(t.Id),
                snapshots.TryGetValue(t.Id, out var files) ? files : [],
                t.ExternalKey, t.ExternalUrl, t.RunnerUserId))],
            [.. group.Branches.OrderBy(b => b.CreatedAt).Select(ToBranchState)]);
    }

    private static List<StoryChangedFile> ParseSnapshotFiles(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            return [.. doc.RootElement.EnumerateArray()
                .Where(e => e.TryGetProperty("path", out _))
                .Select(e => new StoryChangedFile(
                    e.GetProperty("path").GetString() ?? "",
                    e.TryGetProperty("additions", out var a) && a.TryGetInt32(out var ai) ? ai : 0,
                    e.TryGetProperty("deletions", out var d) && d.TryGetInt32(out var di) ? di : 0))];
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return [];
        }
    }

    public async Task SetGroupBranchPullRequestAsync(
        Ulid groupId, Ulid repoId, string branchName, string? forgePrUrl, bool draft, CancellationToken ct = default)
    {
        var group = await _db.TaskGroups.IgnoreQueryFilters().Include(g => g.Branches)
            .FirstOrDefaultAsync(g => g.Id == groupId, ct);
        if (group is null)
            return;

        var row = group.Branches.FirstOrDefault(b => b.RepoId == repoId);
        if (row is null)
        {
            row = new GroupBranch { Id = Ulid.NewUlid(), OrgId = group.OrgId, GroupId = groupId, RepoId = repoId, BranchName = branchName };
            _db.GroupBranches.Add(row);
        }
        if (forgePrUrl is not null)
        {
            row.ForgePrUrl = forgePrUrl;
            row.ForgePrDraft = draft;
            row.ForgeState ??= "open";
        }
        await _db.SaveChangesAsync(ct);
    }

    public async Task SetStoryReadyAsync(Ulid groupId, DateTimeOffset? readyAt, CancellationToken ct = default)
    {
        var group = await _db.TaskGroups.IgnoreQueryFilters().Include(g => g.Branches)
            .FirstOrDefaultAsync(g => g.Id == groupId, ct);
        if (group is null)
            return;

        group.ReadyAt = readyAt;
        if (group.Status is TaskGroupStatus.Active or TaskGroupStatus.AwaitingMerge)
            group.Status = readyAt is null ? TaskGroupStatus.Active : TaskGroupStatus.AwaitingMerge;
        foreach (var branch in group.Branches)
        {
            branch.ReadyAt = readyAt;
            if (branch.ForgePrUrl is not null)
                branch.ForgePrDraft = readyAt is null;
        }
        await _db.SaveChangesAsync(ct);
    }

    public async Task SettleStoryAsync(Ulid groupId, CancellationToken ct = default)
    {
        var group = await _db.TaskGroups.IgnoreQueryFilters().FirstOrDefaultAsync(g => g.Id == groupId, ct);
        if (group is null || group.Status == TaskGroupStatus.Merged)
            return;
        var now = DateTimeOffset.UtcNow;
        group.Status = TaskGroupStatus.Merged;
        group.SettledAt = now;
        group.ReadyAt ??= now;
        await _db.SaveChangesAsync(ct);
    }

    public async Task SetGroupBranchForgeStateAsync(Ulid groupId, Ulid repoId, string state, CancellationToken ct = default)
    {
        var row = await _db.GroupBranches.IgnoreQueryFilters()
            .FirstOrDefaultAsync(b => b.GroupId == groupId && b.RepoId == repoId, ct);
        if (row is null || row.ForgeState == state)
            return;
        row.ForgeState = state;
        await _db.SaveChangesAsync(ct);

        // Every story pull request merged: the story is settled.
        var rows = await _db.GroupBranches.IgnoreQueryFilters().AsNoTracking()
            .Where(b => b.GroupId == groupId && b.ForgePrUrl != null)
            .Select(b => b.ForgeState)
            .ToListAsync(ct);
        if (rows.Count > 0 && rows.All(s => s == "merged"))
            await SettleStoryAsync(groupId, ct);
    }

    public async Task SetTaskReviewBaseShaAsync(Ulid taskId, string sha, CancellationToken ct = default)
    {
        var task = await _db.Tasks.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null || task.ReviewBaseSha is not null)
            return;
        task.ReviewBaseSha = sha;
        await _db.SaveChangesAsync(ct);
    }
}
