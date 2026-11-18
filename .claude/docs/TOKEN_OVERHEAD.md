# Token Overhead — Measurement & Baseline Methodology

On the user's runner (run by `./orkka start`), Orkka drives coding-agent CLIs with the **user's
own subscription**. The product
constraint: a task or chat turn through Orkka must not burn meaningfully more subscription
tokens than the same work typed directly into the terminal. This doc defines how to measure
that overhead so every optimization has a before/after number.

## Instruments

1. **Per-task, per-execution numbers** — usage records carry `TaskId` + `ExecutionId` and
   input/output/cache-read/cache-write tokens, so grouping a task's usage rows (`usage.usage_records`) by
   `ExecutionId` (joined to the execution's lane) gives one row per phase run. A QA-fail or
   review-changes cycle shows up as a second Developing row, so loop cost is directly visible.
   Rows with a `TaskId` and no `ExecutionId` are task-scoped work outside a phase
   (merge-conflict runs). Runner-served rows record tokens with a $0 cost, so count tokens,
   not dollars.
2. **Top-tasks breakdown** — `GET /api/usage/breakdown?groupBy=task` (also `model`,
   `agent`, `project`).
3. **Prompt-section size logs** — `[PromptSize]` structured logs:
   - Orkka.Local: one Information line per phase run (`TaskOrchestrator.LogPromptSections`) with
     char counts for system, user, memories, contextFiles, docs, plan, diff, conversation.
   - Chat: one Debug line per turn (`SendMessageEndpoint`) with sent vs fresh prompt size —
     the fresh−sent delta is what session resume saved that turn.

## Baseline: "bare CLI" cost of the same task

1. Pick a **reference task** and freeze it: fixed repo, fixed title + description,
   one linked context document, QA lane enabled with a change that fails QA once
   (forces a second Developing cycle).
2. Run it through the full pipeline on a runner. Record per-lane input/output tokens from
   the task's usage rows (instrument 1).
3. In a fresh worktree of the same repo/branch, run the bare equivalent once:
   `claude -p "<task title>. <task description>"` and take input/output tokens from the
   CLI result message (`/cost` or the result JSON in `-p --output-format json`).
4. **Overhead** = (sum of Orkka per-lane input tokens) − (bare CLI input tokens),
   with cache reads weighted at the provider's discount (Anthropic: 10% of input price).
   Compare output tokens too, but input dominates — output is mostly genuine work.

Orkka legitimately spends more than one bare run (PM review, planning, QA, review are
extra runs a terminal user wouldn't do). The target is that **each phase's prompt overhead**
on top of its own bare-run equivalent stays small, and that **loops/follow-ups do not
re-pay full context** (session resume, context-as-files).

## Per-chat-turn baseline

Scripted 6-turn conversation with one agent; per-turn `InputTokens` from usage records
(`TaskId == null`, matching AgentId). Turn 2+ of a resumable provider should cost roughly
(new message + slim system prompt), not (full persona + static blocks + history).

## Quality gates while reducing context

- Plan / QA verdict / review verdict JSON parse rates unchanged.
- Review comments still cite valid `path:line`.
- The planted QA failure is still caught.
- A context-file-dependent task still succeeds with paths-only rendering.
