namespace Orkka.Modules.Execution.Agents;

/// <summary>
/// The turn / tool-call / wall-clock ceiling for one kind of agent run. All three are
/// applied per run: <see cref="MaxTurns"/> rides on the <see cref="AgentRequest"/>, and
/// <see cref="MaxToolCalls"/>/<see cref="MaxWallClockSeconds"/> override the process-wide
/// <see cref="AgentLimitsOptions"/> for that run. Values ≤ 0 disable the corresponding cap.
/// </summary>
public sealed class PhaseBudget
{
    public int MaxTurns { get; set; }
    public int MaxToolCalls { get; set; }
    public int MaxWallClockSeconds { get; set; }

    /// <summary>
    /// Vendor-cost ceiling for one run of this phase, in USD, enforced by RunCostGuard inside an
    /// in-process Claude run. 0 (the shipped default) disables it, and in the local-only product
    /// it has no effect on pipeline runs: they execute on the developer's runner, and the runner
    /// protocol carries no per-run ceiling. The user-facing limit is the project's opt-in task
    /// spend limit (ProjectSettingsDto.TaskSpendLimitUsd — estimated list-price spend across
    /// every run of a task), checked by the orchestrator before each phase starts. Either one
    /// tripping is a pause-and-ask, never a kill and never a "wrap up and call it done": the
    /// orchestrator parks the task in Pending Questions asking the requester to continue or stop
    /// (TaskOrchestrator.PauseForSpendLimitAsync). A flat number here fires on legitimate work —
    /// arm it only as an emergency brake, e.g. "PhaseBudgets:Developing:MaxCostUsd": 25.
    /// </summary>
    public decimal MaxCostUsd { get; set; }
}

/// <summary>
/// Per-phase execution budgets for board (pipeline) task runs, resolved by the
/// <c>TaskOrchestrator</c> and carried on each <see cref="AgentRequest"/>.
///
/// Turn and tool-call counts are NOT capped for pipeline work (both default to 0 =
/// disabled). They were, and they were the wrong knob: an agent that has read 40 files
/// isn't runaway, it's working, and killing it there throws away the entire run's spend
/// and leaves the task failed with nothing to show ("Agent run reached the 40 tool-call
/// limit and was stopped"). Every fired cap was a legitimate run — the counts scale with
/// repo size and task scope, which no fixed number can predict. The agent decides how many
/// tools a task needs; it stops on its own when the work is done.
///
/// Wall-clock is the one hard stop that remains, and it is the honest one: it bounds cost
/// and terminates a genuinely stuck run (hung command, tool loop) without punishing depth.
/// The values below are ceilings, not allocations — an agent that finishes in two minutes
/// costs two minutes — so they are set generously, sized to the heaviest legitimate run of
/// each phase rather than the typical one.
///
/// Bound via configuration section "PhaseBudgets"; the defaults below are the shipped
/// values and can be tuned per environment (e.g. "PhaseBudgets:Developing:MaxWallClockSeconds")
/// with no redeploy. Setting a non-zero MaxTurns/MaxToolCalls there re-arms that cap.
/// </summary>
public sealed class PhaseBudgetsOptions
{
    public const string SectionName = "PhaseBudgets";

    /// <summary>PM refine (Ready lane): read + a little clarification, no code. Reading a
    /// large repo to judge whether a task is well-specified is normal here — this phase is
    /// what the old 40 tool-call cap killed most.</summary>
    public PhaseBudget PmRefine { get; set; } = new()
    { MaxTurns = 0, MaxToolCalls = 0, MaxWallClockSeconds = 1800 };

    /// <summary>Planning: read the repo and produce a plan; no writes.</summary>
    public PhaseBudget Planning { get; set; } = new()
    { MaxTurns = 0, MaxToolCalls = 0, MaxWallClockSeconds = 2400 };

    /// <summary>Developing an existing codebase: implement the plan, iterate, run commands.</summary>
    public PhaseBudget Developing { get; set; } = new()
    { MaxTurns = 0, MaxToolCalls = 0, MaxWallClockSeconds = 5400 };

    /// <summary>QA/testing: start the app and drive it through the Playwright MCP browser.
    /// Browser driving is tool-call heavy by nature — one call per click, read, screenshot.</summary>
    public PhaseBudget Testing { get; set; } = new()
    { MaxTurns = 0, MaxToolCalls = 0, MaxWallClockSeconds = 3600 };

    /// <summary>Code review: read the diff and leave comments; no writes. Large PRs are
    /// paged through .orkka/context/diff.patch one Read per page, so the count scales with
    /// diff size — 20 turns killed real reviews (WAV-19).</summary>
    public PhaseBudget Review { get; set; } = new()
    { MaxTurns = 0, MaxToolCalls = 0, MaxWallClockSeconds = 3600 };

    /// <summary>Merge-conflict resolution: a scoped edit over the conflicting hunks.</summary>
    public PhaseBudget MergeResolve { get; set; } = new()
    { MaxTurns = 0, MaxToolCalls = 0, MaxWallClockSeconds = 1800 };
}
