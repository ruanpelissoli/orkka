---
area: AIR
title: AI provider readiness — agents run only on the developer's own CLI subscriptions
modes-default: [hybrid-repo]
---

## AIR-001 No AI CLI signed in: the task parks WaitingForRunner with a note
```yaml
priority: P0
surface: [api, ui]
automation: planned
refs:
  - orkka-api:POST /api/tasks/{taskId}/submit
  - orkka-api:GET /api/tasks/{taskId}
  - orkka-api:GET /api/tasks/{taskId}/messages
since: 2026-10-26
```
**Preconditions**: Runner online but every provider `authenticated:false` (e.g. a runner started with a HOME that has no CLI logins).
**Steps**:
1. Submit a task.
**Expected**:
- `status:"WaitingForRunner"`; exactly one system message "Waiting for an AI subscription: no AI CLI is signed in on your runner. Sign in to Claude Code (`claude`, then /login), Codex (`codex login`), Gemini (`gemini`) or GitHub Copilot (`gh auth login`) on that machine — this task starts on its own once one is ready."; no run, no usage row.

## AIR-002 Signing a CLI in unparks the task without a click
```yaml
priority: P0
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/tasks/{taskId}
  - orkka-api:GET /api/me/runner
since: 2026-10-26
```
**Preconditions**: AIR-001 state.
**Steps**:
1. Sign Claude Code in on that machine; wait for the next Hello (≤ 60 s).
**Expected**:
- `GET /api/me/runner` reports claude ready; the task re-queues and starts; the parking note is not repeated.

## AIR-003 A model whose provider is not signed in is said at creation and waits
```yaml
priority: P1
surface: [api, ui]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/tasks
  - orkka-api:GET /api/tasks/{taskId}/messages
  - i18n:modelBilling
since: 2026-10-26
```
**Preconditions**: Runner with Claude ready, Codex not signed in.
**Steps**:
1. On `/tasks/new` pick a Codex model; create.
**Expected**:
- The create form says "Runs on your runner · 0 credits — waiting: sign in to Codex on your runner first."; the task is created (not rejected) with `runnerUserId` = caller, the thread explains the provider is not signed in on the runner, and it runs only once Codex signs in.

## AIR-004 There is no cloud execution: nothing ever falls back to a billed run
```yaml
priority: P0
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/usage/summary
  - orkka-api:GET /api/tasks/{taskId}
since: 2026-10-26
```
**Preconditions**: Runner stopped for 15 minutes with a submitted task (the cloud path and its fallback sweep were removed in orkka-api e3a3a06 — there is no setting to turn them on).
**Steps**:
1. `GET /api/tasks/{id}`; `GET /api/usage/summary`.
**Expected**:
- The task is still `WaitingForRunner` (no `executionTarget` field exists); no usage row with a non-zero cost; no cloud job.

## AIR-005 Utility completions run on the runner; offline degrades with a reason
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/run-config/detect
  - orkka-api:POST /api/projects/{projectId}/milestones/plan
since: 2026-10-26
```
**Preconditions**: Runner stopped.
**Steps**:
1. `POST …/run-config/detect`; start a milestone plan.
**Expected**:
- Both refuse with "Your runner is offline or unavailable — run `./orkka start` to …" (plan job fails `RunnerOffline`); nothing is billed to a platform key.

## AIR-007 Gemini, Grok and OpenCode are not ready by default — no guard can be put on them
```yaml
priority: P0
surface: [api, ui, cli]
automation: planned
refs:
  - orkka-api:GET /api/me/runner
  - orkka-api:src/Orkka.LocalRunner/ProviderDetection.cs
  - orkka-ui:/settings/machine
  - cli:doctor
since: 2026-11-17
```
**Preconditions**: The Gemini CLI installed and signed in on this machine; `~/.orkka/runner.env` has no `ORKKA_ALLOW_UNGUARDED_PROVIDERS` (the harness: `<E2E_RUNNER_HOME>/runner.env`); runner connected.
**Steps**:
1. `GET /api/me/runner`; read the `gemini` provider entry.
2. Open `/settings/machine` and read the provider chips.
3. Create a task on a Gemini model and submit it.
**Expected**:
- Step 1: `gemini` reports `cliFound:true, authenticated:false` with `detail` "gemini is signed in, but Orkka cannot restrict what its agent runs in your shell (no command hook or deny list it can drive), so it is off by default. To use it anyway on this machine, add ORKKA_ALLOW_UNGUARDED_PROVIDERS=gemini to ~/.orkka/runner.env (comma-separate several). Claude, Codex and Copilot run with Orkka's command guard." Grok and OpenCode answer the same when signed in.
- Step 2: the Gemini chip is not "ready".
- Step 3: the task waits for the provider (AIR-003) — nothing the host sends can make the runner start a Gemini agent.
**Notes**: automatable on a machine with Gemini signed in — seed `runner.env` in the sandbox Orkka home; skip otherwise.

## AIR-008 Opting in on the machine makes an unguarded provider ready
```yaml
priority: P1
surface: [api, cli]
automation: planned
refs:
  - orkka-api:GET /api/me/runner
  - orkka-api:src/Orkka.LocalRunner/ProviderDetection.cs
since: 2026-11-17
```
**Preconditions**: AIR-007 state.
**Steps**:
1. Append `ORKKA_ALLOW_UNGUARDED_PROVIDERS=gemini` to `~/.orkka/runner.env`; wait for the next Hello (≤ 60 s, no restart).
2. `GET /api/me/runner`.
3. Remove the line again.
**Expected**:
- Step 2: `gemini` reports `authenticated:true` and its models; a Gemini task can start.
- Step 3: after the next Hello it is `authenticated:false` with the AIR-007 detail again.
**Notes**: the opt-in is read only from the machine's `runner.env` — an API call, project setting or host message cannot set it.
