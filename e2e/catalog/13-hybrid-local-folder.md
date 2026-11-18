---
area: HYF
title: Hybrid — local folder on your machine as a project repo
modes-default: [hybrid-folder]
---

## HYF-001 Browse this machine's drives and folders through the runner
```yaml
priority: P0
surface: [api]
automation: automated
refs:
  - orkka-api:POST /api/runners/fs/browse
since: 2026-09-15
```
**Preconditions**: Runner online with `local-folders`; a git repo at `C:\e2e\folder-<stamp>` and a plain folder `C:\e2e\plain-<stamp>`.
**Steps**:
1. `POST /api/runners/fs/browse {path:null}`.
2. `POST /api/runners/fs/browse {path:"C:\\e2e"}`.
**Expected**:
- Step 1: 200 `{supported:true, path:null, parentPath:null, entries:[{name:"C:\\", fullPath:"C:\\", isGitRepo:false}, …]}`.
- Step 2: `path:"C:\\e2e"`, `parentPath:"C:\\"`, entries include `folder-<stamp>` with `isGitRepo:true` and `plain-<stamp>` with `isGitRepo:false`.

## HYF-002 Return supported:false from browse while the runner is offline
```yaml
priority: P1
surface: [api, ui]
automation: planned
refs:
  - orkka-api:POST /api/runners/fs/browse
  - orkka-ui:/p/[projectId]/repos
  - i18n:folderPicker
since: 2026-09-15
```
**Preconditions**: Runner stopped.
**Steps**:
1. `POST /api/runners/fs/browse {path:null}`.
2. On `/p/{slug}/repos` click "Browse…".
**Expected**:
- Step 1: 200 `{supported:false, entries:[]}`.
- Step 2: picker shows "Start Orkka on your machine (`./orkka start`) to browse folders there."; nothing to select.

## HYF-003 Initialise a new git repository in a folder on this machine
```yaml
priority: P1
surface: [api, cli]
automation: planned
refs:
  - orkka-api:POST /api/runners/fs/init-repo
since: 2026-09-15
```
**Preconditions**: Runner online.
**Steps**:
1. `POST /api/runners/fs/init-repo {parentPath:"C:\\e2e", name:"init-<stamp>"}`.
2. `POST /api/runners/fs/init-repo {parentPath:"", name:""}`.
**Expected**:
- Step 1: 200 `{supported:true, success:true, path:"C:\\e2e\\init-<stamp>", defaultBranch:"main"}`; on disk the folder has `.git`, a seeded `.gitignore` and at least one commit (`git log --oneline | wc -l` ≥ 1 — bare mirrors and worktrees need a commit).
- Step 2: 400 "A parent folder and a name are required."
- Offline variant: 422 "Your runner is not connected — start it on your machine with `./orkka start` to work with folders that live there."

## HYF-004 Link a git folder to the project as a LocalPath repo
```yaml
priority: P0
surface: [api, ui]
automation: automated
refs:
  - orkka-api:POST /api/projects/{projectId}/repos/local
  - orkka-api:GET /api/projects/{projectId}/repos
  - orkka-ui:/p/[projectId]/repos
  - i18n:localRepos
since: 2026-09-15
```
**Preconditions**: Project `e2e-hyf-<stamp>`; runner online; `C:\e2e\folder-<stamp>` is a git repo with one commit.
**Steps**:
1. `POST /api/projects/{p}/repos/local {path:"C:\\e2e\\folder-<stamp>"}`.
2. Open `/p/{slug}/repos`.
**Expected**:
- Step 1: 200 with `provider:"local"`, `gitUrl:"C:\\e2e\\folder-<stamp>"`, `defaultBranch:"main"`; `GET …/repos` lists it (the UI reads `provider === "local"`).
- Step 2: the repo row appears with the LocalRepoRemotePanel hint "Publish `folder-<stamp>` to a remote whenever you are ready — the folder stays your working copy." and the "Connect & push" button (no "Let Orkka host it").
**Notes**: cleanup deletes the project; the folder is left untouched.

