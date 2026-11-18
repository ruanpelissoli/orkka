namespace Orkka.Modules.Tasks.Features.Milestones;

using FastEndpoints;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Features.Tasks;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.SharedKernel.Security;

public sealed class PlanMilestoneValidator : Validator<PlanMilestoneRequest>
{
    public PlanMilestoneValidator()
    {
        // Required only when planning something new. Planning INTO a milestone already has a
        // goal — the milestone's own outcome — and asking the user to retype it into a
        // textarea would create a second, divergent copy of the thing they already wrote.
        RuleFor(x => x.Goal)
            .NotEmpty().WithMessage("Describe what you want to build.")
            .When(x => x.MilestoneId is null);

        RuleFor(x => x.Goal)
            .MaximumLength(2000).WithMessage("Goal must be 2000 characters or fewer.");
    }
}

/// <summary>
/// Queues a planning run and returns immediately — 202 with a job id, nothing planned yet.
///
/// The propose/accept split is the point: each proposed task costs real money to run, so the
/// user edits and prunes the plan before anything is created. Generalizes the decomposer that
/// previously only ever fired once, during managed provisioning.
///
/// The generation itself used to run right here, on the request thread. It measured 90s–210s
/// in production against the requester's own runner, and it was bound to
/// <c>HttpContext.RequestAborted</c>: a user who navigated away after three minutes of a
/// spinner tore down the fetch, which cancelled the relay mid-generation and surfaced as a
/// 500 for work the org had already paid for. Nothing in the chain was timing out — the
/// person was. So the run moved to <see cref="MilestonePlanQueueProcessor"/>, off any request
/// thread, and the only thing left here is the validation that must answer synchronously:
/// permission, a project that exists, a milestone that exists, a repo that belongs to it.
/// </summary>
public sealed class PlanMilestoneEndpoint(
    TasksDbContext db,
    IProjectResolver projectResolver,
    IProjectsApi projectsApi,
    ICurrentUser currentUser,
    ITaskNotifier taskNotifier,
    ILogger<PlanMilestoneEndpoint> logger)
    : Endpoint<PlanMilestoneRequest, MilestonePlanJobResponse>
{
    public override void Configure()
    {
        Post("/api/projects/{projectId}/milestones/plan");
    }

    public override async Task HandleAsync(PlanMilestoneRequest req, CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.TasksCreate))
            return;

        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // Planning into an existing milestone: its outcome is the goal. Resolved here rather
        // than inside the decomposer so an unknown id is a 404 instead of a planning failure.
        Milestone? target = null;
        if (req.MilestoneId is { } targetId)
        {
            target = await db.Milestones
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == targetId && m.OrgId == currentUser.OrgId
                    && m.ProjectId == projectId, ct);

            if (target is null)
            {
                await Send.NotFoundAsync(ct);
                return;
            }
        }

        var goal = string.IsNullOrWhiteSpace(req.Goal)
            ? GoalFromMilestone(target!)
            : req.Goal.Trim();

        // No repo means the model plans from the goal alone — worse, but not an error. An
        // explicitly wrong repo is an error.
        var repoId = req.RepoId;
        if (repoId is { } requested)
        {
            var repo = await projectsApi.GetRepoAsync(requested, ct);
            if (repo is null || repo.ProjectId != projectId || repo.OrgId != currentUser.OrgId)
            {
                ThrowError("The specified repository does not belong to this project.");
                return;
            }
        }
        else
        {
            // The milestone's own repo first. Falling straight to the project's first repo
            // would plan an API milestone against the storefront's tree on any multi-repo
            // project — confidently, and against files that are not there.
            repoId = target?.RepoId
                ?? (await projectsApi.ListReposAsync(projectId, ct)).FirstOrDefault()?.Id;
        }

        // A second click, a double-submitted form, or a refresh that re-fires the request must
        // not queue a second generation — each one is a paid model call, and the two would
        // race to hand the user two different plans for the same milestone. Re-attach to the
        // run already in flight instead. Scoped to this requester because the run bills them
        // and executes on their runner.
        var inFlightQuery = db.MilestonePlanJobs
            .AsNoTracking()
            .Where(j => j.OrgId == currentUser.OrgId
                && j.ProjectId == projectId
                && j.RequesterUserId == currentUser.UserId
                && (j.Status == MilestonePlanStatus.Pending
                    || j.Status == MilestonePlanStatus.Running));

        // Split rather than `j.MilestoneId == req.MilestoneId`. Planning a NEW milestone is the
        // null case and it is the common one, so it must not rest on how a provider chooses to
        // compare a null parameter to a null column — spelled out, it reads the same in SQL as
        // it does here.
        inFlightQuery = req.MilestoneId is { } scoped
            ? inFlightQuery.Where(j => j.MilestoneId == scoped)
            : inFlightQuery.Where(j => j.MilestoneId == null);

        var inFlight = await inFlightQuery
            .OrderByDescending(j => j.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (inFlight is not null)
        {
            logger.LogInformation(
                "Re-attaching to in-flight milestone plan {JobId} for project {ProjectId}",
                inFlight.Id, projectId);
            await Send.ResponseAsync(inFlight.ToResponse(null), 202, ct);
            return;
        }

        var job = new MilestonePlanJob
        {
            // Explicit id: the SaveChanges interceptor assigns ids too late for the response
            // and the notification below, both of which need it in this method.
            Id = Ulid.NewUlid(),
            OrgId = currentUser.OrgId,
            ProjectId = projectId,
            MilestoneId = req.MilestoneId,
            RepoId = repoId,
            // The derived prose, not the raw request field: for a milestone-scoped plan the
            // goal IS the milestone, and re-deriving it at execution time would let a rename
            // between enqueue and pickup change what was asked for.
            Goal = goal,
            RequesterUserId = currentUser.UserId,
            RequesterLocale = currentUser.Locale,
            CurrentStep = MilestonePlanSteps.Queued,
        };

        db.MilestonePlanJobs.Add(job);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Queued milestone plan {JobId} for project {ProjectId} (milestone {MilestoneId})",
            job.Id, projectId, req.MilestoneId);

        // Announced immediately so a second tab watching this project shows the run starting,
        // not just the tab that clicked.
        await taskNotifier.SendMilestonePlanChangedAsync(
            projectId, MilestonePlanNotifications.From(job));

        // 202, not 200: nothing has been planned yet. The client follows the job over SignalR
        // and re-fetches it on reconnect.
        await Send.ResponseAsync(job.ToResponse(null), 202, ct);
    }

    /// <summary>
    /// A milestone with no outcome is just a label, so its title has to carry the whole goal.
    /// Says "this milestone only" out loud because the planner also receives the rest of the
    /// roadmap as context and would otherwise be free to plan into any of it.
    /// </summary>
    private static string GoalFromMilestone(Milestone milestone) =>
        string.IsNullOrWhiteSpace(milestone.Outcome)
            ? $"Plan the tasks for the milestone \"{milestone.Title}\", and only that milestone."
            : $"""
              Plan the tasks for this milestone, and only this milestone.

              ## {milestone.Title}

              {milestone.Outcome}
              """;
}

