---
area: RURL
title: Repositories by URL — any git remote, forge detection, runner access check
modes-default: [hybrid-repo]
---

## RURL-001 Add an https remote; the forge is detected from the host
```yaml
priority: P0
surface: [api]
automation: automated
refs:
  - orkka-api:POST /api/projects/{projectId}/repos
  - orkka-api:GET /api/projects/{projectId}/repos
since: 2026-10-26
```
**Preconditions**: An `e2e-*` project.
**Steps**:
1. `POST …/repos {name:"api", gitUrl:"https://github.com/acme/api.git", defaultBranch:"main"}`.
2. Repeat with `https://dev.azure.com/acme/Shop/_git/api`, `https://gitlab.com/acme/api.git`, `https://bitbucket.org/acme/api.git`, `https://git.example.com/acme/api.git`.
**Expected**:
- Each → 201; `provider` = `github`, `azuredevops`, `gitlab`, `bitbucket`, `other` respectively.
- No credential is requested or stored.

## RURL-002 Add ssh and scp-style remotes, including an ssh host alias
```yaml
priority: P0
surface: [api]
automation: automated
refs:
  - orkka-api:POST /api/projects/{projectId}/repos
since: 2026-10-26
```
**Preconditions**: An `e2e-*` project.
**Steps**:
1. `POST …/repos` with `ssh://git@github.com/acme/api.git`, then `git@github.com:acme/web.git`, then `git@github-work:acme/tool.git`.
**Expected**:
- All three → 201 as remotes (never classified as folders).
- `github` for the first two; the alias `github-work` → `other` unless `provider:"github"` is passed (RURL-004).

## RURL-003 Refuse a folder path in the URL field
```yaml
priority: P1
surface: [api, ui]
automation: automated
refs:
  - orkka-api:POST /api/projects/{projectId}/repos
  - i18n:addRepoByUrl
since: 2026-10-26
```
**Preconditions**: An `e2e-*` project.
**Steps**:
1. `POST …/repos {gitUrl:"C:\\code\\api"}`; then `file:///C:/code/api`.
2. Type `C:\\code\\api` into "Add repository by URL" on the Repos page.
**Expected**:
- Both API calls → 400 "Enter the repository's clone URL — https (https://github.com/acme/api.git) or ssh (git@github.com:acme/api.git)."
- The form shows "That is a folder path, not a remote. Use “Add local repository” to link a folder on your machine." and "Add repository" stays disabled.

## RURL-004 Override the forge and name a CLI account
```yaml
priority: P1
surface: [api]
automation: automated
refs:
  - orkka-api:POST /api/projects/{projectId}/repos
  - orkka-api:PATCH /api/projects/{projectId}/repos/{repoId}
since: 2026-10-26
```
**Preconditions**: An `e2e-*` project.
**Steps**:
1. `POST …/repos {gitUrl:"git@github-work:acme/tool.git", provider:"github", cliAccount:"dana-work", …}`.
2. `PATCH …/repos/{id} {defaultBranch:"main", provider:"gitlab"}`; then `{defaultBranch:"main", cliAccount:""}`.
**Expected**:
- Step 1: 201 `provider:"github"`, `cliAccount:"dana-work"`.
- Step 2: `provider` becomes `gitlab`; an empty `cliAccount` clears it (null).

## RURL-005 Refuse credentials embedded in an https URL
```yaml
priority: P0
surface: [api, ui]
automation: automated
refs:
  - orkka-api:POST /api/projects/{projectId}/repos
  - i18n:addRepoByUrl
since: 2026-10-26
```
**Preconditions**: An `e2e-*` project.
**Steps**:
1. `POST …/repos` with `https://dana:s3cret@github.com/acme/api.git`.
2. With `https://ghp_abc123@github.com/acme/api.git`, `https://glpat-xyz@gitlab.com/acme/api.git`, `https://x-access-token:t@github.com/acme/api.git`.
3. With `https://acme@dev.azure.com/acme/Shop/_git/api` (a plain user name).
**Expected**:
- Steps 1–2: 400 "Remove the credentials from the URL — your runner uses your own git sign-in."; nothing stored; the UI shows "This URL contains a password or token. Remove it — your runner uses your own git credentials."
- Step 3: 201 — a user name alone is not a secret.