## HYF-005 Turn a plain folder into a git repository when linking it
```yaml
priority: P1
surface: [api, cli]
automation: automated
refs:
  - orkka-api:POST /api/projects/{projectId}/repos/local
since: 2026-09-15
```
**Preconditions**: `C:\e2e\plain-<stamp>` contains a `README.md` and no `.git`.
**Steps**:
1. `POST /api/projects/{p}/repos/local {path:"C:\\e2e\\plain-<stamp>"}`.
**Expected**:
- 200 `provider:"LocalPath"`; the folder now has `.git`, a `.gitignore` seeded from detected build markers, and an initial commit containing `README.md`.

## HYF-006 Reject linking a folder that does not exist or with no path
```yaml
priority: P2
surface: [api]
automation: automated
refs:
  - orkka-api:POST /api/projects/{projectId}/repos/local
since: 2026-09-15
```
**Preconditions**: Runner online.
**Steps**:
1. `POST …/repos/local {path:"C:\\e2e\\does-not-exist"}`.
2. `POST …/repos/local {path:""}`.
3. `POST …/repos/local {path:"C:\\e2e\\folder-<stamp>"}` with an API key lacking `ReposManage`.
**Expected**:
- Step 1: 422 "That folder does not exist on your machine: C:\e2e\does-not-exist".
- Step 2: 400 "A folder path is required."
- Step 3: 403.

## HYF-007 Reject linking with 422 while the runner is offline or too old
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/repos/local
since: 2026-09-15
```
**Preconditions**: Runner stopped.
**Steps**:
1. `POST …/repos/local {path:"C:\\e2e\\folder-<stamp>"}`.
**Expected**:
- 422 "Your runner is not connected — start it on your machine with `./orkka start` to work with folders that live there."
- Old-runner variant (daemon without `local-folders`, `Supported:false`): 422 "Linking a folder needs a newer runner on that machine — update Orkka (`git pull`, then `./orkka start`) and try again." — manual, needs an old build.

## HYF-008 Link local repositories from the Repos page picker
```yaml
priority: P0
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/repos
  - orkka-ui:apps/local/components/repos/LocalFolderPickerModal.tsx
  - orkka-api:POST /api/runners/fs/browse
  - orkka-api:POST /api/projects/{projectId}/repos/local
  - i18n:localRepos
  - i18n:folderPicker
since: 2026-09-15
```
**Preconditions**: Runner online; project without repos.
**Steps**:
1. On `/p/{slug}/repos` read the "Add local repository" box (hint "Work from a folder on your own machine. Orkka links it as a repository — creating the git repo if it doesn't have one yet — and your agents work there."); click "Browse…".
2. In "Link local repositories" navigate to `C:\e2e`, check `folder-<stamp>`, click "Link 1 repository".
**Expected**:
- Footer hint "Select folders to link — one that isn't a repository yet becomes one".
- Row status "Linking..." then success; `POST …/repos/local` issued once; modal closes and the repo appears in the list.
- Failure variant (folder removed between browse and link): the row shows the API detail and the modal stays open.

## HYF-010 Read the sync status of a linked folder
```yaml
priority: P1
surface: [api]
automation: automated
refs:
  - orkka-api:GET /api/projects/{projectId}/repos/{repoId}/sync-status
since: 2026-09-15
```
**Preconditions**: HYF-004 repo without remote; runner online.
**Steps**:
1. `GET …/repos/{r}/sync-status`.
2. Stop the runner; repeat.
**Expected**:
- Step 1: 200 `{supported:true, isGitRepo:true, hasRemote:false, hasUpstream:false, ahead:0, behind:0, branch:"main", remoteUrl:null}`.
- Step 2: 200 `{supported:false, …}`; the UI panel shows "Start Orkka on that machine (`./orkka start`) to manage this folder."

## HYF-011 Show the ahead-by count and push local commits
```yaml
priority: P0
surface: [ui, api, cli]
automation: automated
refs:
  - orkka-ui:/p/[projectId]/repos
  - orkka-api:GET /api/projects/{projectId}/repos/{repoId}/sync-status
  - orkka-api:POST /api/projects/{projectId}/repos/{repoId}/push
  - i18n:localRepos
