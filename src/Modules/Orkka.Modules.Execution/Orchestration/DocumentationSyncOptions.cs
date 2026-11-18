namespace Orkka.Modules.Execution.Orchestration;

/// <summary>
/// Sizing for the post-completion documentation pass, bound from configuration section
/// "DocumentationSync" so each limit can be tuned per environment without a redeploy.
///
/// Every value here exists to keep the pass proportional to what it is worth. The pass runs on
/// every completed task of an opted-in project, so its floor — one triage call over metadata —
/// is what most tasks will ever cost; the larger budgets apply only once triage has decided
/// there is genuinely something to write.
/// </summary>
public sealed class DocumentationSyncOptions
{
    public const string SectionName = "DocumentationSync";

    /// <summary>How far back the sweep looks for settled tasks. This is what stops a project
    /// that has just enabled documentation from firing a model call for every task it has ever
    /// finished; work older than this is simply left alone.</summary>
    public int LookbackHours { get; set; } = 48;

    /// <summary>Seconds between sweeps. Documentation is never urgent — the task is already
    /// done — so this is deliberately slower than the 5s queue poll.</summary>
    public int SweepIntervalSeconds { get; set; } = 60;

    /// <summary>Tasks started per sweep, so one enabling project cannot monopolise a cycle.</summary>
    public int MaxTasksPerSweep { get; set; } = 5;

    /// <summary>Runs attempted before a task's pass is left alone. Bounds the cost of a task
    /// whose content the model reliably chokes on.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>How long a claimed pass may sit untouched before another worker may take it
    /// over. Nothing else ever moves a row out of Running, so without this a worker that died
    /// mid-pass would hide its task from documentation permanently.</summary>
    public int AbandonedAfterMinutes { get; set; } = 30;

    /// <summary>Existing documents one task may revise. A task that appears to invalidate the
    /// entire library is far more likely to be a triage misfire than a genuine sweep.</summary>
    public int MaxUpdatesPerTask { get; set; } = 3;

    // ── Input budgets ───────────────────────────────────────────────────────

    public int MaxDescriptionChars { get; set; } = 4_000;
    public int MaxPlanChars { get; set; } = 4_000;
    public int MaxExistingDocChars { get; set; } = 30_000;
    public int MaxFilePaths { get; set; } = 60;

    // ── Output budgets ──────────────────────────────────────────────────────

    /// <summary>Triage answers with a short JSON verdict and nothing else.</summary>
    public int TriageMaxTokens { get; set; } = 800;

    /// <summary>The write pass returns whole documents, so it needs real room.</summary>
    public int WriteMaxTokens { get; set; } = 16_000;
}
