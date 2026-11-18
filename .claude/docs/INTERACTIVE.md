# INTERACTIVE.md — Interactive Development

A single live chat that does real work on a real branch. The user talks to a dev agent, watches
the diff build up, approves the commands that matter, and finishes with a PR that enters the
normal Code Review lane.

It exists for the work the pipeline is bad at: a 15-minute bugfix is not worth drafting a task,
waiting on PM refinement, approving a plan, and sitting through QA.

### Discuss vs an interactive session

Both are a chat with an agent about a task; they differ in whether anything changes.

- **Discuss** (the task's Discuss tab, AGENTS.md → Task chat) talks ABOUT a task the pipeline is
  working: ask about the ticket, have the agent analyze the repo, shape the plan, work out the
  answer to its questions. It is **read-only** — a separate checkout of the task branch, no edits,
  no commands, no lane change — and the pipeline keeps the task. What you agree on goes back in
  with "Apply to task": it answers PendingQuestions, requests plan changes in PlanReady, or becomes
  a note the next run reads. Use it when the pipeline should still do the work.
- **An interactive session** DOES the work: the agent edits the task's worktree turn by turn while
  you watch the diff and approve commands, and it ends in a PR. Use it when you would rather drive
  the change yourself than wait for plan → develop → review.

A Discuss chat never touches the task's worktree, so it is safe while a pipeline run (or a
session) is active on the same task.

---

## The one structural decision

**An interactive session IS a task, from the first keystroke.** Not a chat that becomes a task
when it finishes.

Every capability this feature needs is already keyed to a task: worktree lifecycle and
`SetupCommand`, open-in-editor, usage records (`TaskId`/`ExecutionId`), compute metering,
timeline and evidence, reset/cancel, PR creation, the Code Review lane. Materializing the task at
the *end* would mean reconciling spend onto a row that did not exist while the money was spent,
and leaving the board silent while somebody is actively editing the auth module.

So: open a session → a task row exists immediately. "Lands on the board as a completed task"
describes where it ends, not when it is born.

The corollary is that this is **not a new kind of chat**. It is a `Developing` phase whose turns
are triggered by a human instead of by the queue sweep.

### Sessions only start fresh

A session cannot be opened on an existing task. Taking task #412 interactive is superficially
attractive — it already has a worktree and a branch — but it makes `IsInteractive` mutable
mid-life, which means the queue exclusion flips underneath a running lease. That is a different
feature with a different set of races, and it is not needed for the work this exists to serve.

`IsInteractive` is therefore set once at creation and never changes.

---

## Entities

### No new lane

`Lane` has no `HasConversion` registered in `TasksDbContext`, so EF persists it as an int.
Inserting a value mid-enum renumbers every existing row, and appending after `Done` yields a lane
that sorts last on a board where lane order *is* the pipeline. Neither is acceptable for what is
really a flag.

An interactive session lives in `Lane.Developing` with `Status.Active`, marked by:

```csharp
// TaskEntity
public bool IsInteractive { get; set; }
public DateTimeOffset? InteractiveSessionStartedAt { get; set; }  // non-null = session live
```

`IsInteractive` does one load-bearing thing: `ListQueuedTasksAsync` excludes it, so the
autonomous sweep can never claim a task a human is driving. Belt and braces — an interactive task
is never set to `InQueue` in the first place, and the queue keys on that status.

`InteractiveSessionStartedAt` is the live marker, mirroring `AgentConversation.ActiveRunStartedAt`.

Everything else is reused as-is: `RepoId`, `BranchName`, `CurrentAgentId`, `AssignedAgentId`,
`RunnerUserId`, `Model`, `LeaseOwner`, `LeaseExpiresAt`.

### Transcript: `TaskMessage`

`MessageType` already carries `Text, Question, Progress, Error, ToolCall, ToolResult, Answer,
SystemEvent`. That is an interactive transcript. `SendTaskMessageEndpoint` is already the
"human speaks to a task mid-flight" path — interactive extends it rather than opening a second one.

