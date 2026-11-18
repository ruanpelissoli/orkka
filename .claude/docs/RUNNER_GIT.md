# RUNNER_GIT.md — git on the developer's machine

All git happens on the developer's runner, through the one git engine (`src/Orkka.Git`,
`GitProcess`). Orkka holds **no forge credential** — no GitHub App, no installation, no
webhooks, no stored token — and the host never clones a repository.

## Layout

```
~/.orkka/repos/{org_id}/{repo_id}/
  bare/            bare mirror: object cache for clones + read-only ops (ls-remote, merge-tree, browsing)
  worktrees/
    {KEY-n}/       standalone clone per task (own .git and refs; origin = the remote or the linked folder)
  merge-scratch/   throwaway clones for local-folder squash merges
```

Clones, not linked worktrees: every linked worktree shared the mirror's ref store, so parallel
tasks, chat workspaces, GC and the agent's own git raced on one set of refs. A clone owns its
refs (`--reference-if-able bare --dissociate` makes it near-instant; `--filter=blob:none`
against a remote) and can be deleted and rebuilt from the remote at any moment. Legacy linked
worktrees (a `.git` FILE) are still reused and removed correctly.

Repositories linked from a local folder one by one keep their task folders next to the folder
(`C:\Code\my-app` → `C:\Code\my-app-worktrees\…`) so the developer can watch. Repositories of
a **base-folder project** never do: the first `EnsureBareClone` that carries
`LocalCheckoutPath` writes a sticky `base-folder-checkout` marker next to the mirror, and from
then on that repo's task folders stay in `{repo}/worktrees` under the cache — a sibling folder
would land inside the developer's base folder.

### Mirror seeding from the developer's checkout

`EnsureBareCloneRequest.LocalCheckoutPath` (additive, capability `repo-discovery`) names the
developer's own checkout of a remote in the project's base folder (`RepoSummary.LocalCheckoutPath`
= `BaseFolder` + `FolderPath`). A FIRST mirror is then seeded from it: `git clone --mirror
<checkout>`, `remote set-url origin <remote>`, `remote.origin.mirror` unset, the fetch refspec set
to `+refs/heads/*:refs/remotes/origin/*`, then a fetch from the remote — so the bulk of the objects
come from the disk. Any failing step deletes the half-made mirror and falls back to the normal
clone (which then reports the real reason). Nothing is written to the checkout.

## Credentials and identity

- `GitProcess.DeveloperMachine`: no credential-helper clearing, no forced identity. Clone, fetch
  and push use the developer's own sign-in — credential manager or `gh` login for https, keys
  and `~/.ssh/config` aliases for ssh. Prompts are off (`GIT_TERMINAL_PROMPT=0`,
  `GIT_SSH_COMMAND=ssh -o BatchMode=yes` unless the developer set one), every call has a
  deadline, logs are redacted, every operation lands in the `sourcecontrol.git_operations` ledger.
- A clone that fails tells the developer to sign git in on that machine
  (`RepoAccessDiagnostics`); `GET /api/projects/{id}/repos/access-check` asks the runner up front.
- Commits are the developer's: the workspace's identity
  (`./orkka workspace set-identity <name> --name … --email …` → `ORKKA_GIT_AUTHOR_NAME/EMAIL`
  in its `profile.env` → `Git:CommitAuthor*`), written into each clone's config because an
  `includeIf "gitdir:…"` rule never matches clones under `~/.orkka/repos`; else their global
  git config. The command guard refuses an agent's attempt to change it.
- A repo URL with embedded credentials is refused when the repo is added.
- **Origins are read as configured** (`git config --get remote.origin.url`), never with `remote
  get-url`, which applies the developer's `url.*.insteadOf` rules: a GitHub origin rewritten to a
  mirror or a path is still a GitHub repository with a forge PR. Git applies the rewrite to the
  clone, fetch and push themselves. The runner's own `GIT_CONFIG_*` settings (`core.longpaths`, …)
  are appended after the pairs its environment already carries, never over them.
- Runner protocol v4 (now v5): runners must advertise `developer-git` or the host refuses git work with
  "update Orkka". The Hello reports `ForgeClis` (gh/az/glab installed, signed in,
  account names — never tokens).

## Branches

