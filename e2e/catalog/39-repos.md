---
area: REPO
title: Repositories — connect, browse, branches, commits, pull requests, settings
modes-default: [all]
---

## REPO-001 Render the Repos page with the hub layout and insight tabs
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/repos
  - orkka-api:GET /api/projects/{projectId}/repos
  - i18n:repoMisc
  - i18n:repoInsights
  - i18n:repoBrowser
  - i18n:sidebar
since: 2026-09-15
```
**Preconditions**: Project `e2e-repo-<stamp>` with the fixture web repo linked.
**Steps**:
1. Open `/p/{slug}/repos`.
**Expected**:
- Sidebar item "Repos" is highlighted; page title metadata is the repos title; heading "Repositories" with a ghost "Manage" button that opens the "Manage Repositories" modal (which reads "No repositories connected yet." when empty).
- RepoHubLayout lists the repo (selected by default or via "Select a repository..."); tabs "Code", "Commits", "Branches", "Pull Requests"; an "Open remote" link (title "Open orkka-e2e-fixture-web on the remote host") pointing at the GitHub URL.
- Buttons with `aria-label` "Clone repository", "Workspace commands" and "Remove repository" are present for the repo.

## REPO-002 Show the empty state when the project has no repositories
```yaml
priority: P2
surface: [ui]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/repos
  - i18n:repoMisc
since: 2026-09-15
```
**Preconditions**: Project with zero repos.
**Steps**:
1. Open `/p/{slug}/repos`.
**Expected**:
- Text "No repositories linked yet." in the hub layout; the GitHub connect card shows the "Add Repo" button; the local-folder card shows "Add local repository" with "Browse…".

## REPO-007 Browse the repository tree and lazily expand folders
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/repos
  - orkka-api:GET /api/projects/{projectId}/repos/{repoId}/tree
  - orkka-api:GET /api/projects/{projectId}/repos/{repoId}/files
  - i18n:repoMisc
  - i18n:fileViewer
since: 2026-09-15
```
**Preconditions**: REPO-001 project.
**Steps**:
1. Open the "Code" tab.
2. Expand the `app` (or `src`) folder.
**Expected**:
- `GET …/tree` renders root entries (`package.json`, `orkka.json`, folders); the viewer pane reads "Select a file to view its contents".
- Expanding issues `GET …/files?path=app` and inserts its children; an empty folder shows "Empty"; a failed row shows "Failed to load" with "Retry".

## REPO-008 Show a file's content in the viewer
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/repos/{repoId}/files/content
  - i18n:fileViewer
since: 2026-09-15
```
**Preconditions**: Code tab open.
**Steps**:
1. Click `orkka.json`.
**Expected**:
- `GET …/files/content?path=orkka.json&ref=main` → 200 with the manifest text; the viewer renders it with the `web` run target on port 3000; a zero-byte file shows "Empty file".

## REPO-009 Search file paths in the repository
```yaml
priority: P2
surface: [api, ui]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/repos/{repoId}/files/search
since: 2026-09-15
```
**Preconditions**: REPO-001 project.
**Steps**:
1. `GET …/files/search?q=orkka`.
2. `GET …/files/search?q=zzznomatch`.
**Expected**:
- Step 1: array containing `orkka.json`; step 2: empty array (200).

## REPO-010 List branches with the default and remote badges
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/repos/{repoId}/branches
  - i18n:branchList
since: 2026-09-15
```
**Preconditions**: Fixture repo has `main` plus a remote-only branch `e2e-remote-<stamp>` pushed with `git push origin HEAD:refs/heads/e2e-remote-<stamp>`.
**Steps**:
1. Open the "Branches" tab.
**Expected**:
- `main` carries the badge "default"; `e2e-remote-<stamp>` carries "remote" (title "Exists only on the repository's remote — not checked out locally yet"); "No branches found." only for an empty repo; failure shows "Failed to load branches."

## REPO-013 Change the default branch with the picker
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:PATCH /api/projects/{projectId}/repos/{repoId}
  - orkka-api:GET /api/projects/{projectId}/repos
  - i18n:defaultBranchPicker
