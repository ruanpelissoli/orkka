---
area: FRG
title: Forge publishing — the developer's gh / az / glab open the pull request
modes-default: [hybrid-repo]
---

## FRG-001 Approve & merge on a GitHub remote publishes a PR via gh
```yaml
priority: P0
surface: [api, ui]
automation: automated
refs:
  - orkka-api:POST /api/tasks/{taskId}/pull-request/merge
  - orkka-api:GET /api/tasks/{taskId}/pull-request
  - orkka-api:GET /api/tasks/{taskId}
  - i18n:forgePr
costUsd: 0
since: 2026-10-26
```
**Preconditions**: Runner online with capability `forge-cli` and `gh` signed in (locally: the harness's stub gh on the runner's PATH); a task on a GitHub remote at WaitingCodeReview.
**Steps**:
1. Approve the PR; click "Open pull request" → "Confirm: open pull request" (or `POST …/pull-request/merge`).
2. `GET …/pull-request`; `GET /api/tasks/{id}`.
**Expected**:
- The runner pushes `orkka/<KEY-n>` and runs `gh auth status --hostname github.com`, `gh pr list --repo <owner/name> --head <branch> --state open --json url --jq .[0].url`, then `gh pr create --repo <owner/name> --head <branch> --base <defaultBranch> --title <task title> --body-file <tmp>`.
- The record's `forgeUrl` is the URL gh printed; the task has `publishedPrUrl` = the same URL, lane Done; the UI shows "Published" and "View pull request on GitHub".
- Orkka never merges on the forge: the forge PR stays open for the developer's team.

