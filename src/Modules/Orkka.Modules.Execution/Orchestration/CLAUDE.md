# Execution/Orchestration — prompt assembly

## Purpose
`TaskOrchestrator` runs the task pipeline and assembles the system prompt for every phase.
`BuildPersonaSection` is the single head of all of them; the phase-specific bodies live in
`Build{Planning,Developing,Qa,…}Prompt` and `TaskOrchestrator.InteractivePrompts.cs`.

## Order is the design
The prompt is assembled persona → (engineering bar, code-comment policy, visual bar) → team
instructions → memories → phase assignment + response format → phase notices (read-only
phase, refused tools, autonomy) → `ClaudeMdGuidelines` (planning, developing) → locale
(`AgentLanguage.Instruction`).

- **`SharedPromptBlocks.{EngineeringBar,CodeCommentPolicy,VisualBar}` go before the team
  instructions** — they are defaults, not boundaries. "Add explanatory comments to every
  function" on the Team page has to beat them, both by position and by each block's own
  closing sentence.
- **Everything after the persona is orchestrator-owned**: the phase assignment, the response
  contract (plan JSON, review verdict + "What blocks", QA report) and the notices are written
  here, not in `DefaultPrompts`, so they reach agents seeded before they existed and cannot be
  edited away on the Team page.
- **There is no boundary-prompt family.** Nothing platform-authored is appended after the
  user's text to out-recency it; security never depended on prompt text: the sanitized environment, `AgentBashGuard` and the worktree
  boundary hook are the enforcement (SECURITY.md).

## Where the quality bar is enforced, phase by phase
- **Planning** (`BuildPlanningPrompt`) asks the plan for *Existing code to reuse*, *Design*,
  *Testing strategy* and *Risks / refactor-first* sections — the plan is where duplication and
  wrong-layer placement are cheapest to catch.
- **Developing** (`BuildDevelopingPrompt`) points at the craft skills, demands a `git diff`
  self-review and the lint/typecheck/test commands in the final message, and — when the repo
  has a `CheckCommand` — says the platform will run it. The **check gate**
  (`RunDevelopingCheckAsync`, called in `RunDevelopingPhaseAsync` right after the empty-diff
  check and before the PR) then actually runs it: non-zero exit → the output goes into the
  conversation as a `QaReport` and `ITasksApi.ApplyCheckFailureAsync` sends the task back to
  Developing (`Worker:MaxCheckCycles`, default 2; then `WaitingUserVerification`). "Could not
  run" (no command, old runner, `Worker:CheckTimeoutSeconds` elapsed) fails OPEN, exactly like
  the merge build check — a stronger signal when present, never a new precondition.
- **Code review** (`BuildCodeReviewSystemPrompt`) carries the orchestrator-owned "What blocks"
  list: the objective engineering defects that justify `request_changes`, mirrored from the
  `engineering-review` skill. Orchestrator-owned so it reaches reviewers seeded before it
  existed and cannot drift from the dev agent's bar.

## The code-comment injection contract
- Opt-in per call site: `BuildPersonaSection(agent, codeWritingPhase: true)`. Planning,
  developing and the interactive turn opt in; QA, code review, Ready/PM and the interactive
  *finish* prompt (commit/push only) do not.
- The parameter defaults to `false` so a new call site fails safe — a missing rule, never a
  code-writing rule leaking into a review prompt.
- Guarded by `agent.Role.IsCodeWritingRole()` and by a marker check
  (`SharedPromptBlocks.CodeCommentPolicyMarker` / `EngineeringBarMarker`) against the
  **already-truncated** persona, so a freshly seeded agent that carries the rule in its stored
  prompt never gets a second copy. The engineering bar is injected first, then the comment
  policy, then (UI roles only) the visual bar.
- Nothing here writes to `ProjectAgent.SystemPrompt`. That is deliberate: a user-edited persona
  is structurally impossible to overwrite. The flip side — a user who deletes the rule from
  their persona gets the injected copy back — is intended; team instructions are the control.

## Gotchas
- The assembled system prompt is passed to `claude.exe` as a command-line argument and Windows
  caps the whole line near 32K (`CreateProcess` error 206). That is why `PersonaCharCap` (12k),
  `UserInstructionsCharCap` (4k) and `MemoriesSectionCharBudget` (4k) exist, and why anything
  added to every prompt has to justify its bytes. Task context, plans and diffs ride in the
  user message over stdin and are not subject to this.
- The `agent is null` fallback personas are platform-authored one-liners; they get no injection
  because there is no stored persona and no team instructions to reconcile with.
- Untrusted content (task text, conversation, context files) is fenced with `PromptSafety.Wrap`
  in the **user** message, never spliced into the system prompt.