### Executions: one per turn

Not one per session. `TaskExecution.SessionId` plus
`GetLatestExecutionSessionAsync(taskId, Lane.Developing, ct)` is *already* how a re-entered
Developing run warm-resumes (`TaskOrchestrator.RunDevelopingPhaseAsync`). Turn N resuming turn N−1
is that mechanism verbatim, for free.

Per-turn executions also give per-turn token accounting (usage records) and render in the
existing timeline/evidence UI with no changes.

### Approvals: their own table

They have state transitions and must outlive a process, so they cannot be an in-memory dictionary.

```
TaskApprovalRequest
  Id, OrgId, TaskId, ExecutionId, ToolUseId
  Tool, Command
  Status   : Pending | Allowed | Denied | Expired
  Scope    : Once | Session
  DecidedByUserId, CreatedAt, DecidedAt
```

---

## Session affinity is the binding constraint

A CLI session file lives on the machine that ran the previous turn. Both `ChatTurnRunner` and
`AgentRequest.ResumeSessionId` document this and handle losing it.

In batch mode a lost session costs one cold re-run. **In interactive mode it costs a cold re-run on
every turn the user takes** — which is the entire cost model of the feature.

Therefore the lease is held **for the session, not the turn**: `LeaseOwner`/`LeaseExpiresAt` are
renewed by a heartbeat for as long as `InteractiveSessionStartedAt` is non-null, and every turn
routes to the lease holder. These columns already exist; interactive inverts their lifecycle, so
it must be deliberate.

Do **not** reuse `RunnerPresenceRules.OnlineWindow` (3 minutes, 60s heartbeat) as the session
liveness signal. That granularity is right for "is this machine around to claim queued work" and
badly wrong for a live session — a user would wait up to three minutes before anything noticed a
dead one. The session lease renews off the live SSE/SignalR connection.

### An unreachable backend must be visible and bounded

A UserRunner session executes on the user's runner, which can be away — restarting,
asleep, on dropped wifi. That is transient, so the session should not close on the first miss;
pickup happens seconds before the runner reconnects, and closing there destroyed live sessions
over a six-second gap.

Silence is the other failure, and it is worse. Deferring with only a log entry leaves the session
reading "Session live / preparing the workspace" indefinitely — no branch, no reply, and nothing
anywhere telling the human that their runner is the problem. Observed in the wild: 42 minutes of
apparent hang while the pickup sweep deferred every five seconds.

