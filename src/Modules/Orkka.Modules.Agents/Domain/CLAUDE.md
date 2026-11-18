# Agents/Domain — personas and seeding

## Purpose
`DefaultPrompts` holds one persona per `AgentRole`; `AgentSeeder` stamps
`DefaultPrompts.ForRole(role)` into `ProjectAgent.SystemPrompt` when a project's pod is created
(always the full roster — there is one kind of project).

## The trap: personas are a database column, not a constant
`SystemPrompt` is written **once, at seed time**. Editing a constant here changes what *future*
agents are seeded with and nothing else — every project that already exists keeps the persona it
was created with, and a user can edit theirs on the Team page besides.

So a rule that must apply to every agent, everywhere, cannot live here alone. It belongs in a
runtime injection (`TaskOrchestrator`), which reaches existing agents and user-created custom
agents with no data migration and no risk of overwriting an edited persona. `DefaultPrompts`
then carries the same text as well, so newly seeded agents look right on the Team page and in
chat. That is exactly how the code-comment policy, the engineering bar and the visual bar are
wired: `SharedPromptBlocks.{CodeCommentPolicy,EngineeringBar,VisualBar}` are concatenated into
the seeded personas *and* injected at run time when absent (marker-detected).

## Skills are the other half of the bar
The bars are deliberately short; the playbooks live in `SkillSeeder.BuiltIns` (`code-craft`,
`frontend-craft`, `testing-craft` for the dev roles, `engineering-review` and `design-review`
for the reviewer, `ui-craft`/`brand-consistency`/`ux-direction` for UI). Built-ins carry a
stable `Key`, and `SkillBackfillService` tops existing projects up at startup and attaches each
to its `DefaultRoles` — so a new built-in reaches every project with no migration, unlike a
persona edit.

## Business rules
- Security is not prompt text: the sanitized environment, `AgentBashGuard` and the worktree
  boundary hook enforce it at run time (SECURITY.md). The DBA persona's "no database access, no
  credentials" line describes that reality; it is not what enforces it.
- Shared text is appended by compile-time const concatenation
  (`""" … """ + SharedPromptBlocks.X`), which keeps the personas usable as `[InlineData]`
  arguments in the existing tests.
- Which roles carry the code-comment rule is decided by `AgentRoleExtensions.IsCodeWritingRole`
  (dev roles + DevOps), and `DefaultPromptsTests` asserts the personas and that predicate agree.
  Add a role to one without the other and agents get the rule twice or not at all.
- `CodeReviewer` carries the mirror-image clause (flag unclear code, not uncommented code) so
  the reviewer does not ask for the comments the dev agent was told not to write — and a
  *Design* checklist item pointing at the `engineering-review` skill, whose Blocking list is
  what the orchestrator's review prompt ("What blocks") lets bounce a task.
- Every dev persona (FrontendDev included) carries a `YOUR TESTS` block; the QA lane reports an
  uncovered change as a failure, so the agent that builds it must be told to cover it.

## Gotchas
- `CreateAgentValidator` caps `SystemPrompt` at 12k — a default must never exceed what a user
  could save. `ForRole_PersonasFitTheSystemPromptCap` guards it.
- `{AgentName}` is substituted at prompt-assembly time, not at seed time.
- Personas are prose the model reads, but the tests pin specific phrases (`YOUR TESTS`,
  `Integration-test`, the comment-policy marker). Rewording is fine; deleting a pinned
  guarantee should break a test, and does.
