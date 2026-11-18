namespace Orkka.Modules.Execution.Orchestration;

/// <summary>
/// Per-phase budgets for the context sections injected into agent prompts. Inlining file
/// contents the agent can read itself (Read/Glob/Grep) is double-spend on input tokens, so
/// the PM-review and planning phases default to a paths-only listing. Context documents live
/// in the Documents module (DB), which agents cannot reach with repo tools — they are only
/// ever budgeted, never reduced to paths.
/// </summary>
public sealed class ExecutionContextOptions
{
    public const string SectionName = "ExecutionContext";

    /// <summary>Char budget for context documents in the PM-review phase. Briefs and specs
    /// are the PM's most relevant context, so they stay inline but capped.</summary>
    public int PmReviewMaxDocChars { get; set; } = 20_000;

    /// <summary>When true, PM review lists linked context files as paths for the agent to
    /// read with its tools instead of inlining their contents.</summary>
    public bool PmReviewFilePathsOnly { get; set; } = true;

    /// <summary>Char budget for context documents in the planning phase.</summary>
    public int PlanningMaxDocChars { get; set; } = 50_000;

    /// <summary>When true, planning lists linked context files as paths — the planning agent
    /// explores the repo with Read/Glob/Grep anyway.</summary>
    public bool PlanningFilePathsOnly { get; set; } = true;

    /// <summary>Char budget for inlined context files when a phase does inline them.</summary>
    public int MaxFileChars { get; set; } = 50_000;

    /// <summary>Char budget for context documents in the developing phase.</summary>
    public int DevelopingMaxDocChars { get; set; } = 50_000;

    /// <summary>When true, developing lists linked context files as paths (mandatory reads)
    /// instead of inlining up to 50K chars the agent can Read itself. There is no
    /// re-discovery risk: the paths are listed explicitly and the approved plan's
    /// filesChanged map came from real exploration. Config-revertible.</summary>
    public bool DevelopingFilePathsOnly { get; set; } = true;

    /// <summary>
    /// When true, context documents are written to .orkka/context/doc-*.md in the task
    /// worktree and the prompt carries only a path list — instead of re-inlining the same
    /// document bytes in the PM-review, planning, AND developing phases. Falls back to
    /// inlining when the workspace can't take files (old local runner, no worktree).
    /// </summary>
    public bool DocsAsFiles { get; set; }

    /// <summary>
    /// When true, the QA and code-review phases write the task diff to
    /// .orkka/context/diff.patch instead of inlining it (paid twice per task otherwise).
    /// Same fallback rules as <see cref="DocsAsFiles"/>.
    /// </summary>
    public bool DiffAsFile { get; set; }

    /// <summary>
    /// When true, the developing phase writes the approved plan to
    /// .orkka/context/plan.json instead of inlining it (re-sent on every dev cycle
    /// otherwise). Same fallback rules as <see cref="DocsAsFiles"/>.
    /// </summary>
    public bool PlanAsFile { get; set; }

    /// <summary>
    /// When true, a Developing re-entry (QA fail, review changes-requested, verification
    /// feedback, answered question) resumes the previous Developing run's claude session
    /// instead of cold-booting with the full context re-injected. The resumed prompt sends
    /// only the feedback newer than the previous run; a failed resume falls back to the
    /// full cold prompt. Claude only — other providers always run the cold path.
    /// </summary>
    public bool EnableDevelopSessionResume { get; set; } = true;
}