since: 2026-09-15
```
**Preconditions**: Branch `e2e-branch-<stamp>` exists (REPO-011).
**Steps**:
1. In "Default branch" (title "Default branch — the base agents branch from and merge into") choose `e2e-branch-<stamp>`.
2. Choose `main` again.
**Expected**:
- `PATCH …/repos/{r} {defaultBranch:"e2e-branch-<stamp>"}` → 200; toast "Default branch set to e2e-branch-<stamp> — new tasks branch from and merge into it."; `GET …/repos` reflects it; a remote-only option is marked "remote" (title "…selecting it checks it out locally").
- Failure (route 500) → "Could not change the default branch." and the picker reverts.

## REPO-014 List commits with Load more and open a commit diff
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/repos/{repoId}/commits
  - orkka-api:GET /api/projects/{projectId}/repos/{repoId}/commits/{sha}/diff
  - i18n:repoMisc
since: 2026-09-15
```
**Preconditions**: Fixture repo has more commits than one page.
**Steps**:
1. Open the "Commits" tab; click "Load more".
2. Click the latest commit.
**Expected**:
- `GET …/commits?branch=main` renders sha, message, author name, date — each commit carries no `authorEmail` (Orkka no longer reads or sends author e-mail addresses); "Load more" fetches the next page (cursor/skip) and appends; "No commits found on this branch." for an empty branch.
- CommitDiffModal titled "Commit <short sha>" shows "N files changed" from `GET …/commits/{sha}/diff`; "No changes in this commit." for an empty commit.

## REPO-015 Filter the pull-request list by Open, Closed and All
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:GET /api/repos/{repoId}/pulls
  - i18n:repoMisc
since: 2026-09-15
```
**Preconditions**: Fixture repo has at least one open PR (`gh pr create` from a scratch branch) and one merged Orkka task PR.
**Steps**:
1. Open the "Pull Requests" tab; click "Open", then "Closed", then "All".
**Expected**:
- `GET /api/repos/{r}/pulls?state=…` per filter; rows show badges "Open" / "Merged" / "Closed" / "Draft", an "Open on GitHub" link, and "View Task" for PRs behind an Orkka task; empty states "No open pull requests found." / "No closed pull requests found." / "No pull requests found."

## REPO-016 Review a pull request from the PR modal
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:GET /api/repos/{repoId}/pulls/{number}
  - orkka-api:POST /api/repos/{repoId}/pulls/{number}/reviews
  - orkka-api:POST /api/repos/{repoId}/pulls/{number}/comments
  - i18n:repoMisc
since: 2026-09-15
```
**Preconditions**: An open non-task PR in the fixture repo.
**Steps**:
1. Click the PR row; in "Pull request #N" click "Comment" with the textarea empty.
2. Type "e2e comment"; click "Comment"; then click "Request Changes"; then "Approve".
**Expected**:
- Modal shows "N files changed", "Mergeable" or "Conflicts", "Files changed" list; the textarea placeholder "Leave a review comment (required unless approving)...".
- Step 1: no request (comment required). Step 2: `POST …/comments` → "Comment posted."; `POST …/reviews {decision:"request_changes"}` → "Changes requested — an Orkka task PR goes back to the dev agent with your feedback."; `POST …/reviews {decision:"approve"}` → "Approved — the review is recorded above. Orkka task PRs move to Done and merge automatically."
- With an API key lacking `CodeReviewReview`, both POSTs → 403.

## REPO-017 Land a pull request from the PR modal
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:POST /api/repos/{repoId}/pulls/{number}/merge
  - orkka-ui:/p/[projectId]/repos
  - i18n:forgePr
since: 2026-09-15
```
**Preconditions**: An open, approved Orkka PR record on a remote repo; runner online with its forge CLI.
**Steps**:
1. Click the PR row; click "Open pull request"; confirm.
**Expected**:
- `POST /api/repos/{r}/pulls/{n}/merge` → 200; the runner publishes the forge PR; the row shows "Published" and a "View pull request" link.
- API key lacking `CodeReviewMerge` → 403.

## REPO-018 Close a pull request without merging
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-api:POST /api/repos/{repoId}/pulls/{number}/close
  - i18n:repoMisc
since: 2026-09-15
```
**Preconditions**: Open scratch PR.
**Steps**:
1. Click "Close without merging"; read the hint; click "Close PR".
**Expected**:
- Hint "The branch and its commits are kept. Any task behind this PR is marked closed, not merged."; `POST …/close` → 200; badge "Closed"; the branch still exists on GitHub; failure shows "Could not close the pull request."

## REPO-019 Show a plain clone command for a remote and refuse it for a folder
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/repos/{repoId}/clone-access
  - orkka-ui:/p/[projectId]/repos
since: 2026-09-15
```
**Preconditions**: A project with one remote repo and one linked local folder.
**Steps**:
1. Click "Clone repository" on the remote; `POST …/clone-access` for it.
2. `POST …/clone-access` for the folder.
**Expected**:
- Remote: 200 with a plain `git clone <url>` — no token is minted or embedded.
- Folder: refused ("open it directly").

## REPO-021 Refuse clone access to an API key without ReposManage
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/repos/{repoId}/clone-access
since: 2026-09-15
```
**Preconditions**: API key with `["TasksCreate"]`.
**Steps**:
1. `POST …/clone-access`.
**Expected**:
- 403 (this GET-like endpoint is gated because it can return a credential); the UI hides nothing but the modal shows "Could not prepare clone access for this repository."

