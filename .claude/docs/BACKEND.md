# BACKEND.md — Orkka.Local (API + engine)

## Architecture: Modular Monolith (vertical slices inside modules)

The backend is a modular monolith: ten self-contained modules, each (except Execution) owning
its own **tables** (in the one SQLite file), entities, EF configurations + migrations, FastEndpoints features,
infrastructure services, and a tiny public **Contracts** assembly. Modules never reference
each other's internals — the compiler enforces it (see `ModuleBoundaryTests`): a module may
only reference `*.Contracts` assemblies and `Orkka.SharedKernel`. Any module can later be
lifted into its own microservice by re-implementing its Contracts over the wire.

```
src/
├── Orkka.Local/                      # the host (a library; src/Orkka runs it, 127.0.0.1): API + engine + hubs + static UI
│   ├── Auth/                         # LocalAuthHandler, LocalRequestGuard, sessions, launch/bootstrap endpoints
│   ├── Infrastructure/               # correlation id, exception handler, health, LocalUserDirectory, app-run events
│   ├── Storage/                      # FileSystemBlobStorageService + HMAC-signed /blobs endpoints
│   ├── LocalComposition.cs           # AddSharedKernel + AddXxxModule per module + host services
│   ├── StaticUi.cs                   # the built web UI: public assets, locale redirect, session-gated index.html, CSP
│   └── OrkkaHost.cs                  # BuildAsync: Kestrel on 127.0.0.1, migrations, pipeline, hubs, relays (a library; src/Orkka runs it)
├── Orkka.SharedKernel/               # ModuleDbContext, in-process IEventBus + event catalog,
│                                     #   ULID converters, ICurrentUser, IUserDirectory, encryption, cache, AI, blob
├── Orkka.Git/                        # the one git engine (GitProcess, credentials, ledger) + GitUrlRedactor, RepoRelativePath
├── Orkka.AgentRuntime/               # agent invokers, Bash guard, git workspace manager
├── Orkka.LocalRunner(.Contracts)/    # the runner (library: RunnerHost, sessions, guards, trackers) + its CLI commands
├── Orkka/                            # the entry point, assembly `orkka`: start (host + runner in one process), open/status/stop
├── Orkka.Runners.Server/             # host side of the runner link (proxies, relays, app-run)
└── Modules/
    ├── Orkka.Modules.Organizations(/.Contracts)   # organizations — workspaces, the local user's membership, API keys, notifications
    ├── Orkka.Modules.Usage(/.Contracts)           # usage — token usage records and their reads
    ├── Orkka.Modules.SourceControl(/.Contracts)   # sourcecontrol — Orkka's own pull requests, git-operation ledger, repo reads
    ├── Orkka.Modules.Projects(/.Contracts)        # projects — projects, repos, run configs
    ├── Orkka.Modules.Runners(/.Contracts)         # runners — runner tokens/presence, local folders, execution targets
    ├── Orkka.Modules.Documents(/.Contracts)       # documents — project documents
    ├── Orkka.Modules.Agents(/.Contracts)          # agents — agent team, chat, memories, audit log
    ├── Orkka.Modules.Tasks(/.Contracts)           # tasks — kanban tasks, plans, executions, TaskHub
    ├── Orkka.Modules.Trackers(/.Contracts)        # trackers — Jira/ADO connections, ticket sources, ticket links
    └── Orkka.Modules.Execution/                   # no tables — agent runtime; consumes other contracts only
```

Module internal layout (every module follows it):

```
Orkka.Modules.Tasks/
├── TasksModuleExtensions.cs   # AddTasksPersistence / AddTasksModule / AddTasksModuleApi / MapTasksModule
├── Domain/                    # entities + module-local types (vertical slices still apply)
├── Features/                  # FastEndpoints slices + SignalR hubs owned by the module
├── Infrastructure/            # TasksDbContext (module "tasks"), configurations, migrations, services
└── PublicApi/                 # implementations of the module's Contracts interfaces
```

Cross-module rules:
- **Sync calls** go through Contracts interfaces (`IProjectsApi`, `ITasksApi`, `IAgentsApi`,
  `IPullRequestService`, `IUsageRecorder`, `IOrgMembershipService`) — return values / transactional writes.
- **Reactions** go through the in-process `IEventBus` (SharedKernel `Events/Catalog`):
  `ProjectCreated` → Agents seeds the team, `UsageRecorded` → Agents tracks cost,
  `ProjectDeleted` → each module cleans its own data. The bus is in-process, and
  Orkka.Local is a single process, so every handler sees every event; DB-flag hand-offs
  polled by the queue still work as before.
- **Data**: one SQLite file (`{DataDir}/orkka.db`), each module's own tables in it, a
  per-module `__EFMigrationsHistory_{module}`. Cross-module references are plain ULID columns —
  no cross-module FKs or navigations. Orkka.Local applies all module migrations at startup.
  SQLite has one writer: never write through another module's API (another connection) inside
  an open transaction — see DATABASE.md → Concurrency.