## RURL-006 Validate name, URL length and default branch
```yaml
priority: P2
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/repos
since: 2026-10-26
```
**Preconditions**: An `e2e-*` project.
**Steps**:
1. `POST …/repos` with empty `name`; with a 2049-char URL; with empty `defaultBranch`.
**Expected**:
- 400 "Repository name is required."; 400 "Git URL must be 2048 characters or fewer."; 400 "Default branch is required."

## RURL-007 Classify the URL live in the Add repository by URL form
```yaml
priority: P1
surface: [ui]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/repos
  - orkka-ui:packages/ui/src/components/repos/AddRepoByUrlForm.tsx
  - i18n:addRepoByUrl
  - i18n:forges
since: 2026-10-26
```
**Preconditions**: Repos page of an `e2e-*` project.
**Steps**:
1. Open "Add repository by URL"; type `https://github.com/acme/api.git`, then `ssh://git@gitlab.com/acme/api.git`, then `git@github-work:acme/tool.git`.
**Expected**:
- Kind line "HTTPS remote" / "SSH remote" / "SSH remote (scp-style)"; Name prefills from the last path segment.
- Forge select: GitHub "Detected from github.com.", GitLab, then "Could not tell from github-work. If it is an SSH host alias, pick the forge it points to." with Other selected.
- "CLI account (optional)" field with its hint; "Add repository" → "Adding…" → the repo appears in the list.

## RURL-008 Check repo access from my runner: readable
```yaml
priority: P0
surface: [api, ui]
automation: automated
refs:
  - orkka-api:GET /api/projects/{projectId}/repos/access-check
  - i18n:repoAccessCheck
since: 2026-10-26
```
**Preconditions**: Runner online; a project with the fixture remote the runner's git can read.
**Steps**:
1. Click "Check access from my runner" (or `GET …/repos/access-check`).
**Expected**:
- 200 `{supported:true, repos:[{repoId, name, ok:true, reason:null, advice:null}]}`; the UI shows "Checking…" then "<name>: your runner can read it".

## RURL-009 Check repo access from my runner: not signed in / not found
```yaml
priority: P0
surface: [api, ui]
automation: automated
refs:
  - orkka-api:GET /api/projects/{projectId}/repos/access-check
  - i18n:repoAccessCheck
since: 2026-10-26
```
**Preconditions**: Runner online; a project with a remote the developer's git cannot read (a private repo of another account, or a non-existent path).
**Steps**:
1. `GET …/repos/access-check`.
**Expected**:
- 200 `{supported:true, repos:[{ok:false, reason, advice}]}` — `advice` tells them to sign git in on that machine (credential manager / `gh auth login` / ssh key); the UI shows "<name>: your runner cannot read it" with the advice.

## RURL-010 Check repo access while the runner is offline → supported:false
```yaml
priority: P1
surface: [api, ui]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/repos/access-check
  - i18n:repoAccessCheck
since: 2026-10-26
```
**Preconditions**: Runner stopped.
**Steps**:
1. `GET …/repos/access-check`; click the button.
**Expected**:
- 200 `{supported:false, repos:[]}`; the UI says "Your runner did not answer — start it with `./orkka start` to check access." — never an error banner.

## RURL-011 The first task clones the remote on the runner with the developer's git
```yaml
priority: P0
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/tasks/{taskId}/submit
costUsd: 0
since: 2026-10-26
```
**Preconditions**: Runner online; a remote added by URL; nothing cloned yet under `~/.orkka/repos/<org>/<repo>`.
**Steps**:
1. Submit a task on that repo.
**Expected**:
- `~/.orkka/repos/<org>/<repo>/bare` appears (a mirror) and `worktrees/<KEY-n>` a standalone clone whose origin is the remote; no Orkka-held credential is used (the platform has none).

## RURL-012 A clone that fails tells the developer to sign git in on that machine
```yaml
priority: P1
surface: [api, ui]
automation: planned
refs:
  - orkka-api:GET /api/tasks/{taskId}/messages
since: 2026-10-26
```
**Preconditions**: Runner online; a remote the developer's git cannot read.
**Steps**:
1. Submit a task on it.
**Expected**:
- The task stops with a thread message from `RepoAccessDiagnostics` naming the remote and the remedy (sign in with the credential manager / `gh auth login`, or add the ssh key); no retry loop spends anything.
