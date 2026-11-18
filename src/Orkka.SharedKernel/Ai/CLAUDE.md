# SharedKernel/Ai — prompt fragments

## Purpose
Platform-owned prompt text that more than one module must agree on. Modules reference these
constants instead of re-typing the wording, so a rule cannot drift between the copy an agent
is seeded with and the copy it is handed at run time.

## SharedPromptBlocks — defaults and protocols, placed before the team's instructions
There is one fragment family. (Security is not a prompt fragment: it is enforced by the
sanitized environment, the Bash guard and the worktree boundary hook — see SECURITY.md.)

- **`SharedPromptBlocks`** — behaviour defaults and text protocols. `InteractiveQuestions` is a
  format contract (the fenced `questions` block the UI renders as option cards).
  `CodeCommentPolicy`, `EngineeringBar` and `VisualBar` are *defaults*, not boundaries: they
  are placed **before** `## Additional instructions from your team`, and their last line says
  team instructions override them. "Add explanatory comments to every function" on the Team
  page must win.

## The three bars
| Block | Marker | Audience (runtime predicate) | Long-form companion |
|---|---|---|---|
| `CodeCommentPolicy` | `COMMENTS:` | `IsCodeWritingRole` | — |
| `EngineeringBar` | `YOUR ENGINEERING BAR` | `IsCodeWritingRole` | `code-craft`, `frontend-craft`, `testing-craft` skills |
| `VisualBar` | `YOUR VISUAL BAR` | `IsUiBuildingRole` | `ui-craft` skill |

Each is short on purpose (pinned by `DefaultPromptsTests`: comment policy ≤ 600 chars,
engineering bar ≤ 1,600, visual bar ≤ 1,400) and points at a built-in skill the agent reads
from disk when it matters — the bar is the non-negotiables, the skill is the playbook.

## CodeCommentPolicy / EngineeringBar specifics
- Two consumers, one constant: `DefaultPrompts` concatenates them into the seven code-writing
  personas at compile time (new agents), and `TaskOrchestrator.BuildPersonaSection` injects them
  at run time for agents already in the database or created with a hand-written persona.
- The markers (`"COMMENTS:"`, `"YOUR ENGINEERING BAR"`) are how the runtime side detects a
  persona that already carries the block, so nobody pays for two copies. Changing a marker
  without changing the block's opening words silently doubles the text in every code-writing
  prompt.
- The two exceptions (repo/lint conventions, genuine "why") are load-bearing, not hedging. A
  blanket ban makes agents strip `///` XML docs and JSDoc out of repos whose conventions — or
  lint rules — require them.

## Gotchas
- These blocks ride in **every** run of their phase. `DefaultPromptsTests` pins
  `CodeCommentPolicy` at <= 600 chars / <= 6 lines for exactly that reason; if you need more
  words, you probably want the persona or a skill instead.
- The assembled system prompt reaches `claude.exe` as a command-line argument, and Windows
  caps the whole line near 32K (`CreateProcess` error 206). Growth here is multiplied by the
  persona (12k) and memory (4k) budgets that already live in that space.
- Raw-string constants start with a blank line by convention so callers can concatenate
  without managing separators.