So a deferral now announces itself **once** into the conversation ("waiting for your runner to
reconnect"), stays quiet on subsequent sweeps, and after `UnreachableBackendGrace` (15 minutes)
closes the session with a plain explanation. Committed work is on the branch either way, and a
closed session can be reopened — an open one nobody can drive cannot.

### What this implies for triggering

| Signal | Path | Why |
|---|---|---|
| Session pickup | DB marker + a *separate* interactive pickup sweep | No lease holder exists yet. Seconds of latency on open is fine, and it is the coordination shape everything else already uses. Note this cannot be the queued-tasks sweep — `ListQueuedTasksAsync` excludes interactive tasks by design. The pickup predicate is `IsInteractive && InteractiveSessionStartedAt != null && (LeaseOwner == null \|\| LeaseExpiresAt < now)`, which doubles as the recovery path when a session's worker dies. |
| Every turn | the session's own loop, polling for unanswered messages | Once picked up, the holder *stays* in `RunInteractiveSessionAsync` for the life of the session. It already has the lease, the worktree and the warm CLI process, so it can poll one indexed table every couple of seconds and needs no cross-process signal at all. |
| Approval decisions (slice 2) | direct internal HTTP to `LeaseOwner` | Mid-run, inside a hook, with a human waiting. This is the one signal that cannot ride the loop. |

The direct path is the shape already in production for the chat relay and the editor endpoints:
pod locator + `Runners:RelayUrlTemplate` + `X-Worker-Key`. `LeaseOwner` is the routing key —
it already means "which worker instance owns this task's in-flight run".

An earlier draft of this design routed turns 2..N over that same relay. Implementation made it
obvious that was unnecessary: the worker never *lets go* between turns, so there is nothing to
route to it. Keeping the loop self-contained also means a session survives a relay outage.

### The watermark

Turns are driven by "which human messages does this session still owe a reply to", answered by
`InteractiveAnsweredThroughAt` on the task.

The tempting shortcut — treat the last execution's `CompletedAt` as the watermark — is wrong in a
way that is easy to ship and hard to notice. A follow-up typed *while the agent is working* is
older than the run that finishes after it, so it would be counted as already answered and never
replied to. The turn therefore stamps the newest message it actually consumed, captured before
the run starts.

The watermark advances on failure too. Leaving it behind after a failed turn would replay the
same message on the next tick forever, burning tokens on a loop nobody asked for.

---

## The approval round-trip

The pipeline's model is: approve once (the plan), then run unattended under static guards.
Interactive inverts it: the human is present for every turn. That is the whole value, and it means
the Bash guard needs a third outcome besides allow and deny.

### One guard, not two

Do not register a second `PreToolUse` hook alongside `AgentBashGuard`. That makes correctness
depend on multi-hook composition semantics, and the failure mode is "the command runs anyway".

`InteractiveBashGuard` calls `AgentBashGuard.IsBlockedCommand` first — already `public static`,
already shared with the worktree-setup caller precisely so two callers cannot disagree about what
"restricted" means.

```
IsBlockedCommand(cmd) → Deny        // existing message + 3-strike audit. NEVER escalated to a prompt.
OnSafeList(cmd)       → Allow       // build, test, lint, git status/diff/log, scripts in package.json
SessionAllows(prefix) → Allow       // "always allow" from an earlier decision this session
otherwise             → ask
```

**Order is a security property.** A blocklist hit is denied outright. An interactive session must
not become the way to get `psql` authorized — the human's presence adds a third outcome for
*ordinary* commands, it does not soften the command guard (SECURITY.md, "Command guard").

`ShellCommandParser.ExtractPrograms` supplies the program tokens that key the safe-list and the
per-session memory.

### The round trip

```
hook (runner) → RunnerApprovalGate → hub invoke RequestApproval
host          → InteractiveApprovalCoordinator.RequestAsync
                  persist TaskApprovalRequest (Pending)
                  push ApprovalRequested on the task's stream
                  await TaskCompletionSource, 10-minute cap
click         → POST /api/tasks/{taskId}/approvals/{approvalId} {decision}  [TasksExecute]
                  ExecuteUpdate guarded on Status == Pending → resolve the TCS
hook          → AgentHookResponses.Allow() / Deny(reason)
```

The database, not the in-memory map, is the arbiter: the decision update only matches while the
row is still Pending, so a double click — or a decision racing the expiry sweep — settles exactly
once and the loser gets a 409 rather than believing they allowed something they did not.

Prompts are expired when a session is picked up (its previous run is gone, so nothing can answer
them) and when it closes.

**Verified end to end on a user's runner** (2026-08-10): the agent reached for `rm obsolete.txt`, the
policy stopped it, the card rendered with the command verbatim, approving it returned `Allowed`
across runner → host → browser → host → runner in the same second, and the file was deleted. A
chained command (`ls … && rm …`) was correctly caught by the same prompt rather than laundered
behind its safe prefix.

Four bugs only the live run found:

- **The guard was nested inside `BashGuardEnabled`**, which is *off* on a user's own machine — so
  no prompt could ever appear on the edition this exists for. That flag answers "may Orkka refuse
  this?"; approval answers "should the human see it?". Separated, with the flag now controlling
  only whether the blocklist is enforced.
- **The human's message was wrapped in the untrusted-content fence**, and a session refused its
  operator's `rm` as a suspected prompt injection — correctly, given what it was told. Session
  messages are now delivered as operator instruction, and conversation history is bounded so the
  same text cannot appear fenced alongside it.
- **A restart stranded sessions for the whole lease TTL**, because a lease still held in this
  worker's own name read as "someone is driving it". The processor now treats its own lease as
  reclaimable; only another worker's live lease defers.
- **A transient backend outage closed sessions permanently.** Immediate pickup races the runner's
  reconnect by a few seconds; the old code read "runner not connected" as a dead session and
  ended it. Transient failures now defer and retry; only a reachable backend that still cannot
  produce a worktree ends the session.

### Session grants are enforced per segment

A grant is stored from one command and matched against every command that follows, so the check
has to hold for the *whole* line — not for whichever program the line happens to key on. Keyed as
a single string, a grant for `git clean` would have admitted `git clean -fd && git reset --hard`,
because that line still keys as "git clean".

So `RequiredApprovalKeys` returns one key per ask-worthy segment, and a command runs unasked only
when **every** one of them is granted. Safe segments need no key, which keeps the common
`cd app && npm install` riding a grant for `npm install`.

Two rules make the keys themselves honest:

- The key is the program, plus a verb only for programs whose next token is one (`git clean`,
  `npm install`, `docker compose`). For everything else the program *is* the grant — `rm` takes
  filenames, and `rm obsolete.txt` as a key looks narrow while covering whatever came next.
- A subcommand-style program with no readable verb (`git -C /srv/app status`) is not keyable at
  all. Falling back to `git` would turn a grant for one subcommand into a grant for all of them.

When a line has several ask-worthy segments there is no one label describing what would be
granted, so `ApprovalKey` returns empty and the card offers only Allow once and Deny.

### Where each half runs

The invoker — and therefore the hook — executes **inside the runner process** for every
session (`Orkka.LocalRunner` constructs `ClaudeAgentInvoker`), while the click lands on the **host**. So
the round trip is runner → host → browser → host → runner, and it needs a runner-initiated call,
not just the host→runner client-result invokes that already exist. The runner's SignalR connection
can invoke hub methods on the host, which is the channel to use; the hook simply awaits that call.

### Every run is guarded

There is no switch: `AgentGuardOptions` is gone, and every run on the developer's machine —
interactive or not — carries the command guard, the read guard and the write boundary
(SECURITY.md). The developer's own toolchain stays unguarded by design; what is refused is what
reaches past the repository with their sign-ins. Interactive sessions add exactly one outcome on
top: asking the human before a command that destroys work the session has not landed
(`git reset --hard`, `git clean`, history rewrites). A push is not askable — the platform pushes.

### Risk lives in the guard, not in prompts

Anything that reads a credential, pushes, publishes, reaches another host or a cloud account,
or leaves the worktree is refused outright and never offered to anyone: credential stores and
files, `git push` / new remotes, registry publishing, forge CLIs, remote shells (`ssh`, `scp`,
`rsync`), cloud and cluster CLIs (`kubectl`, `terraform`, `aws`, `az`, `gcloud`), host and service
control, and deletes that leave the worktree — an absolute path, `~`, a `..` traversal, an
unexpanded `$VAR`, or a bare `*`.

Note what that last rule does *not* block: `rm -rf node_modules`, `rm -rf bin obj`, `rm -rf dist`.
Cleaning build output is what a build does in every language, and refusing it would teach agents
that cleaning is impossible. What matters is the target, not the flags — `rm /etc/passwd` is no
better for lacking `-rf`.

Everything else a toolchain does simply runs, **including installs** and the project's local
services (`docker compose`, a local `psql`). A developer whose every command stops for approval
learns to click through without reading, which costs more than it buys on the commands that
actually matter.

So approval covers one narrow band: commands that destroy work the session has not landed —
`git reset --hard`, `git clean`, `git checkout -- .`, `git restore .`, dropping stashes, history
rewrites. That work is the human's as much as the agent's.

Order is a security property. The blocklist is consulted first and its answer is final; a human
clicking "allow" is easier than any of the other six layers, so a session must never be the route
by which `psql` gets authorised.

### The Finish turn is not interactive

Pressing Finish *is* the human's approval to commit and push, so that final run uses the ordinary
guard. Marking it interactive would prompt them for the thing they just clicked — and with the
session already closed and nobody watching, the prompt would hang until it expired, stranding the
work unpushed.

Three rules that are not negotiable:

1. **Timeout denies.** Never allow on expiry. The deny text must say the approval *expired*, not
   that the command is restricted — otherwise the agent concludes its tools are dead and parks on
   a question nobody can answer. Same lesson the `_blockedAttempts >= 3` wording already encodes.
2. **Every `HookOutput` field populated.** Always go through `AgentHookResponses.Allow`/`Deny`.
   An unset field makes the CLI discard the decision and run the command anyway.
3. **Sweep `Pending` rows to `Expired`** when the owning lease expires, or the UI shows a live
   prompt for a run that is already dead.

---

## Divergence from `ChatTurnRunner`

| | `ChatTurnRunner` | Interactive |
|---|---|---|
| Workspace | shared per-project worktree, `reset --hard` on reuse | per-task worktree on a branch (`SetupGitWorkspaceAsync`) |
| Tools | `Read/Glob/Grep` + `orkka` MCP | full dev set + Bash under `InteractiveBashGuard` |
| Invoker | `IChatAgentInvoker` | `IAiAgentInvoker` via `ExecutionRoute` |
| Transcript | `AgentMessage` (agents schema) | `TaskMessage` (tasks schema) |
| Usage | `TaskId: null`, `ComputeRunKind.ChatTurn` | `TaskId` + `ExecutionId`, `ComputeRunKind.TaskRun` |
| Resume key | `AgentConversation.ClaudeSessionId` | `TaskExecution.SessionId` |
| Output | diagrams / docs / tasks / designs | the diff |

**The chat workspace is structurally hostile to this and must not be extended into it.** It is
read-only by design, pinned at `origin/{branch}`, `reset --hard`-ed on reuse, and *shared across
every conversation in the project* — two users writing at once would erase each other.

What interactive borrows from the chat side is only the delivery mechanics: `ChatRunHandle`'s
detach semantics, where a browser disconnect stops the relay but never cancels the agent or skips
persistence. Lift `ChatRunCoordinator`/`ChatRunHandle` into SharedKernel as a keyed generic rather
than reimplementing — two divergent answers to "don't lose the run when the tab closes" is how you
get a bug that only reproduces on flaky wifi.

### Where the code lives

`TaskOrchestrator` is already `partial`, so the interactive path lives in
`TaskOrchestrator.Interactive.cs` and `TaskOrchestrator.InteractivePrompts.cs` rather than in a
service of its own.

An earlier draft called for extracting `SetupGitWorkspaceAsync`, `StreamAgentRunAsync` and
`RecordRunResultsAsync` into a shared `TaskRunSupport`. That was the right instinct about file
size and the wrong trade: those methods reach into `GetGitTokenAsync`, `RunSetupCommandAsync`,
`PersistAgentTextChunkAsync` and half a dozen private fields, so pulling them out means
restructuring the class the entire pipeline runs through — a real risk of regression, bought with
no functional gain. A partial file keeps the new code separate and reviewable today; the
extraction stays available later, once the interactive path has demonstrated what it actually
needs rather than what we guessed it would.

---

## Live diff panel

**Built** (Code View, phase 2). Computed on the machine holding the worktree, stored as the task's
one *live* code-change snapshot, pushed over the existing `TaskHub` group. Applies to every run
that owns a branch — the batch Developing phase and interactive turns alike.

- **Trigger**: `LiveWorkspaceCapture` (Execution/Orchestration), one per agent run, fed from the
  stream loop in `StreamAgentRunOnceAsync`. A mutating `ToolUse` chunk (Edit/Write/MultiEdit/
  NotebookEdit/Bash) marks the tree dirty; *any* later chunk means the previous tool finished
  (a tool_use block streams before its tool runs, so that is the "now diff" signal). Debounce
  1.5 s, at most one capture per 4 s, a bounded flush at run end. Failures are logged and
  swallowed; a runner without the op disables the capture for that run.
- **Content**: `IGitWorkspaceManager.GetWorkingTreeDiffAsync` — the working tree (committed,
  staged, unstaged, untracked; `.gitignore` honoured) against the merge-base with
  `origin/{base}`. Implemented by staging into a throwaway `GIT_INDEX_FILE` seeded from a copy
  of the real index, so the worktree's own index is never touched while the agent's git commands
  use it. Runner-proxied (`GetWorkingTreeDiff` op, `RunnerCapabilities.WorkingTreeDiff`).
- **Storage**: `TaskCodeChangeSnapshot.IsLive` — one row per task (filtered unique index),
  overwritten on every capture with a rising `Revision`; `ITasksApi.UpsertLiveCodeChangeSnapshotAsync`
  returns null when the diff hash is unchanged. `SaveCodeChangeSnapshotAsync` (the permanent
  end-of-phase record) deletes the live row first, and its dedup ignores live rows so a permanent
  copy identical to the live one is still written. `GET /code-changes` lists the live row first
  (`isLive`, `revision`); the diff comes from the same per-snapshot endpoint — so a tab opened
  mid-run paints at once and no patch rides on a broadcast.
- **Push**: `WorkspaceChangedEvent` (files + stats + revision + snapshotId) via
  `ITaskNotifier.SendWorkspaceChangedAsync` → `TaskHub` `WorkspaceChanged`. The Code View
  applies the file list immediately, then fetches the diff by id; older revisions are dropped;
  browser-side tool-call previews (Tier A) older than the capture are folded away.

---

## Finishing

"Finish" is an **explicit user action**, never inferred. An agent deciding on its own that a
session is over and opening a PR is a bad surprise.

### Sessions name themselves

A session opens with the placeholder `Interactive session`, because someone should be able to
start typing about a bug before naming it. The first turn then titles it from the opening message
with one fast-utility call, *before* running — a turn takes minutes, and a board full of
"Interactive session" tells nobody, including the person deciding whether to interrupt, what any
of them is about.

The guard lives in the UPDATE's `WHERE` clause (`Title == UntitledSession`), not in a
read-then-write: a user renaming a session while its first turn titles it must keep their words,
and that also makes titling naturally once-only.

**A session that changed nothing is a normal ending.** People open sessions to ask a question or
to explore an idea they then decide against. The wrap-up run is told that explicitly, so it does
not create an empty commit or treat git's "nothing to commit" as a failure to troubleshoot; and
routing keys on whether the branch actually reached the remote (`RemoteBranchExistsAsync` — the
same thing a PR would be built from, not the agent's report of what it did). Nothing pushed means
the task closes Done/Completed with a note, instead of parking an empty diff in front of a
reviewer while the awaiting-PR sweep retries a branch that never existed.

On finish, the existing pipeline does the rest:

1. The agent commits (`CommitAndPushInstruction` tells it not to push) and the runner pushes the
   branch (`EnsureBranchPushedAfterRunAsync`).
2. The orchestrator opens Orkka's own PR (`IPullRequestService`); once reviewed it is
   squash-merged into a linked folder or published to the forge by the developer's CLI.
3. The task advances into `CodeReview` (or `Done`, per the project's pipeline settings) and
   Sentinel reviews it like any other task.

The commit / push / review / merge half of the feature is therefore nearly free: it is the pipeline
that already exists.

**Abandoning** a session reuses the existing reset/archive flow rather than inventing orphan-chat
cleanup — another dividend of the task-first decision.

---

## Metering

Open question that must be settled **before** there is data to migrate.

Chat compute is billed on wall-clock around the whole turn (`ChatTurnRunner` wraps
`RecordComputeAsync` in a `finally`, deliberately counting cancelled and faulted turns because
they occupied capacity). A 40-minute interactive session where the human thinks for 30 of those
minutes must not bill 40 minutes of compute — but the warm session genuinely does hold a process.

Token accounting is unaffected: usage records carry `TaskId`/`ExecutionId` and attribute to the
task's creator.

Note that on a UserRunner target the question is moot — those runs execute on the customer's own
machine and record no compute at all, by design.

---

## Build order

**UserRunner first.** It was built and proven first on the single-runner path (the since-removed
Orkka Local edition): no pod locator, no relay template, no `X-Worker-Key` hop, so the approval
round-trip was host↔runner on a connection that already existed — roughly a third of the
multi-pod surface, and dogfoodable the same day.

| Slice | Contents | Status |
|---|---|---|
| 1 | Task + session lifecycle, turn loop with session resume, explicit Finish → PR → Code Review. Runs under today's static guard. | **built** — see below |
| 2 | Approval round-trip (`InteractiveBashGuard`, `TaskApprovalRequest`, decision routing). | **built** (UserRunner) |
| 3 | Live diff panel. | **built** — Code View phase 2 (`LiveWorkspaceCapture`, live snapshot, `WorkspaceChanged`) |
| 4 | Hosted multi-pod routing: relay, lease affinity across pods. | |

Slice 1 turned out to need almost no new UI. The task detail page already carries a conversation
thread with a composer posting to `/api/tasks/{id}/messages` — the exact endpoint the session loop
consumes — plus a live agent stream over `TaskHub`. A session renders there unchanged, so the only
genuinely new affordances are a banner that ends the session and a button that starts one.

**Verified end to end on a user's runner** (2026-08-10): session opened from the board → picked up
within ~2s → branch and worktree created → a message drove a real Claude turn that edited the file
→ Finish committed with a conventional-commit message, pushed, moved the task to `CodeReview` and
created the PR. A host restart mid-session was also exercised: the session stayed live and was
re-picked-up, as designed.

Two things that run only fell out of running it:

- **The agent is re-resolved every turn**, not captured at pickup. The task page exposes an agent
  picker *during* a session, and a session can outlive several changes to it; captured once,
  neither a different agent nor an edited model took effect until the session was re-picked-up,
  which nothing in the UI suggests. A changed agent also forces a cold start — resuming would keep
  answering as the agent the user just switched away from.
- **Every turn logs a line.** Without one a session is silent between pickup and its next lane
  change, so a turn failing inside the invoker looks identical to a loop that never saw the
  message. That is the wrong question to be left asking, and it cost real time to answer once.

---

## Concurrency with the pipeline

An interactive session on a repo while autonomous tasks are in flight on the same repo produces
conflicts discovered at PR time. Survivable — `MergeResolution` and `MergeFailureNotifications`
already exist — but the session-open UI should surface "N tasks in flight on this repo" rather
than letting the user find out later.

A session is `Active`, but it is **excluded from concurrency accounting** — both the
`MaxParallelTasks` slot count and the same-repo conflict set (`ListActiveTasksAsync` filters it
out at source).

`MaxParallelTasks` budgets autonomous work. A session is human-paced and mostly idle, so counting
it consumes capacity nobody is using; on the common setting of 1, opening a session would stop the
pipeline dead for as long as it stayed open. The repo exclusion is deliberate too — leaving the
session's repo in the conflict set would make the whole thing pointless for single-repo projects,
which are most of them. Two branches can conflict at merge like any other pair, and
`MergeResolution` already handles that.

---

## Positioning

The obvious objection is "why not just open Claude Code?". The answer must be continuity, and the
UX should make it loud: the session ends as a PR in the existing Code Review lane, the agent keeps
its memory, the work is on the board, the spend is attributed, the commands are audited.

A chat box next to a diff competes with an IDE. The same agent that already knows this codebase,
producing a task that lands in the same pipeline, does not.