---

## .NET 10 & C# Latest Features — Enforced

```csharp
// ✅ Primary constructors on all classes
public sealed class CreateTaskHandler(AppDbContext db, ICurrentUser user, IHubContext<TaskHub> hub);

// ✅ Collection expressions
List<string> defaultLanes = ["Drafting", "Ready", "Planning", "Plan Ready",
    "Pending Questions", "Developing", "Code Review", "Done"];

// ✅ sealed by default — unseal only when inheritance is needed
// ✅ required members on DTOs
public sealed class CreateTaskRequest
{
    public required string Title { get; init; }
    public required Guid RepoId { get; init; }
    public string? Description { get; init; }
    public Priority Priority { get; init; } = Priority.Medium;
}

// ✅ Raw string literals for SQL/long text
// ✅ Pattern matching (switch expressions) everywhere
// ✅ Global usings in GlobalUsings.cs
// ✅ File-scoped namespaces
// ✅ Guid.CreateVersion7() for ordered UUIDs
```

---

## FastEndpoints Pattern

Every endpoint is a single file. No controllers. No MediatR.

```csharp
public sealed class CreateTaskEndpoint : Endpoint<CreateTaskRequest, TaskResponse>
{
    public override void Configure()
    {
        Post("/api/projects/{ProjectId}/tasks");
    }

    public override async Task HandleAsync(CreateTaskRequest req, CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(Resolve<ICurrentUser>(), this, Permission.TasksCreate))
            return;
        var db = Resolve<AppDbContext>();
        var user = Resolve<ICurrentUser>();
        var hub = Resolve<IHubContext<TaskHub>>();

        var task = new TaskEntity
        {
            Id = Guid.CreateVersion7(),
            OrgId = user.OrgId,
            ProjectId = req.ProjectId,
            RepoId = req.RepoId,
            Title = req.Title,
            CurrentLane = Lane.Drafting,
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.Tasks.Add(task);
        await db.SaveChangesAsync(ct);

        await hub.Clients.Group($"project:{task.ProjectId}")
            .SendAsync("TaskCreated", task.ToResponse(), ct);

        await SendCreatedAtAsync($"/api/tasks/{task.Id}", task.ToResponse(), ct);
    }
}
```

---

## EF Core — Performance First

### Global Tenant Isolation

```csharp
public sealed class AppDbContext(DbContextOptions<AppDbContext> options, ICurrentUser currentUser)
    : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        // Every ITenantEntity gets .HasQueryFilter(e => e.OrgId == currentUser.OrgId)
    }
}
```

### Explicit OrgId Checks — Defense in Depth (MANDATORY)

The global query filter is the first layer, not the only one. It is bypassed whenever
`ICurrentUser` is unauthenticated (background services, event handlers), by `IgnoreQueryFilters()`, or by a future regression. Every user-facing
endpoint MUST therefore also scope its queries and creations explicitly:

```csharp
// ✅ Queries: inject ICurrentUser and filter every tenant-entity query explicitly
public sealed class GetTaskEndpoint(OrkkaDbContext db, ICurrentUser currentUser) ...

var entity = await db.Tasks
    .FirstOrDefaultAsync(t => t.Id == id && t.OrgId == currentUser.OrgId, ct);

// ✅ Route-parent checks too (projectId/taskId from the route is untrusted)
var exists = await db.Projects.AnyAsync(p => p.Id == projectId && p.OrgId == currentUser.OrgId, ct);

// ✅ Creations: set OrgId explicitly — never rely on the SaveChanges interceptor
var entity = new TaskEntity { OrgId = currentUser.OrgId, ProjectId = projectId, ... };

```

The `OrgId` always comes from the server-side identity (`ICurrentUser` from the local
session or an org API key) — never from the URL, body,
or headers. Exemptions: `Organization` lookups by `Id == currentUser.OrgId` (not a
tenant entity), background work with no user context, and lookups scoped through
an already-org-validated parent entity (e.g. `pr.OrgId == repo.OrgId`).

### Performance Rules

```csharp
// ✅ ALWAYS async — no sync DB calls. No .Result or .Wait(). Ever.
// ✅ AsNoTracking for reads
await db.Tasks.AsNoTracking().Where(t => t.ProjectId == id).Select(t => t.ToDto()).ToListAsync(ct);

// ✅ Project only needed columns — never load full entities for read endpoints
// ✅ ExecuteUpdateAsync/ExecuteDeleteAsync for bulk ops
await db.Notifications.Where(n => n.UserId == uid && !n.IsRead)
    .ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true), ct);

// ✅ Compiled queries for hot paths
private static readonly Func<AppDbContext, Guid, Lane, IAsyncEnumerable<TaskEntity>> _tasksByLane =
    EF.CompileAsyncQuery((AppDbContext db, Guid projectId, Lane lane) =>
        db.Tasks.AsNoTracking().Where(t => t.ProjectId == projectId && t.CurrentLane == lane));

// ✅ Keyset pagination (not OFFSET)
.Where(t => t.CreatedAt < cursor).OrderByDescending(t => t.CreatedAt).Take(20)

// ✅ JSON columns: a string property (TEXT); match inside in memory, not in SQL
modelBuilder.Entity<Project>().Property(p => p.Settings);
```