/// <summary>
/// Persists an edited proposal: one milestone plus its tasks, in a single transaction.
/// The client may have removed or rewritten anything before sending it back.
/// </summary>
public sealed class AcceptMilestonePlanEndpoint(
    TasksDbContext db,
    IProjectResolver projectResolver,
    IProjectsApi projectsApi,
    ITaskNumberService taskNumbers,
    ICurrentUser currentUser,
    ITaskNotifier taskNotifier,
    Orkka.Modules.Runners.Contracts.IRunnerCoverageResolver coverageResolver,
    Orkka.Modules.Agents.Contracts.IAgentsApi agentsApi,
    ITasksApi tasksApi,
    ILogger<AcceptMilestonePlanEndpoint> logger)
    : Endpoint<AcceptMilestonePlanRequest, MilestoneResponse>
{
    public override void Configure()
    {
        Post("/api/projects/{projectId}/milestones/accept");
    }

    public override async Task HandleAsync(AcceptMilestonePlanRequest req, CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.TasksCreate))
            return;

        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // ResolveAsync passes a well-formed ULID through without checking it exists.
        var project = await projectsApi.GetProjectSummaryAsync(projectId, ct);
        if (project is null || project.OrgId != currentUser.OrgId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (req.Tasks is not { Count: > 0 })
        {
            ThrowError("A plan needs at least one task.");
            return;
        }

        var repo = req.RepoId is { } requestedRepoId
            ? await projectsApi.GetRepoAsync(requestedRepoId, ct)
            : null;

        if (req.RepoId is not null && (repo is null || repo.ProjectId != projectId
            || repo.OrgId != currentUser.OrgId))
        {
            ThrowError("The specified repository does not belong to this project.");
            return;
        }

        // Per-task repos, matched by name like the PM chat tool does. Without this every task in
        // a plan took one repo, so a multi-repo project filed its Go API work against the mobile
        // app — and the task then ran an agent in the wrong worktree.
        var projectRepos = (await projectsApi.ListReposAsync(projectId, ct))
            .Where(r => r.OrgId == currentUser.OrgId)
            .ToList();

        if (projectRepos.Count == 0)
        {
            ThrowError("This project has no repositories yet — tasks need one. Link a repo first.");
            return;
        }

        var reposByName = projectRepos
            .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // The milestone is resolved BEFORE the repo default, because on a multi-repo project
        // its own repo is the right answer and the project's first is merely the last resort.
        // The default used to be computed first, so planning into an existing milestone filed
        // every unmatched task against the project's first repo instead of the milestone's.
        Milestone? existing = null;
        if (req.MilestoneId is { } existingId)
        {
            existing = await db.Milestones
                .FirstOrDefaultAsync(m => m.Id == existingId && m.OrgId == currentUser.OrgId
                    && m.ProjectId == projectId, ct);

            if (existing is null)
            {
                await Send.NotFoundAsync(ct);
                return;
            }
        }

        // A task with no repo cannot be submitted — SubmitTask refuses it, and there is no repo
        // picker on the task detail page to fix it afterwards. So the default is resolved here
        // rather than left null: the caller's choice, else the milestone's, else the project's
        // only sensible fallback.
        if (repo is null && existing?.RepoId is { } milestoneRepoId)
            repo = await projectsApi.GetRepoAsync(milestoneRepoId, ct);

        var defaultRepo = repo ?? projectRepos[0];

        // A repo name the model invented is caught here, while the user can still fix the
        // plan — a silent fallback filed the task in the wrong tree and the agent then
        // planned against code that isn't there.
        var unknownNames = req.Tasks
            .Select(t => t.Repo)
            .Where(name => !string.IsNullOrWhiteSpace(name) && !reposByName.ContainsKey(name!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (unknownNames.Count > 0)
        {
            ThrowError(
                $"The plan names repositories this project doesn't have: "
                + $"{string.Join(", ", unknownNames)}. Valid repos: "
                + $"{string.Join(", ", projectRepos.Select(r => r.Name))}. "
                + "Edit the plan's tasks (or re-plan) and accept again.");
            return;
        }

        // Accepted tasks run on the accepting user's runner, like the create endpoint and the PM
        // chat path. The dev agent — and so the model — can differ by repo, so coverage is read
        // per repo and a provider their runner has not signed in is said on each task.
        var decisionsByRepo = new Dictionary<Ulid, Orkka.Modules.Runners.Contracts.RunnerCoverageDecision>();
        foreach (var plannedRepoId in req.Tasks.Select(t => ResolveRepo(t.Repo, reposByName, defaultRepo).Id).Distinct())
        {
            decisionsByRepo[plannedRepoId] = await coverageResolver.ResolveAsync(
                await Tasks.DevModelLookup.QueryAsync(agentsApi, currentUser.OrgId, currentUser.UserId,
                    projectId, plannedRepoId, null, null, project.Settings?.DefaultModel, ct), ct);
        }

        // The numbers first, on their own: the counter lives on the projects table and SQLite
        // has one writer, so reserving inside the transaction below would wait on it. A plan
        // that fails to save leaves a gap in the numbering, which is fine.
        var nextNumber = req.Tasks.Count == 0
            ? 0
            : await taskNumbers.ReserveRangeAsync(projectId, req.Tasks.Count, ct) - req.Tasks.Count + 1;

        // One transaction for the milestone and every task: a half-created plan is worse than none.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        Milestone milestone;
        if (existing is not null)
        {
            milestone = existing;
        }
        else
        {
            milestone = new Milestone
            {
                // Explicit id: the SaveChanges interceptor assigns ids too late, so the tasks
                // below would take Ulid.Empty as their MilestoneId and violate the FK.
                Id = Ulid.NewUlid(),
                OrgId = currentUser.OrgId,
                ProjectId = projectId,
                // Scoped to wherever its tasks landed — a new milestone whose work is all in
                // one repo belongs to that repo, and spans repos otherwise.
                RepoId = SingleRepoOrNull(req.Tasks, reposByName, defaultRepo),
                Title = req.MilestoneTitle.Trim(),
                Outcome = string.IsNullOrWhiteSpace(req.Outcome) ? null : req.Outcome.Trim(),
                Position = await MilestoneQueries.NextPositionAsync(db, currentUser.OrgId, projectId, ct),
                CreatedByUserId = currentUser.UserId,
            };
            db.Milestones.Add(milestone);
        }

        // Cross-repo groups: only for contract keys that STILL have two or more tasks left in
        // two or more repositories after the user's edits. A key whose other half was deleted
        // would otherwise produce a group of one, whose merge gate waits forever on a sibling
        // that will never exist.
        var groupsByContractKey = new Dictionary<int, TaskGroup>();
        foreach (var contract in req.Contracts ?? [])
        {
            var members = req.Tasks.Where(t => t.ContractKey == contract.Key).ToList();
            if (members.Count < 2)
                continue;

            var distinctRepos = members
                .Select(t => ResolveRepo(t.Repo, reposByName, defaultRepo).Id)
                .Distinct()
                .Count();

            if (distinctRepos < 2)
                continue;

            var group = new TaskGroup
            {
                // Explicit id: the interceptor assigns ids at save time, too late for the tasks
                // below to reference it.
                Id = Ulid.NewUlid(),
                OrgId = currentUser.OrgId,
                ProjectId = projectId,
                MilestoneId = milestone.Id,
                Title = members[0].Title,
                Contract = contract.Contract.Trim(),
                ContractSource = ContractSource.Declared,
                // Already planned, already split — there is nothing left for the split pass to do.
                Status = TaskGroupStatus.Active,
                CreatedByUserId = currentUser.UserId,
            };
            db.TaskGroups.Add(group);
            groupsByContractKey[contract.Key] = group;
        }

        // Key -> created task, so the planner's ordering can be rebuilt after the user has
        // pruned the proposal. Keys the user removed simply never appear, and edges naming
        // them are dropped below.
        var byKey = new Dictionary<int, TaskEntity>(req.Tasks.Count);
        // Every task that was created, in plan order. Distinct from byKey on purpose: keys are
        // not guaranteed unique (a client that never assigned them sends 0 for all), and
        // anything that must touch EVERY task — the billed-run notes below — cannot iterate a
        // map that keeps one task per key.
        var createdTasks = new List<TaskEntity>(req.Tasks.Count);

        foreach (var planned in req.Tasks)
        {
            var number = nextNumber++;
            var entity = new TaskEntity
            {
                // Explicit id for the same reason as the milestone above: the interceptor
                // assigns ids at save time, so a second entity with a default key throws
                // "same key already tracked".
                Id = Ulid.NewUlid(),
                OrgId = currentUser.OrgId,
                ProjectId = projectId,
                MilestoneId = milestone.Id,
                GroupId = planned.ContractKey is { } key
                    && groupsByContractKey.TryGetValue(key, out var group)
                        ? group.Id
                        : null,
                RepoId = ResolveRepo(planned.Repo, reposByName, defaultRepo).Id,
                Number = number,
                Title = planned.Title,
                Description = planned.Description,
                Priority = Enum.TryParse<Priority>(planned.Priority, ignoreCase: true, out var p)
                    ? p : Priority.Medium,
                Type = Enum.TryParse<TaskType>(planned.Type, ignoreCase: true, out var ty)
                    ? ty : TaskType.Feature,
                // Lands in Drafting like any human-written task. Accepting a plan is not the
                // same as approving the spend to run it — submitting each task still is.
                CurrentLane = Lane.Drafting,
                Status = Contracts.TaskStatus.Draft,
                CreatedByUserId = currentUser.UserId,
                RequesterLocale = currentUser.Locale,
                RunnerUserId = currentUser.UserId,
            };

            db.Tasks.Add(entity);
            byKey[planned.Key] = entity;
            createdTasks.Add(entity);
        }

        // The planner's build order, persisted as real edges. These tasks land in Drafting and
        // will each pass through submission — where the AI analyzer also runs — but that only
        // sees whatever exists at the moment each one is submitted. Recording the order now,
        // from the plan that knew it, is what stops the whole milestone starting at once.
        //
        // A gated group is ONE unit of that order. A member's work only lands when every
        // member has finished and the group merges, and a dependency on a member is met only
        // by that merge (TaskDependencyQueries.IsUnmet). So an edge from a member of an EARLIER
        // group to a member of a LATER group deadlocks: the earlier group cannot merge until
        // its member runs, that member waits on the later group's merge, and the later group's
        // members wait — by the same build order — on the earlier group. Prod, 2026-09-15:
        // three API+UI pairs with every UI task "after" every API task, and nothing could
        // start. Units rank by their lowest key, and an edge may only stay inside a unit or
        // point from a higher-ranked unit to a lower-ranked one; the rest are dropped. That
        // keeps the order the planner meant — group by group — and is acyclic by construction,
        // as the lower-keys-only rule below already was for single tasks.
        var plannedByKey = req.Tasks.GroupBy(t => t.Key).ToDictionary(g => g.Key, g => g.First());
        int? GroupKeyOf(PlannedTaskResponse t) =>
            t.ContractKey is { } ck && groupsByContractKey.ContainsKey(ck) ? ck : null;
        int UnitRank(PlannedTaskResponse t) =>
            GroupKeyOf(t) is { } ck ? req.Tasks.Where(o => o.ContractKey == ck).Min(o => o.Key) : t.Key;

        foreach (var planned in req.Tasks)
        {
            foreach (var dependsOnKey in planned.DependsOn ?? [])
            {
                // Lower keys only: acyclic by construction rather than by detection. Unknown
                // keys are tasks the user removed from the proposal.
                if (dependsOnKey >= planned.Key
                    || !byKey.TryGetValue(dependsOnKey, out var dependsOn))
                    continue;

                var dependsOnPlanned = plannedByKey[dependsOnKey];
                var sameUnit = GroupKeyOf(planned) is { } unit && GroupKeyOf(dependsOnPlanned) == unit;
                if (!sameUnit && UnitRank(planned) <= UnitRank(dependsOnPlanned))
                {
                    logger.LogInformation(
                        "Plan for milestone {MilestoneId}: dropped build-order edge {Key} -> {DependsOnKey} — it points from a gated group to a later one and would deadlock the merge gate",
                        milestone.Id, planned.Key, dependsOnKey);
                    continue;
                }

                db.TaskDependencies.Add(new TaskDependency
                {
                    Id = Ulid.NewUlid(),
                    OrgId = currentUser.OrgId,
                    TaskId = byKey[planned.Key].Id,
                    DependsOnTaskId = dependsOn.Id,
                    Reason = "Planned build order — this task depends on the earlier one.",
                    // Manual, not Ai: the analyzer clears and rewrites its own rows on every
                    // run and would drop these on the first re-analysis.
                    Source = DependencySource.Manual,
                });
            }
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        logger.LogInformation(
            "Accepted plan: milestone {MilestoneId} with {Count} task(s) in project {ProjectId}",
            milestone.Id, req.Tasks.Count, projectId);

        foreach (var created in createdTasks)
            await Tasks.DevModelLookup.AnnounceAsync(tasksApi, created.Id, decisionsByRepo[created.RepoId!.Value], ct);

        var counts = (await MilestoneQueries.GetCountsByMilestoneAsync(
            db, currentUser.OrgId, projectId, ct)).For(milestone.Id);
        var response = milestone.ToResponse(counts);

        await taskNotifier.SendMilestoneChangedAsync(projectId,
            MilestoneNotifications.Changed(milestone.Id, response.Status, counts));

        await Send.OkAsync(response, ct);
    }

    /// <summary>Repo a planned task names, or the plan's default when it named none. Unknown
    /// names never reach here — the accept handler rejects them up front, while the user can
    /// still fix the plan.</summary>
    private static Orkka.Modules.Projects.Contracts.RepoSummary ResolveRepo(
        string? repoName,
        IReadOnlyDictionary<string, Orkka.Modules.Projects.Contracts.RepoSummary> byName,
        Orkka.Modules.Projects.Contracts.RepoSummary fallback) =>
        !string.IsNullOrWhiteSpace(repoName) && byName.TryGetValue(repoName, out var match)
            ? match
            : fallback;

    /// <summary>The milestone's repo when every task resolves to the same one, else null.
    /// Labelling a cross-repo milestone with one repo would be worse than leaving it unset.</summary>
    private static Ulid? SingleRepoOrNull(
        IReadOnlyList<PlannedTaskResponse> tasks,
        IReadOnlyDictionary<string, Orkka.Modules.Projects.Contracts.RepoSummary> byName,
        Orkka.Modules.Projects.Contracts.RepoSummary fallback)
    {
        var ids = tasks.Select(t => ResolveRepo(t.Repo, byName, fallback).Id).Distinct().ToList();
        return ids.Count == 1 ? ids[0] : null;
    }
}