Every task forks from, and targets, the repo's `DefaultBranch`, recorded as the task's base when
its worktree is created (`SetTaskBaseBranchAsync`) so a later default-branch change cannot
retarget open work. The task branch is `orkka/{KEY}-{n}`, or for a tracker ticket the source's
branch template (`TrackerBranchName.Render`, default `{type}/{key}-{slug}` →
`feature/SHOP-12-add-invoice-export`), so Jira's development panel and ADO link the branch and its
PR to the ticket. Branch → task lookups use `TaskEntity.BranchName`, never a prefix.

**A Story's shared branch.** The members of a tracker Story share ONE branch (the Story's
`branch_name`, per repository). Each member is still its own clone: the first is cut from the
default branch, every later one continues `origin/{branch}` as the previous member pushed it (a
linked folder is the clone's origin, so the same holds). A member records `ReviewBaseSha` when its
clone is made (`ResolveReviewBase`: the remote head, else the merge-base) and every diff Orkka
shows of it is its own change (`GetDiffSinceAsync`, `GetDiffRequest.BaseSha`; live, mid-run: `GetWorkingTreeDiffAsync(baseSha:)`, `GetWorkingTreeDiffRequest.BaseSha`): `ReviewBaseSha..HEAD`,
or — when a base sync merged the default branch into the shared branch since — the tree of
`ReviewBaseSha` merged with the newest synced default-branch commit (`git merge-tree --write-tree`,
the second parent of the newest first-parent merge since the base) against HEAD
(`ReviewDiffBaseAsync`). A reset member keeps the shared branch and re-records its base after its
earlier commits; nothing is ever force-pushed. Because the
branch is shared, pull requests are created for an explicit task (`CreateForTaskAsync`) and branch
lookups prefer the member still in flight; nothing deletes the shared branch, and removing a
landed member's clone leaves it on the remote.

## Who pushes

**The platform pushes, never the agent.** The command guard refuses `git push`, new remotes and
forge CLIs inside agent runs. After every run the runner pushes the task branch
(`EnsureBranchPushedAfterRunAsync`); for a linked local folder the worktree's origin IS the
folder, so the push is a ref update into it. A phase never completes on a failed push.

## Pull requests

Orkka's own review record (`IPullRequestService` / `OrkkaPullRequestService`,
`sourcecontrol.local_pull_requests`, `orkka-local://pull/N`) carries the diff view, line comments
and approval for every repo — the diff is always read live from the runner. Approving lands the
work:

- **Remote repo → publish.** The runner pushes and opens a forge pull request with the
  developer's own CLI — `gh pr create`, `az repos pr create` or `glab mr create`, picked by
  `ProjectRepo.ForgeKind` (detected from the host, with a manual override for ssh aliases);
  idempotent per head branch; `GH_TOKEN` scoped to `ProjectRepo.CliAccount` for multi-account
  `gh`. **Orkka never merges on a forge** — the team reviews and merges that PR. Its URL lands on
  `LocalPullRequest.ForgeUrl` and `TaskEntity.PublishedPrUrl`.
- **Linked local folder → squash merge** through a scratch clone and a push into the folder
  with `receive.denyCurrentBranch=updateInstead`, passed to the receiving git for that one push (`push --receive-pack="git -c receive.denyCurrentBranch=updateInstead receive-pack"`) and never written to the user's `.git/config`: a ref update when the base is not checked out,
  a fast-forward of the working tree when it is checked out and clean, a refusal with the reason
  when it is dirty. One merge at a time per folder.

**Story pull requests** (`StoryLandingService`, runner capability `stories`). A Story member's
Orkka pull request "merges" by landing on the shared branch (already pushed). The first landing
opens the forge pull request as a **draft** (`gh pr create --draft`, `az repos pr create --draft
true`, `glab mr create --draft`), each later one rewrites its checklist (`gh pr edit
--body-file`, `az repos pr update --description`, `glab mr update --description`), and the last
one — once the shared branch merges cleanly into the default branch of EVERY repository of the
story (a conflicting one is resolved there first, in its last member's clone) — marks them all
**ready** (`gh pr ready`, `az repos pr update --draft false`, `glab mr update --ready`; `--undo` /
`--draft true` / `--draft` turn it back when a sub-task joins late). A runner without `stories` is refused with
"update Orkka" rather than opening a ready pull request. A linked folder gets the whole
shared branch squash-merged at the end.

## Repository commands

`setup` (once per task worktree), `check` (the quality gate after every dev run), `build` (merge
verification) and run targets come from the repository's `orkka.json` on its default branch, or
from the project's settings with the developer's approval on the machine — see SECURITY.md,
"What the host can ask the runner to do".
