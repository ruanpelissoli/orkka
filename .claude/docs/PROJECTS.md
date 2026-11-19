# PROJECTS.md — Orkka Projects

## One kind of project

There is a single project type. A project is a name, a slug, a short `Key` (task numbers are
`{KEY}-n`), settings (`ProjectSettings`, JSON in `projects.projects.Settings`) and its
repositories. Orkka never creates, scaffolds, hosts or deploys the code — the repos are
the developer's own, and Orkka's job is the board, the pipeline and the agents that work them.

(The old Managed/Unmanaged split, hosted apps, provisioning and the brief-to-roadmap flow were
removed in P0 of the pivot, the GitHub App in P1 — see `.claude/CLAUDE.md` → Product Vision.
Jira/Azure DevOps/GitHub Projects ticket sources and workspaces per job came after — see CLAUDE.md.)

## Creating a project

`POST /api/projects` (`CreateProjectEndpoint`, `ProjectsCreate`) — both it and
`POST /api/projects/from-folder` (below) go through `ProjectCreator`:

1. Slug and key — validated for uniqueness when given, generated from the name otherwise
   (`ProjectIdentifierService`).
2. Settings start from `Mappers.DefaultSettings`, with the project language taken from the
   creator's UI locale (`ProjectSettings.Language`, later resolved by `ProjectLanguage.Resolve`).
3. `ProjectCreated` is published and the Agents module seeds the pod
   (`AgentSeedOnProjectCreated`) — no cross-module call from the endpoint.

A blank project gets its repositories linked afterwards. The primary way in is a base folder.

## Projects from a base folder

The developer points Orkka at ONE folder on their machine (Runners module, `BaseFolderEndpoints`):

- `POST /api/runners/fs/discover { path }` — preview, relayed to the caller's runner
  (`/internal/runner/fs/discover` → runner op `DiscoverRepos`, capability `repo-discovery`;
  `supported: false` with the reason when the runner is offline or too old). The runner
  (`RepoDiscovery`) refuses a relative path, a drive root and the home folder itself, and only
  asks git questions — it never runs `git init` or writes anything.
  - **Monorepo**: the folder itself has `.git` → one repo (`relativePath ""`) plus its
    top-level directories as `areas`.
  - **Folder of repos**: `.git` dirs/files up to two levels down; hidden folders,
    `node_modules`, `bin`, `obj`, `*-worktrees`, `.orkka` and anything inside a repo already
    found (submodules) are skipped.
  - Per repo: relative path, origin (credentials stripped; null = none), default branch
    (`origin/HEAD`, else the current branch), current branch, dirty, unpushed commits, has
    commits, a description (`orkka.json` `description`, else the README's first paragraph, ≤ 300
    chars) and a stack hint (`RepoStackClassifier`).
- `POST /api/projects/from-folder { path, name? }` (`ProjectsCreate`): discovers, creates the
  project (name defaults to the folder name) with `Project.BaseFolder` + `BaseFolderShape`
  (Monorepo/MultiRepo), registers every repo through `IProjectsApi.AddDiscoveredRepoAsync` and
  primes each mirror (`ensure-clone` with the checkout path). Returns the project, its repos and
  warnings (no commits yet → skipped; a dirty local-folder repo cannot land until clean). 422
  when the runner is offline, naming `./orkka start`.
  - **A repo with an origin is a remote**: `GitUrl` = the origin, so it gets a branch push and a
    forge PR through the developer's CLI, exactly like a manually added remote.
  - **A repo without one is a local folder**: `GitUrl` = its absolute path, squash-merged into
    it as today.
  - `ProjectRepo.FolderPath` is relative to the base, forward slashes, `""` for a monorepo,
    never `..` (`BaseFolders.NormalizeFolderPath` + `RepoRelativePath.IsSafeRelative`);
    `ProjectRepo.Description` is the routing hint agents see next to each sibling (editable:
    `PATCH /api/projects/{id}/repos/{repoId}` with `description`, ≤ 300).
- `POST /api/projects/{id}/rescan[?auto=true]` (`ReposManage`): new repos are added; a vanished
  one gets `MissingSince` and its in-flight tasks a thread note (nothing is stopped); a
  reappearing one is cleared; a changed origin updates `GitUrl` only when no task on that repo
  is in flight, otherwise a warning is returned. The UI calls it with `auto=true` when the
  project is opened: at most one real rescan per project per 10 minutes (the last result is
  returned meanwhile, `skipped: true`), and an offline runner is a skip, not an error. A base
  folder that cannot be read at all changes nothing. The response also carries each repo's
  live dirty/unpushed/current-branch state for the Repos page.

The developer's checkouts are only ever read: mirrors and task folders live under
`~/.orkka/repos` (RUNNER_GIT.md), and a remote's first mirror is seeded from the checkout.
Agents on a folder-of-repos project may read the siblings (SECURITY.md → Sibling repositories).

## Repositories

A `ProjectRepo` is `GitUrl` + `DefaultBranch` + `ForgeKind` + `CliAccount` plus the per-repo
commands below. Orkka holds **no forge credential**: every clone, fetch and push runs on the
developer's runner with their own git sign-in (credential manager / `gh` login for https, keys
for ssh). `GitUrl` is stored verbatim and `RepoUrl.Classify` (SharedKernel) reads it every time
as `RepoLocation.Remote` or `LocalFolder` (`RepoUrl.DetectForge`, `ParseRef`,
`HasEmbeddedCredential` alongside). Two ways in:

- **A remote** (`POST /api/projects/{id}/repos`) — any URL the developer's git can reach: https,
  ssh, scp-style, `~/.ssh/config` host aliases. The validator refuses folders and URLs with
  embedded credentials. `ForgeKind` (github / azuredevops / gitlab / bitbucket / other) is
  detected from the host and can be overridden, because an ssh alias (`git@github-work:org/app.git`)
  hides the forge; it picks the CLI that publishes pull requests. `CliAccount` is the `gh`
  account (or ADO organization) to act as when the developer has several; null = the CLI's
  active account.
- **A folder on the developer's machine** (Runners module → `LocalRepoEndpoints`): browse the
  machine's folders through the connected runner (`POST /api/runners/fs/browse`), `git init`
  one if needed (`/api/runners/fs/init-repo`), link it (`POST /api/projects/{id}/repos/local`).
  The folder can later be given a remote the developer created (`…/repos/{repoId}/attach-remote`)
  and pushed (`…/push`, `…/sync-status`) — on the runner, with their credentials.

All repo browsing (tree, files, commits, branches) goes through the current user's runner:
`RepoReaders.Current` relays to `ILocalRepoReader` whenever a runner is reachable; branch
writes from the Repo page refuse ("managed in your own git"). Before any work,
`GET /api/projects/{id}/repos/access-check` asks the runner whether it can reach each remote.
A clone that fails tells the developer to sign git in on their machine (`RepoAccessDiagnostics`).

`POST /api/projects/{id}/repos/{repoId}/clone-access` (`ReposManage`) returns the plain
`git clone <url>` — the developer's own git credentials do the work. A local folder answers
with "open it directly" instead.

### Per-repo commands

| Field | Purpose |
|---|---|
| `SetupCommand` | Run once per task worktree before any agent (deps, env). Marker `.orkka/setup` caches success by command hash. |
| `QaRunCommand` | How the QA lane starts this repo's app (falls back to `ProjectSettings.QaRunCommand`, then to the run config). |
| `BuildCommand` | Proves a merge still compiles (conflict-resolution path only). |
| `CheckCommand` | The quality gate — lint + typecheck + tests, run by the platform after every Developing run. |

Never a place for secrets: the command runs on the user's runner, where it can copy their own
`.env`. Details: `.claude/CLAUDE.md` → Repository Management.

### Stack detection

`ProjectRepo.Stack` is one of `RepoStacks` (`node`, `python`, `go`, `rust`, `jvm`, `dotnet`,
`php`, `ruby`, `lua`, `cpp`, `mobile`, `general`) — the toolchain a repo needs.
`RepoStackClassifier.Classify` decides it from the worktree: the repo's own `orkka.json`
`stack` wins, otherwise manifest-file presence at the root and one level down, with a
precedence order (a Next.js app with a `Dockerfile` is still Node; a Kotlin service with a lint
`package.json` is still JVM). Pure file presence — no model, no network. It stays `null`
(= general) until a worktree has been on disk. Runs on the user's own runner use whatever
toolchain their machine has, so the stack is descriptive only — nothing routes on it.

The repo's own `CLAUDE.md` is read by agents in the worktree (the prompts tell them to) but
Orkka never generates one.

## Agents

Every project is seeded with the full roster — PM (Luna), Designer (Muse), FullStackDev (Nova),
Solution Architect (Archie), FrontendDev (Pixel), BackendDev (Atlas), DevOps (Forge), Code
Reviewer (Sentinel), QA (Scout), plus the singleton specialists from `AgentSeeder.Specialists`
(GameDev, GameDesigner, DBA, Legal, MobileDev). Seeding is not stack- or pipeline-dependent:
`AgentSeeder.CreateDefaultPod` always seeds everyone, models come from the workspace's AI provider
(Settings → Workspace; Automatic = Claude when it can run, else the first signed-in provider —
`IWorkspaceAiProviderResolver`, AGENTS.md → Default models per provider; QA always Claude), and a
republished `ProjectCreated` only tops up missing
specialists. Built-in skills are seeded and attached to their default roles in the same
handler. One persona per role: `DefaultPrompts.ForRole(role)`.

An agent's repo scope is `ProjectAgent.RepoIds` (empty = all repos), edited on the Team page.
See AGENTS.md for personas, prompt assembly, memory and skills.

## Run config and `orkka.json`

`orkka.json` at a repo root is THE Orkka file in a repo (`OrkkaManifest`):

- `stack` — overrides stack detection (above).
- `run.targets` (`RunManifest`) — how to start the project's apps on the developer's machine.
  Read as-is with no model call (`RunConfigSource.Manifest`) and re-read whenever it changes.

Without a manifest, `IRunConfigDetector` (`RunConfigDetector`) reads each repo's file tree and
manifests, asks the utility model, and stores **run targets** in `projects.project_run_configs`
(key, label, kind, repo, command, `installCommand`, `stopCommand`, working directory, port,
`dependsOn`, `autoSelect`, `restartOnMerge`, `previewPath`). AI-sourced configs carry a SHA-256
fingerprint of the manifests they came from; a mismatch marks them stale and offers a re-detect,
never runs one. `PUT /api/projects/{id}/run-config` (`ReposManage`) lets a human correct a
command. Dev agents are told to keep `orkka.json` true as the app grows
(`RunnableContract.DevAgentSection` in every Developing prompt).

The Run button (runner capability `app-run`) starts those targets in the developer's real
working copy and streams the output back — see `.claude/CLAUDE.md` → "Run the project locally".

## Deleting a project

`DELETE /api/projects/{id}` (`ProjectsDelete`) removes the project; other modules clean up
their rows by reacting to the deletion event. Nothing outside Orkka (repos, folders, forge pull
requests) is touched.
