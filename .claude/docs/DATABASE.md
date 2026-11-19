# DATABASE.md — Orkka Database Architecture

## Overview

Orkka keeps its data in one **SQLite** file on the developer's machine, shared by every backend
module. Orkka holds no customer-application databases: the code the agents work on — and
whatever database that code uses — belongs to the developer's employer and stays on their side.

---

## The file

```
{DataDir}/orkka.db          (DataDir = Local:DataDir, default ~/.orkka/data or %ORKKA_HOME%/data)
  + orkka.db-wal, orkka.db-shm   (WAL journal)
  accessed by: Orkka.Local only (EF Core, Microsoft.EntityFrameworkCore.Sqlite; migrates at startup)
```

- The host derives the path from `Local:DataDir` (`LocalSettings.DatabasePath`) unless
  `ConnectionStrings:DefaultConnection` names another file. `./orkka start` uses the default
  (`~/.orkka/data`); there is no database server, port, password or process to supervise.
- All nine module contexts open the same file through `SqliteDatabase.AddModuleDbContext`
  (SharedKernel/Persistence). Every connection gets, from `SqlitePragmaInterceptor`:
  `journal_mode = WAL` (readers never block the writer), `busy_timeout = 10000`,
  `foreign_keys = ON`, `synchronous = NORMAL`. Microsoft.Data.Sqlite also retries a busy
  statement for its 30 s command timeout.