## REPO-022 Save the four workspace commands from the Workspace commands modal
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:PATCH /api/projects/{projectId}/repos/{repoId}
  - orkka-api:GET /api/projects/{projectId}/repos
  - i18n:reposPage
since: 2026-09-15
```
**Preconditions**: REPO-001 project.
**Steps**:
1. Click `aria-label="Workspace commands"`.
2. Fill "Setup command" `npm ci`, "Run command" `npm run dev`, "Build command" `npm run build`, "Check command" `npm run lint && npm test`; click "Save".
**Expected**:
- Modal title "Workspace commands — orkka-e2e-fixture-web"; each field shows its placeholder (`npm ci`, `npm run dev`, `npm run build`, `npm run lint && npm test`) and description; the secrets hint "Don't paste secrets here…" is present; "Cancel" closes without a request.
- `PATCH …/repos/{r} {defaultBranch, setupCommand, qaRunCommand, buildCommand, checkCommand}` → 200; `GET …/repos` returns the four values trimmed.
- Failure (route 500) → "Could not save the setup command. Please try again."

## REPO-023 Clear a workspace command by saving it empty
```yaml
priority: P2
surface: [api, ui]
automation: planned
refs:
  - orkka-api:PATCH /api/projects/{projectId}/repos/{repoId}
  - orkka-api:GET /api/projects/{projectId}/repos
since: 2026-09-15
```
**Preconditions**: REPO-022 values saved.
**Steps**:
1. `PATCH …/repos/{r} {defaultBranch:"main", setupCommand:"   ", checkCommand:""}`.
2. `PATCH …/repos/{r} {defaultBranch:"main"}` (other fields omitted).
**Expected**:
- Step 1: `setupCommand` and `checkCommand` become `null`; `qaRunCommand` and `buildCommand` are unchanged.
- Step 2: nothing but the branch is touched (null fields are "not provided", empty strings clear).

## REPO-024 Remove a repository after confirming
```yaml
priority: P0
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/repos
  - orkka-api:DELETE /api/repos/{repoId}
  - orkka-api:GET /api/projects/{projectId}/repos
  - i18n:reposPage
since: 2026-09-15
```
**Preconditions**: REPO-001 project.
**Steps**:
1. Click `aria-label="Remove repository"`; in the dialog titled "Remove repository" with "Remove this repository from the project?" click "Cancel".
2. Repeat and click "Remove".
**Expected**:
- Step 1: no request; the repo stays.
- Step 2: `DELETE /api/repos/{repoId}` → 204; the repo disappears from the list and from `GET …/repos`; the GitHub repository is untouched.

## REPO-026 Refuse repository writes from an API key without ReposManage
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/repos
  - orkka-api:PATCH /api/projects/{projectId}/repos/{repoId}
  - orkka-api:DELETE /api/repos/{repoId}
since: 2026-09-15
```
**Preconditions**: An API key without `ReposManage`.
**Steps**:
1. `POST …/repos {gitUrl, name, defaultBranch}`; `PATCH …/repos/{r} {defaultBranch}`; `DELETE /api/repos/{r}`.
**Expected**:
- Each → 403; nothing changes.

## REPO-027 List desktop editors for a repository and open it on my machine
```yaml
priority: P2
modes: [hybrid-repo]
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/repos
  - orkka-api:GET /api/projects/{projectId}/repos/{repoId}/editors
  - orkka-api:POST /api/projects/{projectId}/repos/{repoId}/open-in-editor
  - orkka-api:src/Modules/Orkka.Modules.Runners/Features/Editors/RepoEditorEndpoints.cs
since: 2026-09-15
```
**Preconditions**: REPO-001 project; the caller's runner online advertising `editor` with VS Code installed; a second repo id `R2` from another project.
**Steps**:
1. `GET /api/projects/{p}/repos/{r}/editors`.
2. On the Repos page open the repo card's open-in-editor control; pick "Visual Studio Code".
3. Stop the runner; repeat step 1. `GET /api/projects/{p}/repos/{R2}/editors`.
**Expected**:
- Step 1 → 200 `{supported:true, editors:[{key:"vscode", displayName:"Visual Studio Code"}, …]}`.
- Step 2: `POST …/open-in-editor {editor:"vscode"}` → 200 `{openedWith:"Visual Studio Code"}`; the editor opens the runner's persistent checkout of the repo (the same folder the Run button uses), not a task worktree.
- Step 3: offline runner → 200 `{supported:false, editors:[]}` (the control is hidden, no error); foreign repo → the same `supported:false` shape, never a 404 that would confirm the id.
**Notes**: always the caller's own runner — nobody can open a folder on a colleague's desktop. Task-worktree editors are in HYB-0xx (`GET /api/tasks/{taskId}/editors`); the Local host equivalents in LOC-031.