---

## Async/Await & Parallelization

```csharp
// ✅ Parallelize independent queries
var (project, repos, members) = await (
    db.Projects.FindAsync([projectId], ct).AsTask(),
    db.ProjectRepos.Where(r => r.ProjectId == projectId).ToListAsync(ct),
    db.OrgMembers.Where(m => m.OrgId == orgId).ToListAsync(ct)
).WhenAll();

// ✅ Parallel.ForEachAsync for batch work
await Parallel.ForEachAsync(repoIds, ct, async (repoId, token) =>
{
    await gitManager.FetchLatestAsync(orgId, repoId, token);
});

// ✅ Channels for producer/consumer in worker
var channel = Channel.CreateBounded<TaskEntity>(100);
```

---

## SignalR — Live Streaming

### Events Streamed to Frontend

| Event | SignalR Group | Trigger |
|-------|-------------|---------|
| `TaskCreated` | `project:{id}` | New task |
| `TaskLaneChanged` | `project:{id}` | Any lane transition |
| `AgentThinking` | `task:{id}` | Streaming agent reasoning tokens |
| `AgentMessage` | `task:{id}` | Agent message/question/progress |
| `PlanReady` | `task:{id}` | Plan completed |
| `CodeProgress` | `task:{id}` | Files changed during coding |
| `TestResults` | `task:{id}` | Tests completed |
| `PrCreated` | `task:{id}` | PR created |
| `TaskCompleted` | `project:{id}` | Task done |
| `TaskFailed` | `project:{id}` | Task errored |

### Token-by-Token Agent Streaming

```csharp
public async IAsyncEnumerable<string> StreamAgentResponseAsync(
    Guid taskId, string prompt, [EnumeratorCancellation] CancellationToken ct)
{
    await using var stream = anthropicClient.StreamMessageAsync(prompt, ct);
    await foreach (var chunk in stream.WithCancellation(ct))
    {
        await hubContext.Clients.Group($"task:{taskId}")
            .SendAsync("AgentThinking", new { chunk = chunk.Text, ts = DateTimeOffset.UtcNow }, ct);
        yield return chunk.Text;
    }
}
```

---

## Auth — the local user

All endpoints are protected. `LocalAuthHandler` makes a request the single `local_user` (Owner of every workspace) from the `orkka_s` session cookie or the install-secret bearer (`Local:InstallSecret`, handed over in memory by `./orkka start`); `orkka_key_…` bearers go to `OrgApiKeyAuthHandler`. `CurrentUserMiddleware` resolves the workspace (`X-Orkka-Workspace`) and the membership. SignalR hubs resolve the caller in `HubSecurity`. Background services run with no user context and filter by explicit OrgId. See CLAUDE.md → Hosts.

---

## Engine — Task Lifecycle

### On New Task Entering Ready Lane

```
1. TaskQueueProcessor detects task in Ready lane
2. Concurrency check (project `MaxParallelTasks`, runner `ORKKA_MAX_CONCURRENT_RUNS`)
3. If allowed:
   a. GitManager.FetchLatestAsync(orgId, repoId)
   b. WorktreeManager.CreateAsync(orgId, repoId, taskId)
   c. Load context files (CLAUDE.md, README) from worktree
   d. AgentOrchestrator starts plan or execute phase
4. If at capacity: task stays in Ready, queue_position updated
```

### Agent Orchestration

```
Planning → stream thinking → save plan → Plan Ready → WAIT approval
Execution → stream thinking + code → if question → Pending Questions → WAIT reply
         → run tests → create PR → Code Review → WAIT review → Done
```

Agent uses structured tags: `<question>`, `<plan>`, `<progress>`. The orchestrator parses them to determine lane transitions.

---

## Error Handling — Result Pattern

```csharp
public sealed record Result<T>
{
    public T? Value { get; init; }
    public string? Error { get; init; }
    public bool IsSuccess => Error is null;
    public static Result<T> Ok(T value) => new() { Value = value };
    public static Result<T> Fail(string error) => new() { Error = error };
}
// No exceptions for business logic. Ever.
```

---

## Key Dependencies

```xml
FastEndpoints
Microsoft.AspNetCore.SignalR
Microsoft.EntityFrameworkCore + Microsoft.EntityFrameworkCore.Sqlite
Serilog.AspNetCore
ModelContextProtocol.AspNetCore
ih0.Claude.Agent.SDK (vendored, src/ThirdParty)
```