## FRG-002 Publishing twice for the same head branch returns the existing PR
```yaml
priority: P0
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/tasks/{taskId}/pull-request/merge
since: 2026-10-26
```
**Preconditions**: FRG-001 done (a forge PR is open for the head branch).
**Steps**:
1. Trigger the publish again (`POST …/pull-request/merge`, or let the merge sweep retry).
**Expected**:
- `gh pr list --head <branch> --state open` finds the open PR and its URL is returned; there is NO second `gh pr create` call (the stub's call log shows exactly one create).

## FRG-003 gh not signed in: the publish fails with the remedy
```yaml
priority: P1
surface: [api, ui]
automation: planned
refs:
  - orkka-api:POST /api/tasks/{taskId}/pull-request/merge
  - orkka-api:GET /api/tasks/{taskId}/messages
since: 2026-10-26
```
**Preconditions**: Runner online; `gh auth status` exits non-zero (stub mode `signed-out`).
**Steps**:
1. Publish the approved task.
**Expected**:
- Failure "The GitHub CLI is not signed in to github.com — run `gh auth login` on this machine." on the task thread / merge result; no forge PR; the task stays reviewable and publishes once gh is signed in.

## FRG-004 gh not installed: the publish fails naming the install step
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/tasks/{taskId}/pull-request/merge
since: 2026-10-26
```
**Preconditions**: Runner online with no `gh` on its PATH.
**Steps**:
1. Publish the approved task.
**Expected**:
- "The GitHub CLI (gh) is not installed on this machine — install it from https://cli.github.com and run `gh auth login`."

## FRG-005 A repo's cliAccount scopes gh to that account
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/repos
  - orkka-api:POST /api/tasks/{taskId}/pull-request/merge
since: 2026-10-26
```
**Preconditions**: Repo added with `cliAccount:"dana-work"`; the stub gh records environments.
**Steps**:
1. Publish the approved task.
**Expected**:
- The runner first runs `gh auth token --hostname github.com --user dana-work` and every later gh call carries `GH_TOKEN` = that token (scoped to the call, never stored or sent to the host).

## FRG-006 Publish on Azure DevOps through `az repos pr`
```yaml
priority: P1
surface: [api]
automation: manual
refs:
  - orkka-api:POST /api/tasks/{taskId}/pull-request/merge
since: 2026-10-26
```
**Preconditions**: A repo `https://dev.azure.com/<org>/<project>/_git/<repo>`; `az login` done and the azure-devops extension installed.
**Steps**:
1. Publish the approved task; publish again.
**Expected**:
- `az repos pr list … --source-branch <branch> --status active` then `az repos pr create … --target-branch <default> --title … --description …`; `forgeUrl` = `https://dev.azure.com/<org>/<project>/_git/<repo>/pullrequest/<id>`.
- The second publish returns the same URL (active PR found).

## FRG-007 Publish on GitLab through `glab mr create`
```yaml
priority: P2
surface: [api]
automation: manual
refs:
  - orkka-api:POST /api/tasks/{taskId}/pull-request/merge
since: 2026-10-26
```
**Preconditions**: A gitlab.com remote; `glab auth login` done.
**Steps**:
1. Publish the approved task; publish again.
**Expected**:
- `glab mr list --repo <ns/name> --source-branch <branch> --output json`, then `glab mr create … --yes`; `forgeUrl` = the MR URL; the second publish returns the existing MR's `web_url`.

## FRG-008 No CLI for the forge: the branch is pushed and the user opens the PR
```yaml
priority: P2
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/tasks/{taskId}/pull-request/merge
since: 2026-10-26
```
**Preconditions**: A Bitbucket (or `other`) remote.
**Steps**:
1. Publish the approved task.
**Expected**:
- "Orkka has no CLI for this repository's host — branch '<branch>' is pushed; open the pull request into '<default>' yourself."; the branch exists on the remote.

## FRG-009 Publishing needs the forge-cli runner capability
```yaml
priority: P2
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/tasks/{taskId}/pull-request/merge
  - orkka-api:GET /api/me/runner
since: 2026-10-26
```
**Preconditions**: A runner build that does not advertise `forge-cli` (or the runner offline).
**Steps**:
1. Publish the approved task.
**Expected**:
- Refused with "Your runner is not connected (or too old to open pull requests) — run `git pull`, then `./orkka start`, and try again."; nothing is published; it retries when a capable runner connects.

## FRG-010 Show the runner's forge CLIs on the Machine page and the API
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:GET /api/me/runner
  - orkka-ui:/settings/machine
since: 2026-10-26
```
**Preconditions**: Runner online; gh signed in as `dana`, az installed but not logged in, glab absent.
**Steps**:
1. `GET /api/me/runner`.
2. Open `/settings/machine` → "Pull requests".
**Expected**:
- `forgeClis` = `[{cli:"gh", found:true, authenticated:true, accounts:["dana@github.com"]}, {cli:"az", found:true, authenticated:false, detail:"Run `az login`."}, {cli:"glab", found:false, authenticated:false, detail:"Install the GitLab CLI …"}]`.
- The card lists "GitHub CLI" Ready "Signed in as dana@github.com", "Azure CLI (Azure DevOps)" Not signed in with "To sign in, run:", "GitLab CLI" Not installed with "Install it, then run:".
- Runner offline → "As of the runner's last report — it is offline right now."; an old runner → "This runner does not report its forge CLIs yet. Update it with `git pull` + `./orkka start`."

## FRG-011 Forge PR links on the board and the task page
```yaml
priority: P2
surface: [ui]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/tasks/[taskId]
  - orkka-ui:packages/ui/src/components/pr/ForgePrLink.tsx
  - i18n:forgePr
  - i18n:forges
since: 2026-10-26
```
**Preconditions**: A published task on a GitHub remote and one on an Azure DevOps remote.
**Steps**:
1. Open each task page and the review page.
**Expected**:
- "View pull request on GitHub" / "View pull request on Azure DevOps" link to `publishedPrUrl` (new tab); repo rows carry the forge icon.

## FRG-012 Read a published PR's state with the developer's CLI
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/tasks/{taskId}/pull-request/sync
since: 2026-10-26
```
**Preconditions**: A published PR; the stub gh answers `gh pr view <url> --json state --jq .state` with `MERGED`.
**Steps**:
1. `POST /api/tasks/{id}/pull-request/sync`.
**Expected**:
- The record's `forgeState` becomes `merged` (`completed`/`abandoned` for az, `merged`/`closed`/`opened` for glab normalise to merged/closed/open).

## FRG-013 A linked local folder lands by squash-merge, never by publishing
```yaml
priority: P1
modes: [hybrid-folder]
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/tasks/{taskId}/pull-request/merge
since: 2026-10-26
```
**Preconditions**: A task on a linked folder at WaitingCodeReview.
**Steps**:
1. Approve and merge.
**Expected**:
- No forge CLI is invoked; the change lands in the folder's base branch through a scratch clone (HYF-018/019/020); the task is Done with no `publishedPrUrl`.