since: 2026-09-15
```
**Preconditions**: HYF-009 published repo.
**Steps**:
1. In the folder: `git commit --allow-empty -m "e2e <stamp>"`; reload `/p/{slug}/repos`.
2. Click "Push".
**Expected**:
- Step 1: sync-status `ahead:1`; panel text "1 commit to push on main"; "Push" enabled. Two commits read "2 commits to push on main".
- Step 2: button "Pushing…"; `POST …/push {branch:"main"}` → 200; notice "Pushed."; `git ls-remote origin main` equals local HEAD; panel returns to "main is up to date with origin".

## HYF-012 Refuse pushing a folder that has no remote yet
```yaml
priority: P2
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/repos/{repoId}/push
since: 2026-09-15
```
**Preconditions**: HYF-004 repo, never published.
**Steps**:
1. `POST …/repos/{r}/push {}`.
**Expected**:
- 422 "This folder has no remote yet — connect it to GitHub first."

## HYF-013 Connect the folder to an empty remote the user created and push
```yaml
priority: P1
surface: [ui, api]
automation: automated
refs:
  - orkka-api:POST /api/projects/{projectId}/repos/{repoId}/attach-remote
  - orkka-ui:/p/[projectId]/repos
since: 2026-09-15
```
**Preconditions**: An empty repository the user created on their forge (no README/.gitignore/licence) that their own git can push to; HYF-004 repo without a remote. Locally: an empty bare repo.
**Steps**:
1. Paste its URL (https or ssh) into "Connect a repository you created"; click "Connect & push".
**Expected**:
- "Connect & push" is disabled while the input is blank; shows "Connecting…" while pending.
- `POST …/attach-remote {remoteUrl}` → 200 `{remoteUrl}`; notice "Pushed to <url>"; the remote now has the folder's `main`, pushed by the runner with the developer's credentials.
**Notes**: cleanup deletes the remote.

## HYF-014 Reject connecting to a remote that already has commits
```yaml
priority: P1
surface: [api, ui]
automation: automated
refs:
  - orkka-api:POST /api/projects/{projectId}/repos/{repoId}/attach-remote
  - i18n:localRepos
since: 2026-09-15
```
**Preconditions**: `gh repo create acme-dev/e2e-nonempty-<stamp> --private --add-readme`; App has access.
**Steps**:
1. `POST …/attach-remote {remoteUrl:"https://github.com/acme-dev/e2e-nonempty-<stamp>"}`.
**Expected**:
- 422 whose message says "already has commits in it. Create the repository on GitHub WITHOUT a README, .gitignore or licence — Orkka pushes your folder's own history, which cannot merge into a repo that already has its own."
- UI shows the message in the panel error (falls back to "Could not connect the repository to GitHub." only when the body has none); the folder's `origin` is not left pointing at the rejected remote.

## HYF-015 Reject a malformed remote URL or one the runner cannot push to
```yaml
priority: P2
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/repos/{repoId}/attach-remote
since: 2026-09-15
```
**Preconditions**: HYF-004 repo; runner online.
**Steps**:
1. `POST …/attach-remote {remoteUrl:"not a url"}`.
2. `POST …/attach-remote {remoteUrl:"https://github.com/<user>/does-not-exist-<stamp>"}`.
**Expected**:
- Step 1: 400 "Enter the URL of an empty repository you created — https (https://github.com/you/your-repo) or ssh (git@github.com:you/your-repo.git)."
- Step 2: 422 with the runner's git error (repository not found / authentication) — no GitHub App wording.

## HYF-018 Land the merge in the working copy when the tree is clean and main is checked out
```yaml
priority: P0
surface: [api, cli, ui]
automation: automated
refs:
  - orkka-api:POST /api/tasks/{taskId}/pull-request/reviews
  - orkka-api:GET /api/tasks/{id}
  - orkka-ui:/p/[projectId]/tasks/[taskId]/review
since: 2026-09-15
```
**Preconditions**: HYF-016 task at `WaitingCodeReview` (`autoApproveCodeReview:false`); the folder has `main` checked out and `git status --porcelain` empty.
**Steps**:
1. On the review page click "Approve".
**Expected**:
- Task reaches Done; in the folder `git log -1 --format=%s` is the task's merge/squash commit, `git status --porcelain` is still empty and `README.md` on disk contains `e2e-<token>` (fast-forward of the working tree via `receive.denyCurrentBranch=updateInstead`).

## HYF-019 Refuse the merge with a reason while the working tree is dirty
```yaml
priority: P0
surface: [api, cli, ui]
automation: planned
refs:
  - orkka-api:POST /api/tasks/{taskId}/pull-request/reviews
  - orkka-api:GET /api/tasks/{id}
  - orkka-api:GET /api/tasks/{taskId}/messages
  - orkka-ui:/p/[projectId]/board
  - i18n:taskCard