- Each module has its own history table, `__EFMigrationsHistory_{module}`, starting from one
  `Initial` migration (regenerated for SQLite 2026-11 — no database predates it); later schema
  changes are migrations on top of it (Trackers: `TrackerBoard`, the ticket source's board).
  Orkka.Local applies them one module after another at startup (`ApplyModuleMigrationsAsync`).
- Cross-module references are plain ULID columns — no cross-module foreign keys or navigations.
- Agents never see this file's path as a credential, and there is none — see SECURITY.md.

`SQLitePCLRaw` is pinned in SharedKernel (the version EF brings carries a known advisory); the
bundled `e_sqlite3` provides FTS5 and `RETURNING`.

---

## Modules and their tables

SQLite has no schemas: every table lives in the one file, and table names are unique across
modules. The docs write `module.table` (e.g. `organizations.notifications`) to name the owning
module; in SQL the table is just `notifications`.

| Module | Owns (examples) |
|---|---|
| `organizations` | workspaces (orgs, with the workspace's AI provider choice — `AiProvider`, null = Automatic), the local user's membership per workspace (+ permissions, preferences), API keys, in-app notifications |
| `usage` | usage records: tokens per bucket and list-price cost per run (0 for runs on the developer's subscription) |
| `sourcecontrol` | Orkka's pull requests for every repo (`local_pull_requests` + reviews/comments, `ForgeUrl` once published), git operations ledger — no forge credentials |
| `projects` | projects, project repos, run configs |
| `agents` | project agents, chat, memories (+ the FTS5 table `project_memories_fts`), skills, schedules, design assets, brand kit, diagrams, AI model catalog (seeded), audit log |
| `tasks` | tasks, plans, messages, executions, dependencies, milestones (+ plan jobs), task groups, autopilot sessions, forecasts |
| `runners` | runner tokens, presence (+ forge CLI status, `ForgeClisJson`), provider snapshots |
| `trackers` | tracker connections (metadata reported by runners — never a token), project ticket sources (`project_configs`), epic → repo mappings, ticket links |
| `documents` | project documents + revisions |

The Execution module has no tables — it only consumes other modules' contracts.

### Multi-tenancy

Every tenant entity carries `OrgId` (the workspace). `ModuleDbContext` applies a global query filter
to every `ITenantEntity`, so queries get `WHERE "OrgId" = @currentOrgId` automatically. The filter
is the first layer, not the only one: it does not apply where `ICurrentUser` is unauthenticated
(background services, event handlers) or under `IgnoreQueryFilters()`, so every user-facing
endpoint also filters by `currentUser.OrgId` explicitly (BACKEND.md → Explicit OrgId Checks).

---

## Conventions (`ModuleDbContext.ConfigureConventions`)

- **Ids.** ULID primary keys, stored through the Ulid→Guid converter as upper-case TEXT. Text
  order equals ULID order, so `OrderBy(x => x.Id)` and cursors (`Id.CompareTo(cursor) < 0`) are
  correct (pinned by `SqliteDatabaseTests`). In raw SQL pass an id as
  `id.ToGuid().ToString().ToUpperInvariant()` — a raw `Guid` parameter binds as a BLOB and
  matches nothing.
- **Instants.** Every `DateTimeOffset`/`DateTimeOffset?` is stored as its **UTC ticks**
  (INTEGER, `UtcTicksConverter`) and read back at offset zero, so ORDER BY, comparisons (lease
  claims), MIN/MAX translate to integer operations and stay correct whatever offset a value was
  written with. `DateTimeOffset.UtcNow`/`Now` inside a query is evaluated in this process as a
  parameter (`ClientClockEvaluatableExpressionFilter`). SQLite has no date arithmetic over
  ticks: compute `.Date`, spans (`CompletedAt - StartedAt`) and day buckets in memory after
  selecting the columns.
- **Decimals** are TEXT; EF's SQLite provider sums, compares and orders them exactly (its
  `ef_*` functions), so `Sum(u => u.CostTotal)` is not a double sum.
- **JSON** is TEXT (`Project.Settings`, plan/report payloads, `*Json` columns). Primitive
  collections (`List<Guid> RepoIds`, `string[] Permissions`) are JSON arrays by EF's default.
  Prefer matching in memory; when a query must look inside, match the text and say why
  (`CountAutoAnsweredMessagesAsync`).
- **Text search**: `EF.Functions.Like(col, pattern, "\\")` with `LikeSearch.ContainsPattern`.
  SQLite LIKE ignores case for ASCII letters only ("é" does not match "É").
- **No lateral joins.** A per-row `FirstOrDefault()` projecting several columns needs SQL APPLY,
  which SQLite does not have: select scalars (one subquery per column) or query the child table
  with a NOT EXISTS "no later row" filter (`BoardQueries.GetLastAgentMessagesAsync`).
- **Nullable uniqueness.** SQLite unique indexes treat NULLs as distinct; where a NULL must
  collide (an org-wide `MemberPermission`), add a second partial unique index
  `WHERE "Col" IS NULL`.
- Every entity: `OrgId`, `CreatedAt`, `UpdatedAt`. String-backed enums
  (`HasConversion<string>()`) — never rename a stored enum value. Partial indexes use
  `HasFilter` with quoted identifiers and `TRUE`/`FALSE`.
- Raw SQL that EF cannot express (FTS5, triggers, seeds) lives in the module's `Initial`
  migration as `migrationBuilder.Sql(...)` / `InsertData`.

## Concurrency: one writer

SQLite allows one writer at a time; WAL lets readers run alongside it.

- A write transaction (`BeginTransactionAsync` → `BEGIN IMMEDIATE` in Microsoft.Data.Sqlite)
  takes the write lock up front. **Never write through another context — another connection —
  while a transaction is open**: that write waits for the lock the open transaction holds, until
  the busy timeout fails it. This is why task numbers are reserved (`ITaskNumberService` →
  `ProjectsApi.ReserveTaskNumbersAsync`, one `UPDATE "projects" … RETURNING`) **before** a Tasks
  transaction opens; a failed insert leaves a gap in the numbering, which is accepted. The audit
  log (`IAgentAuditLog`) writes through its own context too — call it after commit.
- A single `SaveChanges` is already atomic; reach for an explicit transaction only to group
  ExecuteUpdate/ExecuteDelete statements or several saves.
- Orkka is one process, so per-key serialization is in-process: `KeyedLock<TKey>`
  (SharedKernel/Utilities) per org around the audit chain head (`AgentAuditLog`) and per user
  around first provisioning (`OrgMembershipService`). There is no leader election — the one
  host runs every background sweep; row leases still make claims single-winner.
- `SqliteDatabase.IsConstraintViolation` recognises SQLITE_CONSTRAINT (19) for idempotent
  inserts (`AiModelSeeder`).

## Full-text search (project memory)

`project_memories_fts` is an FTS5 table (`porter unicode61 remove_diacritics 2`) holding its own
copy of each memory's `Title` and `Content`, keyed by the memory's `Id` (UNINDEXED) — not an
external-content table over rowids, which VACUUM may renumber on a table without an INTEGER
PRIMARY KEY. AFTER INSERT / UPDATE OF Title, Content / DELETE triggers on `project_memories`
keep it in step; both are created in the Agents `Initial` migration. `MemorySearch.RankedAsync`
runs `MATCH` + `bm25()` (narrowed to the project in SQL), then applies the caller's LINQ scope
and orders by rank, ties to the newest. `MemoryRules.BuildSearchQuery` turns free text into
`"word" OR "word" …` — each word an FTS5 string, so no user text is ever read as FTS5 syntax.

---

## Local development

No database to install: `dotnet run --project src/Orkka.Local` creates
`~/.orkka/data/orkka.db` (or `$Local__DataDir/orkka.db`) and migrates it. Point it at a scratch
folder with `Local__DataDir` (and `ORKKA_HOME`) to start from an empty database; delete the
folder to reset. Inspect it with any SQLite client (`sqlite3 orkka.db`) while the host is
stopped, or read-only while it runs.

The integration tests (`OrkkaApiFactory`) give each factory its own temp `Local:DataDir`, so
each gets a fresh `orkka.db` (deleted on dispose) — no Docker, no containers.

Adding a migration:
`dotnet ef migrations add <Name> --project src/Modules/Orkka.Modules.<Module> --context <Module>DbContext --output-dir Infrastructure/Migrations`
(built normally — never `--no-build`, which scaffolds an empty migration). The design-time
factory (`Infrastructure/DesignTimeDbContextFactory.cs`) uses the same `UseOrkkaSqlite` setup
as the host and never opens the database.