since: 2026-09-15
```
**Preconditions**: Another HYF-016-style task at `WaitingCodeReview`; in the folder create an uncommitted edit to a tracked file.
**Steps**:
1. Approve the PR.
2. Read the task thread; then `git stash` in the folder and re-trigger the merge (retry from the task actions or approve again).
**Expected**:
- Step 1: the merge is refused; the board card shows "Merge failed"; the thread carries git's own reason mentioning unstaged/uncommitted changes; the working tree is untouched (the local edit survives).
- Step 2: the merge succeeds and the task reaches Done.

## HYF-020 Update the ref only when the base branch is not checked out in the folder
```yaml
priority: P1
surface: [api, cli]
automation: planned
refs:
  - orkka-api:POST /api/tasks/{taskId}/pull-request/reviews
  - orkka-api:GET /api/tasks/{id}
since: 2026-09-15
```
**Preconditions**: Task at `WaitingCodeReview`; in the folder `git switch -c scratch-<stamp>` (main not checked out).
**Steps**:
1. Approve the PR; wait for Done.
**Expected**:
- `git rev-parse main` in the folder advanced to the merge commit while the checked-out `scratch-<stamp>` branch and the working tree are unchanged (plain ref update).

## HYF-021 Create task worktrees next to the folder and remove them after merge
```yaml
priority: P1
surface: [cli, api]
automation: planned
refs:
  - orkka-api:GET /api/tasks/{id}
  - orkka-api:GET /api/tasks/{taskId}/executions
since: 2026-09-15
```
**Preconditions**: HYF-016 task running.
**Steps**:
1. While the task is in Developing list `C:\e2e\folder-<stamp>-worktrees\`.
2. After the task is Done list it again.
**Expected**:
- Step 1: a directory `task-…` exists there and `git -C <it> branch --show-current` is `orkka/task-…`; nothing is written under `~/.orkka/repos` for this repo's dev worktree.
- Step 2: the task worktree is gone (`git worktree list` in the folder no longer lists it).

## HYF-022 Remove a linked folder repo from the project without touching the disk
```yaml
priority: P0
surface: [ui, api, cli]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/repos
  - orkka-api:DELETE /api/repos/{repoId}
  - i18n:reposPage
since: 2026-09-15
```
**Preconditions**: HYF-004 repo listed.
**Steps**:
1. Click the button labelled "Remove repository"; in the dialog "Remove this repository from the project?" click "Remove".
**Expected**:
- `DELETE /api/repos/{repoId}` → 204; the row disappears; `GET …/repos` no longer lists it.
- `C:\e2e\folder-<stamp>` still exists with its `.git` and files; a `{repo}-worktrees` sibling, if present, is left as-is.

## HYF-024 Run a local-folder task end to end at zero cost
```yaml
priority: P0
surface: [ui, api, cli]
automation: automated
costUsd: 0
refs:
  - orkka-ui:/p/[projectId]/board
  - orkka-api:POST /api/projects/{projectId}/tasks
  - orkka-api:POST /api/tasks/{id}/submit
  - orkka-api:GET /api/tasks/{id}
  - orkka-api:GET /api/usage/breakdown
since: 2026-09-15
```
**Preconditions**: HYF-004 repo (no remote); `autoApprovePlan` and `autoApproveCodeReview` on; folder clean on `main`. A folder has no `origin/HEAD`, so its orkka.json does not vouch for its setup/check commands: the runner refuses them (task-thread note naming `./orkka commands approve <id>`, RUN-030) unless approved on this machine — the task still lands either way.
**Steps**:
1. Create and submit "Add a line 'e2e-<token>' to README.md"; watch the board until Done (timeout 25 min).
**Expected**:
- Lanes Drafting → Ready → Planning → Developing → CodeReview → Done; task `Completed`.
- `README.md` in the folder contains `e2e-<token>`; `git log -1` is the task's merge commit; no remote was needed (no push attempted).
- `GET /api/usage/breakdown?groupBy=task` has the task's tokens with no charge (runner work records a cost of 0).
