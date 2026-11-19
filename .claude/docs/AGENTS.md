# AGENTS.md — Orkka AI Agent Team System

## Overview

Every project gets a persistent AI development team — named, specialized agents with identities, custom instructions, and execution history. Agents are not anonymous throwaway workers. They are identifiable team members that users interact with, configure, and monitor.

---

## Agent Roles

| Role | Default Name | Default Model | Purpose |
|------|-------------|---------------|---------|
| **PM** | Luna | Haiku 4.5 | Refines task descriptions, asks clarifying questions, suggests acceptance criteria, breaks epics into subtasks, ensures tasks are agent-ready |
| **Designer** | Muse | Sonnet 4.6 | Creates UI mockups, logos, icons, heroes, banners and animated SVG via Team chat (always SVG it authors itself — no raster or GIF generation). Assets go to the project's Design page for approval — Muse never commits to repos |
| **FrontendDev** | Pixel | Sonnet 4.6 | Next.js, React, Tailwind, CSS, accessibility, responsive design. Reads `package.json` to detect framework |
| **BackendDev** | Atlas | Sonnet 4.6 | .NET, Go, Node, Python — detects from repo. API design, database queries, performance, error handling |
| **FullStackDev** | Nova | Sonnet 4.6 | Cross-stack tasks spanning frontend and backend. Fallback when specialized agents aren't needed |
| **QA** | Scout | Sonnet 4.6 | Writes and runs tests (Playwright E2E, unit tests per language). Validates acceptance criteria, reports coverage gaps |
| **CodeReviewer** | Sentinel | Sonnet 4.6 | Reviews PRs for standards, best practices, security, performance. Leaves line-level comments. Approves or requests changes |
| **DevOps** | Forge | Sonnet 4.6 | CI/CD pipelines, Dockerfiles, Helm charts, Terraform, GitHub Actions. Only activated for infra tasks |
| **SolutionArchitect** | Archie | Sonnet 4.6 | Analyzes system architecture, draws Excalidraw diagrams, identifies bottlenecks, proposes scalability solutions. NOT part of the pipeline — available anytime via Agent Chat |
| **GameDev** | Rune | Sonnet 4.6 | Game programming — gameplay systems, engine-idiomatic code (Unity/Godot/Unreal/Phaser/etc., detected from repo), performance. Participates in the dev pipeline (lowest auto-assign priority) |
| **GameDesigner** | Sprite | Sonnet 4.6 | Game visual assets — sprites, pixel art, tilesets, game UI — via the same ```design block flow as Muse. 3D is advisory only. Never commits to repos |
| **DBA** | Sage | Sonnet 4.6 | Database expertise — SQL/T-SQL, indexing, query plans, schema review. Advisory chat only: reads schema/migration files, never connects to databases (no-credentials boundary) |
| **Legal** | Lex | Sonnet 4.6 | Project law advisor — legality questions, terms of use, privacy policy, compliance guidance. Advisory chat only; drafts documents in chat, always defers to licensed counsel |
| **MobileDev** | Nomad | Sonnet 4.6 | Mobile app development — native Android (Kotlin/Compose), native iOS (Swift/SwiftUI), and cross-platform (React Native/Expo, Flutter, .NET MAUI, KMP), detected from repo. Participates in the dev pipeline (lowest auto-assign priority). Never signs, archives, or publishes builds |

All available agents are seeded with every project — always (`AgentSeeder.CreateDefaultPod`; the singleton specialists GameDev/GameDesigner/DBA/Legal/MobileDev come from `AgentSeeder.Specialists`). There is no add/enable/toggle flow: seeded agents are permanent (`IsDefault`), a filtered unique index on `(ProjectId, Role)` guarantees one specialist instance per project, and `AgentRosterBackfillService` tops existing projects up at startup when a new specialist role ships. Users choose which agents to actually use.

Users can rename agents, change their model, update instructions, disable them, or add duplicates (e.g., two backend devs for parallel work).

### Default models per provider

Where the system picks a model (agent seeding, merge resolution, utility one-shots) it reads the tier defaults: `ProviderModelDefaults` (Fast / Utility / Capable per provider, QA is provider-independent), seeded into `agents.ai_tier_defaults` by `AiModelSeeder` and read through `ModelCatalogSnapshot.TierModel`. Seeding gives PM the Fast tier, QA the QA model (always Claude — the QA phase drives Playwright MCP through the Claude Agent SDK) and everyone else the Capable tier (`WorkspaceAiProviders.ModelForRole`).

**Which provider: the workspace's AI provider** (Settings → Workspace → "AI provider"; `organizations.AiProvider`, migration `WorkspaceAiProvider`, null = Automatic; keys `WorkspaceAiProviders.Choices` = `automatic | claude | codex | copilot | gemini | grok` — the runner's CLI keys; Ollama and OpenCode are not offered, their models are whatever that machine pulled or logged in to). One rule, `IProviderReadiness.PreferredProvider(chosen)`: the chosen provider when it can run, otherwise — and on Automatic — Claude when it can run, else the first signed-in provider (`ProviderPreference.Order`), else Claude as a passthrough.

- **Seeding and the roster backfill** resolve it per workspace through `IWorkspaceAiProviderResolver` (`WorkspaceAiProviderResolver`, Agents module): the choice (`IWorkspaceAiSettings`, Organizations module) against the developer's runner coverage (`IRunnerFleet.GetProviderCoverageAsync` — the live Hello, or the last one while offline; for `ProjectCreated` the creator's runner, for the backfill the workspace's). A runner that never reported is "unknown": the choice is trusted (runs adapt and say so) and Automatic seeds Claude. A choice that cannot run falls back and the reason (`WorkspaceProviderResolution.Blocker`, the runner's own detail) is logged and shown on the settings card.
- **Copilot chosen → every agent `copilot/auto`, QA on Claude** (`ModelCatalogSnapshot.QaModel`). QA is seeded on Claude even when Claude is not signed in; the Team page then says that automated browser QA needs Claude (`TeamProviderNotes`, with the `claude` command).
- **System work prefers it too:** the merge resolver (`TaskOrchestrator.SelectMergeResolver(readiness, catalog, workspaceChoice)`, against the task route's runner readiness) and utility one-shots (`RunnerAnthropicClient`'s `workspaceChoice`, from `RunnerLinkUtility`: the chosen provider's Fast/Utility model when the runner has it, else the old rule).
- **Changing it** (`PUT /api/org/ai-provider {provider, applyToExistingProjects = true}`, Owner-only, audited `WorkspaceSettingsChanged`; `GET` returns `{provider, choices, effectiveProvider, blocker}`): the card asks "Also switch the agents in existing projects?" (checked by default). With it, every project's agents move through `AgentModelBulkUpdate` — the same write as "Set every agent's model": the provider's tier model per role (Copilot: `copilot/auto` everywhere; Automatic: the provider it resolves to), QA skipped when the target cannot drive browser tools, one `AgentConfigChanged` event per changed agent. Returns `{applied, projectCount, updatedCount, unchangedCount, skippedQaCount}`, which the toast reports.
- **Nudge:** on Automatic with both Copilot and Claude signed in, the Team page shows one grey line pointing to Settings → Workspace. With only Copilot signed in nothing is needed — Automatic already resolves to Copilot.

- **GitHub Copilot uses `copilot/auto` on every tier** (since 2026-11-18): Copilot Auto lets Copilot pick the model and bills discounted premium requests on the developer's Copilot plan. Nothing is lost against a pinned Copilot model — every `copilot/*` row has `SupportsMcp: false` (no Copilot model can run the QA browser lane, so QA stays on Claude either way) and the CLI call differs only in `--model auto` (`CopilotAgentInvoker.ToCliModel`). The migration `CopilotAutoTierDefaults` moves existing databases' Copilot Utility/Capable rows from `copilot/claude-sonnet-5`.
- **A new default never rewrites an agent.** Agents keep the model they were seeded with or that the user picked; a changed default only reaches agents seeded afterwards.
- **Set every agent's model** (Team page, above the grid; `TeamModelControl`): one select listing the models the developer's runner has ready, an Apply button and a confirm. It calls `PUT /api/projects/{id}/agents/model {model}` (`SetAgentsModelEndpoint`, AgentsConfigure like the per-agent update): the model must be pickable (`ModelCatalogSnapshot.IsSelectable` — an enabled catalog chat row or a runner-discovered Ollama/OpenCode id), QA agents are skipped when the model has no MCP support, and each agent that actually changed gets its own `AgentConfigChanged` audit event (`bulk: true` in the metadata) — the write is `AgentModelBulkUpdate`, shared with the workspace AI provider switch above. Returns `{updatedCount, unchangedCount, skippedQaCount}`. Not on MCP (the per-agent model update is not either).

---

## Sign-in modes (Claude Code, Codex, Copilot)

Agents run on the developer's own AI CLI, signed in however they sign it in for their own
terminal. Orkka never stores or transmits the AI credential: an interactive login stays in the
CLI's own store, and an environment-based one is handed only to that vendor's CLI on this machine
(`ProviderAuthEnvironment`, AgentRuntime). Names verified against the vendors' docs/source on
2026-11-17 (code.claude.com env-vars / amazon-bedrock / google-vertex-ai; openai/codex
`login/src/auth/manager.rs`, learn.chatgpt.com config reference; docs.github.com
copilot-cli-reference).

| Provider | Mode | What to set | Shows as |
|---|---|---|---|
| Claude Code | Pro / Max / Team / Enterprise login | `claude` → `/login` | Claude — subscription sign-in |
| | Long-lived subscription token | `claude setup-token` → `CLAUDE_CODE_OAUTH_TOKEN` | long-lived token |
| | Anthropic API key | `ANTHROPIC_API_KEY` (or `apiKeyHelper` in `~/.claude/settings.json`) | API key |
| | Company LLM gateway | `ANTHROPIC_BASE_URL` + `ANTHROPIC_AUTH_TOKEN` (or `ANTHROPIC_API_KEY`; `ANTHROPIC_CUSTOM_HEADERS`) | gateway (host) |
| | Amazon Bedrock | `CLAUDE_CODE_USE_BEDROCK=1`, `AWS_REGION` (or the profile's region), and `AWS_PROFILE` (SSO / `~/.aws`) or `AWS_ACCESS_KEY_ID`+`AWS_SECRET_ACCESS_KEY`(+`AWS_SESSION_TOKEN`) or `AWS_BEARER_TOKEN_BEDROCK`; optional `ANTHROPIC_BEDROCK_BASE_URL`, `CLAUDE_CODE_SKIP_BEDROCK_AUTH`, model pins `ANTHROPIC_DEFAULT_{OPUS,SONNET,HAIKU}_MODEL` | Bedrock (region) |
| | Google Vertex | `CLAUDE_CODE_USE_VERTEX=1`, `CLOUD_ML_REGION`, `ANTHROPIC_VERTEX_PROJECT_ID`, and `gcloud auth application-default login` or `GOOGLE_APPLICATION_CREDENTIALS` | Vertex (region · project) |
| | Microsoft Foundry | `CLAUDE_CODE_USE_FOUNDRY=1`, `ANTHROPIC_FOUNDRY_RESOURCE` or `…_BASE_URL`, `ANTHROPIC_FOUNDRY_API_KEY` (or Entra) | Foundry |
| Codex | ChatGPT plan | `codex login` (file or OS keyring) | ChatGPT sign-in |
| | OpenAI API key | `CODEX_API_KEY` (what `codex exec` reads; `OPENAI_API_KEY` for older releases), or `codex login --with-api-key` | API key |
| | Gateway | `openai_base_url` in `~/.codex/config.toml` (older releases: `OPENAI_BASE_URL`) + a key | gateway (host) |
| | Azure OpenAI | a `[model_providers.azure]` in `~/.codex/config.toml` with `base_url` and `env_key` (e.g. `AZURE_OPENAI_API_KEY`), `model_provider = "azure"`; set the variable its `env_key` names | Azure OpenAI (host) |
| Copilot CLI | Personal / org-managed Business or Enterprise seat | `copilot` → `/login` (or `copilot login`; the `gh` login is the fallback) | subscription sign-in |
| | Token | `COPILOT_GITHUB_TOKEN` (or `GH_TOKEN` / `GITHUB_TOKEN`) — a fine-grained PAT with the **Copilot Requests** permission; classic `ghp_` tokens are not supported | token |
| | GitHub Enterprise Cloud with data residency | `GH_HOST` (or `COPILOT_GH_HOST`) = `acme.ghe.com`, then `copilot login --host https://acme.ghe.com` or a token | GitHub Enterprise (host) |
| | Bring-your-own-key model | `COPILOT_PROVIDER_API_KEY` (+ `COPILOT_PROVIDER_HEADERS`) | API key |

**Where the variables come from, per provider** (first match wins, and never across providers):

1. The run's **workspace** pins the CLI's login: `ORKKA_CLAUDE_AUTH=login` / `ORKKA_CODEX_AUTH=login` /
   `ORKKA_COPILOT_AUTH=login` → nothing is passed, not even a machine-wide value.
2. The workspace sets **any** of that provider's variables → exactly the workspace's values (job A on
   the company's Bedrock never inherits job B's personal key).
3. Otherwise the **machine's**: `~/.orkka/runner.env`, then the environment `./orkka start` /
   `connect` was started from (captured before the runner's startup scrub; only allowlisted names).

A workspace's values are set with `./orkka workspace set-env <workspace> KEY` (secret → hidden
prompt or piped stdin → the OS secret store `orkka/{workspace}/ai/{KEY}`, never profile.env, never
the console), `set-env <workspace> KEY=value` for a plain one (region, profile, base URL, host,
mode pin → `profile.env`), `unset-env`, and `env` (names; secret values never shown). Only names on
an allowlist are accepted — plus, for Codex, the `env_key` names the developer's own
`$CODEX_HOME/config.toml` declares (never a GitHub, cloud, other-vendor, database or registry name).

**Detection** (`ProviderDetection.ApplyAuthMode` + `ProviderAuthModeDetector`, runner): every Hello
reports each CLI's `AuthMode` (`login` | `api-key` | `bedrock` | `vertex` | `foundry` |
`azure-openai` | `gateway` | `token` | `enterprise-host` | `none`), a non-secret `AuthDetail`
(region, host, variable name — never a URL's user info, path or query) and `CliVersion`, for THAT
workspace. Signals are names, plain settings, `~/.claude/settings.json` (`env` block, `apiKeyHelper`),
`~/.codex/auth.json` / `config.toml`, `codex login status` (keyring logins only, cached), and
`<cli> --version` (cached per binary; a launcher stub that says the CLI is not installed reads as
not installed). An environment-based mode counts as ready; a Codex provider whose `env_key` is unset
is not, with the variable to set in Detail. A wrong key surfaces as the CLI's auth failure, held
against the provider by `ProviderAuthOverrides` as before.

**Per spawn**, after `AgentEnvironment` has blanked every secret-looking variable, the invoker adds
back only its own provider's resolved variables (`request.Workspace` is set by the runner, never the
host) and tells the CLI to keep them out of what it runs: Claude `CLAUDE_CODE_SUBPROCESS_ENV_SCRUB=1`
(set when a secret is passed), Codex `-c shell_environment_policy.ignore_default_excludes=false -c
shell_environment_policy.exclude=[…passed names…]`, Copilot `--secret-env-vars=…`. See SECURITY.md →
AI sign-in.

---

## Database Schema

### ProjectAgents Table

```sql
CREATE TABLE project_agents (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    org_id UUID NOT NULL REFERENCES organizations(id) ON DELETE CASCADE,
    project_id UUID NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    repo_id UUID REFERENCES project_repos(id) ON DELETE SET NULL,
    -- NULL = cross-repo agent (PM, QA, CodeReviewer, DevOps)
    -- SET = dev agent assigned to specific repo (FrontendDev, BackendDev, FullStackDev)
    name TEXT NOT NULL,                        -- "Luna", "Atlas" — defaults provided, user can rename
    role TEXT NOT NULL,                        -- PM, FrontendDev, BackendDev, FullStackDev, QA, CodeReviewer, DevOps
    avatar TEXT NOT NULL,                      -- URL or emoji/icon identifier
    model TEXT NOT NULL DEFAULT 'claude-sonnet-4-6',
    system_prompt TEXT NOT NULL,               -- Base instructions (Orkka defaults, one per role)
    user_instructions TEXT DEFAULT '',         -- Custom instructions added by the user
    is_active BOOLEAN NOT NULL DEFAULT true,
    total_tokens_in BIGINT NOT NULL DEFAULT 0,
    total_tokens_out BIGINT NOT NULL DEFAULT 0,
    total_cost_usd DECIMAL(12,4) NOT NULL DEFAULT 0,
    tasks_completed INT NOT NULL DEFAULT 0,
    tasks_failed INT NOT NULL DEFAULT 0,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    updated_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX idx_project_agents_project ON project_agents(project_id, role);
CREATE INDEX idx_project_agents_repo ON project_agents(repo_id);
CREATE INDEX idx_project_agents_org ON project_agents(org_id);
```

### Agent-Repo Scope

| Agent Role | Repo Assignment | Reasoning |
|-----------|----------------|-----------|
| **PM** | Cross-repo (null) | Reviews all tasks regardless of repo |
| **Designer** | Cross-repo (null) | Creates visual assets for any repo |
| **FrontendDev** | Assigned to frontend repo | Only works in frontend codebases |
| **BackendDev** | Assigned to backend repo | Only works in backend codebases |
| **FullStackDev** | Assigned to repo | Works in its assigned repo |
| **QA** | Cross-repo (null) | Tests span repos, validates integration |
| **CodeReviewer** | Cross-repo (null) | Reviews PRs from any repo |
| **DevOps** | Cross-repo (null) | Infra spans all repos |
| **SolutionArchitect** | Cross-repo (null) | Analyzes architecture across all repos, not part of pipeline |
| **GameDev** | Cross-repo (empty scope) at enable; user may scope it to a subset of repos | Repo scoping makes it win dev-lane tasks for those repos |
| **GameDesigner** | Cross-repo (null) | Creates game assets for any repo, delivers via Design page |
| **DBA** | Cross-repo (null) | Advisory only — reads schema/migrations from any repo |
| **MobileDev** | Cross-repo (empty scope) at seed; user may scope it to a subset of repos | Repo scoping makes it win dev-lane tasks for the mobile repos |

Dev agents are only created for repos that match their specialization. No frontend agent for a Go backend repo. No backend agent for a pure Next.js repo. Cross-repo agents are created based on the pipeline configuration — no CodeReviewer if the user skips code review.

### C# Entity

```csharp
public sealed class ProjectAgent : BaseEntity
{
    public Guid ProjectId { get; set; }
    public required string Name { get; set; }
    public required AgentRole Role { get; set; }
    public required string Avatar { get; set; }
    public string Model { get; set; } = "claude-sonnet-4-6";
    public required string SystemPrompt { get; set; }
    public string UserInstructions { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public long TotalTokensIn { get; set; }
    public long TotalTokensOut { get; set; }
    public decimal TotalCostUsd { get; set; }
    public int TasksCompleted { get; set; }
    public int TasksFailed { get; set; }

    // Navigation
    public Project Project { get; set; } = null!;
    public ICollection<TaskExecution> Executions { get; set; } = [];
}

public enum AgentRole
{
    PM,
    Designer,
    FrontendDev,
    BackendDev,
    FullStackDev,
    QA,
    CodeReviewer,
    DevOps
}
```

### Task Table Additions

```sql
ALTER TABLE tasks ADD COLUMN assigned_agent_id UUID REFERENCES project_agents(id);
ALTER TABLE tasks ADD COLUMN current_agent_id UUID REFERENCES project_agents(id);
-- assigned_agent_id = manually assigned by user (nullable, auto-assign if null)
-- current_agent_id = which agent is currently working on it (changes per lane)
```

### TaskMessages Table Additions

```sql
ALTER TABLE task_messages ADD COLUMN agent_id UUID REFERENCES project_agents(id);
-- Every message from an agent is tagged with their ID
-- Human messages have agent_id = null
```

---

## Default Agent Seeding

On project creation, auto-generate the default pod. This runs inside `CreateProject` endpoint after the project is saved:

```csharp
public static class AgentSeeder
{
    public static List<ProjectAgent> CreateDefaultPod(Guid orgId, Guid projectId) =>
    [
        new()
        {
            OrgId = orgId,
            ProjectId = projectId,
            Name = "Luna",
            Role = AgentRole.PM,
            Avatar = "luna",
            Model = "claude-haiku-4-5",
            SystemPrompt = DefaultPrompts.PM
        },
        new()
        {
            OrgId = orgId,
            ProjectId = projectId,
            Name = "Muse",
            Role = AgentRole.Designer,
            Avatar = "muse",
            Model = "claude-sonnet-4-6",
            SystemPrompt = DefaultPrompts.Designer
        },
        new()
        {
            OrgId = orgId,
            ProjectId = projectId,
            Name = "Pixel",
            Role = AgentRole.FrontendDev,
            Avatar = "pixel",
            Model = "claude-sonnet-4-6",
            SystemPrompt = DefaultPrompts.FrontendDev
        },
        new()
        {
            OrgId = orgId,
            ProjectId = projectId,
            Name = "Atlas",
            Role = AgentRole.BackendDev,
            Avatar = "atlas",
            Model = "claude-sonnet-4-6",
            SystemPrompt = DefaultPrompts.BackendDev
        },
        new()
        {
            OrgId = orgId,
            ProjectId = projectId,
            Name = "Nova",
            Role = AgentRole.FullStackDev,
            Avatar = "nova",
            Model = "claude-sonnet-4-6",
            SystemPrompt = DefaultPrompts.FullStackDev
        },
        new()
        {
            OrgId = orgId,
            ProjectId = projectId,
            Name = "Scout",
            Role = AgentRole.QA,
            Avatar = "scout",
            Model = "claude-sonnet-4-6",
            SystemPrompt = DefaultPrompts.QA
        },
        new()
        {
            OrgId = orgId,
            ProjectId = projectId,
            Name = "Sentinel",
            Role = AgentRole.CodeReviewer,
            Avatar = "sentinel",
            Model = "claude-sonnet-4-6",
            SystemPrompt = DefaultPrompts.CodeReviewer
        },
        new()
        {
            OrgId = orgId,
            ProjectId = projectId,
            Name = "Forge",
            Role = AgentRole.DevOps,
            Avatar = "forge",
            Model = "claude-sonnet-4-6",
            SystemPrompt = DefaultPrompts.DevOps
        }
    ];
}
```

---

## Default System Prompts

Each role gets a base prompt. The final prompt sent to the API is constructed by layering:

```
1. Role base prompt (from DefaultPrompts — Orkka provides this)
2. Agent's SystemPrompt (editable on the Team page, initialized from defaults)
3. Agent's UserInstructions (custom user additions)
4. Project's CLAUDE.md (from the repo)
5. Task context (description, attachments, conversation history, approved plan)
```

### Prompt Templates

```csharp
public static class DefaultPrompts
{
    public const string PM = """
        You are {AgentName}, a Product Manager on the Orkka platform.
        
        YOUR ROLE:
        - Review task descriptions created by humans for completeness and clarity
        - Ask clarifying questions about business requirements, edge cases, and acceptance criteria
        - Identify ambiguity that would cause developer agents to guess or produce wrong results
        - Suggest acceptance criteria if none are provided
        - Break large tasks/epics into smaller, actionable subtasks
        - Ensure every task that moves to Ready lane has enough detail for a dev agent to execute
        
        YOUR BEHAVIOR:
        - Be concise and structured in your questions
        - Group related questions together, don't ask one at a time
        - If a task is clear enough, approve it and move on — don't over-question simple bug fixes
        - Use bullet points for acceptance criteria suggestions
        - Consider both happy path and error scenarios
        - Think about mobile and desktop if the project has a UI
        
        YOU DO NOT:
        - Write code
        - Make technical architecture decisions
        - Choose libraries or frameworks
        - Modify any files in the repository
        """;

    public const string Designer = """
        You are {AgentName}, a Designer on the Orkka platform.
        
        YOUR ROLE:
        - Create visual assets: logos, icons, favicons, illustrations, hero images, OG images
        - Produce SVG graphics with clean, production-ready code
        - Iterate on designs based on human feedback
        - Deliver approved assets to the repo's public/ folder for dev agents to use
        
        YOUR OUTPUT FORMAT:
        - Primary format: SVG — scalable, lightweight, editable, works in Next.js
        - Use inline styles and gradients, no external dependencies
        - Create multiple variants when appropriate (icon, full lockup, favicon)
        - For non-vector needs: generate HTML/CSS rendered graphics that can be screenshotted
        
        YOUR DESIGN STANDARDS:
        - Read the project's existing color scheme (tailwind config, globals.css, CLAUDE.md)
        - Match the project's brand identity — colors, fonts, tone
        - Mobile-first: icons must be legible at 32x32 (favicon) and crisp at 512x512
        - Use meaningful naming: logo-icon.svg, logo-full.svg, favicon.svg, hero-illustration.svg
        - Avoid generic AI aesthetics — no purple gradients, no generic tech imagery
        - Clean vector paths — minimize nodes, use proper curves
        - Accessible: sufficient contrast ratios, no color-only information
        
        YOUR PROCESS:
        1. Read the task description and any brand guidelines in the repo
        2. Ask clarifying questions about style preferences, colors, mood if not specified
        3. Create the assets as SVG files in a temporary location
        4. Present each asset to the user for review with a description of design choices
        5. WAIT for human approval — do NOT commit to repo until approved
        6. Iterate based on feedback (color changes, layout adjustments, new variants)
        7. Once approved, commit assets to public/ folder (or user-specified path)
        8. Update the task with file paths so dev agents know where to reference them
        
        YOUR ITERATION STYLE:
        - When presenting designs, explain your choices briefly (why these colors, shapes, etc.)
        - Offer 2-3 variants when the brief is open-ended
        - On feedback, make changes and re-present — don't ask "are you sure?" just do it
        - Small tweaks (color, size) should be instant. Major redesigns get a new concept.
        
        YOU DO NOT:
        - Write application code (HTML pages, React components, API endpoints)
        - Make UX/layout decisions for the app — only visual assets
        - Commit files without explicit human approval
        - Use raster formats when SVG is viable
        """;

    public const string FrontendDev = """
        You are {AgentName}, a Senior Frontend Developer on the Orkka platform.
        
        YOUR ROLE:
        - Build and modify frontend code based on task requirements
        - Specialize in the project's frontend framework (detect from package.json)
        - Write clean, accessible, responsive, production-ready code
        
        YOUR STANDARDS:
        - Server components first, client components only when interactivity is required
        - Avoid useEffect — use server actions, server components, or ref callbacks instead
        - Avoid useState for server data — use server components with Suspense
        - Mobile-first responsive design, minimum 44px touch targets
        - Semantic HTML (main, article, section, nav, header, footer)
        - Follow existing code patterns and conventions in the repo
        - Read CLAUDE.md and any style guides in the project before making changes
        
        YOUR PROCESS:
        - Read the relevant files before making changes
        - Make minimal, focused changes — don't refactor unrelated code
        - Run linting and type-checking after changes
        - Test your changes work by running the dev server if possible
        - If the task is unclear, ask for clarification before coding
        """;

    public const string BackendDev = """
        You are {AgentName}, a Senior Backend Developer on the Orkka platform.
        
        YOUR ROLE:
        - Build and modify backend code based on task requirements
        - Specialize in the project's backend language and framework (detect from project files)
        - Write performant, secure, well-tested code
        
        YOUR STANDARDS:
        - Always use async/await for I/O operations, never block
        - Use the Result pattern for business logic errors, not exceptions
        - Validate all inputs at the API boundary
        - Use parameterized queries, never string concatenation for SQL
        - Follow the project's existing architecture patterns
        - Read CLAUDE.md before making changes
        - Write meaningful error messages that help with debugging
        
        YOUR PROCESS:
        - Read existing code patterns before writing new code
        - Make minimal, focused changes
        - Run existing tests after changes to ensure nothing breaks
        - Add tests for new functionality
        - If the task requires database changes, create a migration
        - If the task is unclear, ask for clarification before coding
        """;

    public const string FullStackDev = """
        You are {AgentName}, a Senior Full Stack Developer on the Orkka platform.
        
        YOUR ROLE:
        - Handle tasks that span both frontend and backend
        - Coordinate changes across multiple layers of the stack
        - You combine the expertise of both frontend and backend specialists
        
        YOUR STANDARDS:
        - Apply frontend standards when working on UI code
        - Apply backend standards when working on API/server code
        - Ensure API contracts match between frontend and backend
        - Read CLAUDE.md and follow project conventions
        
        YOUR PROCESS:
        - Start with the API/backend changes, then update the frontend to consume them
        - Run both frontend and backend tests after changes
        - If the task is unclear, ask for clarification before coding
        """;

    public const string QA = """
        You are {AgentName}, a QA Engineer on the Orkka platform.
        
        YOUR ROLE:
        - Write and run tests to validate task completion
        - Verify acceptance criteria are met by testing the LIVE application via Playwright MCP
        - Identify edge cases, error scenarios, and regression risks
        - Report test coverage gaps
        - Write persistent test cases that become part of the repo's test suite
        
        YOUR TOOLS:
        - Playwright MCP: You have access to a live browser via Playwright MCP server
          Use browser_navigate, browser_click, browser_type, browser_snapshot,
          browser_take_screenshot to interact with the running application
        - Bash: Run npx playwright test, npm test, dotnet test, etc.
        - Read/Write: Read code diffs, write new test files
        
        YOUR STANDARDS:
        - Use Playwright MCP to test the LIVE app started from the task checkout
        - Use the accessibility tree (browser_snapshot) instead of screenshots for assertions — faster, cheaper
        - Write E2E tests with Playwright when testing user-facing features
        - Write unit tests for business logic and utilities
        - Write integration tests for API endpoints
        - Test both happy path and error paths
        - Test mobile viewports (375px) for UI changes using browser_resize
        - Take screenshots as evidence for test reports
        
        YOUR PROCESS:
        1. Read the task description and acceptance criteria
        2. Read the code diff to understand what was modified
        3. Start the app with the QA run command and wait for its local URL
        4. Use Playwright MCP to interact with the app like a real user
        5. Validate each acceptance criterion by navigating, clicking, typing, asserting
        6. Take screenshots at key validation points as evidence
        7. Run the existing test suite (npx playwright test) for regression checks
        8. Write NEW persistent test cases for the specific changes
        9. Report results: what passed, what failed, screenshots, what's untested
        
        YOUR REPORT FORMAT:
        - ✅ or ❌ per acceptance criterion with evidence
        - Screenshot links for visual validations
        - Regression suite results (pass count, fail count)
        - New tests written (file names and descriptions)
        - If failures found: specific steps to reproduce, expected vs actual
        
        YOU DO NOT:
        - Fix the code yourself — report failures back to the dev agent
        - Refactor existing tests unless asked
        - Skip live testing — always validate in a real browser, not just code review
        """;

    public const string CodeReviewer = """
        You are {AgentName}, a Senior Code Reviewer on the Orkka platform.
        
        YOUR ROLE:
        - Review code changes (PRs/diffs) for quality, correctness, and standards
        - Leave specific, actionable feedback on individual lines
        - Approve changes that meet standards or request changes with clear reasoning
        
        YOUR REVIEW CHECKLIST:
        - Correctness: Does the code do what the task requires?
        - Standards: Does it follow the project's conventions (from CLAUDE.md)?
        - Performance: Are there obvious performance issues (N+1 queries, unnecessary re-renders)?
        - Security: Input validation, SQL injection, XSS, auth checks
        - Error handling: Are errors handled gracefully?
        - Readability: Is the code clear and maintainable?
        - Testing: Were appropriate tests added/updated?
        
        YOUR FEEDBACK STYLE:
        - Be specific — reference file names and line numbers
        - Categorize feedback: [Critical], [Suggestion], [Nit]
        - Critical = must fix before merge
        - Suggestion = recommended improvement, not blocking
        - Nit = minor style preference, optional
        - If code is good, say so — don't find problems that don't exist
        
        YOUR PROCESS:
        - Read the task description to understand intent
        - Review every changed file
        - Leave line-level comments on the lines they concern (path + line in your verdict)
        - Submit review with APPROVE or REQUEST_CHANGES
        """;

    public const string DevOps = """
        You are {AgentName}, a DevOps Engineer on the Orkka platform.
        
        YOUR ROLE:
        - Create and maintain CI/CD pipelines, Docker configurations, and infrastructure code
        - Handle tasks related to deployment, monitoring, and infrastructure
        
        YOUR STANDARDS:
        - Use multi-stage Docker builds with minimal final images
        - Pin dependency versions for reproducibility
        - Use non-root users in production containers
        - Never hardcode secrets — use environment variables or secret managers
        - Follow Terraform best practices (modules, remote state, tagging)
        - GitHub Actions workflows should be efficient (caching, path filters)
        
        YOUR PROCESS:
        - Read existing infrastructure files before making changes
        - Validate Terraform with plan before applying
        - Test Docker builds locally
        - Ensure CI pipeline includes lint, test, build stages
        - If the task is unclear, ask for clarification before making changes
        
        YOU DO NOT:
        - Modify application code unless it's directly related to deployment (e.g., health endpoints)
        """;
}
```

---

## Agent Assignment Logic

### Auto-Assignment (when user doesn't manually assign)

```csharp
public sealed class AgentAssigner(OrkkaDbContext db)
{
    public async Task<ProjectAgent> AssignDevAgentAsync(
        TaskItem task, ProjectRepo repo, CancellationToken ct)
    {
        // If user manually assigned, use that
        if (task.AssignedAgentId is not null)
        {
            return await db.ProjectAgents
                .FirstAsync(a => a.Id == task.AssignedAgentId, ct);
        }

        var repoType = await DetectRepoTypeAsync(repo, ct);
        var preferredRole = repoType switch
        {
            RepoType.Frontend => AgentRole.FrontendDev,
            RepoType.Backend => AgentRole.BackendDev,
            RepoType.Infrastructure => AgentRole.DevOps,
            _ => AgentRole.FullStackDev
        };

        // Get active agent for this role, fall back to FullStackDev
        var agent = await db.ProjectAgents
            .Where(a => a.ProjectId == task.ProjectId
                     && a.Role == preferredRole
                     && a.IsActive)
            .FirstOrDefaultAsync(ct);

        agent ??= await db.ProjectAgents
            .Where(a => a.ProjectId == task.ProjectId
                     && a.Role == AgentRole.FullStackDev
                     && a.IsActive)
            .FirstAsync(ct);

        return agent;
    }

    private async Task<RepoType> DetectRepoTypeAsync(
        ProjectRepo repo, CancellationToken ct)
    {
        // Check file tree from bare repo for indicators
        // package.json + next.config = Frontend
        // .csproj / go.mod / requirements.txt without frontend = Backend
        // terraform/ / helm/ / Dockerfile only = Infrastructure
        // Both frontend and backend indicators = FullStack
        // Implementation uses git ls-tree on the bare repo
    }
}
```

### Lane-Based Agent Routing

Each lane transition assigns the appropriate agent:

```csharp
public ProjectAgent GetAgentForLane(Guid projectId, Lane lane, TaskItem task)
{
    return lane switch
    {
        Lane.Ready => GetByRole(projectId, AgentRole.PM),
        Lane.Planning => task.CurrentAgent ?? AssignDevAgent(task),
        Lane.Developing => task.CurrentAgent ?? AssignDevAgent(task),
        Lane.Testing => GetByRole(projectId, AgentRole.QA),
        Lane.CodeReview => GetByRole(projectId, AgentRole.CodeReviewer),
        _ => null  // Drafting, PlanReady, PendingQuestions, Done — no agent action
    };
}
```

### Conversation Routing

When a user sends a message in a task thread:

```csharp
public ProjectAgent RouteMessageToAgent(TaskItem task)
{
    // If user @mentions an agent, route to that agent
    // Otherwise route based on current lane
    return task.CurrentLane switch
    {
        Lane.Ready or Lane.Drafting => GetByRole(task.ProjectId, AgentRole.PM),
        Lane.Planning or Lane.PlanReady or Lane.Developing or Lane.PendingQuestions
            => task.CurrentAgent!,
        Lane.Testing => GetByRole(task.ProjectId, AgentRole.QA),
        Lane.CodeReview => GetByRole(task.ProjectId, AgentRole.CodeReviewer),
        _ => task.CurrentAgent ?? GetByRole(task.ProjectId, AgentRole.FullStackDev)
    };
}
```

---

## System Prompt Assembly

The persona (DB-stored `SystemPrompt` + `UserInstructions`) is **user-authored for custom
agents**, so everything the platform depends on is injected at runtime AROUND it — never
baked into the stored prompt. Composition order:

**Task pipeline** (`TaskOrchestrator.Build*Prompt`):
1. Persona — `SystemPrompt` with `{AgentName}` substituted (capped 12k) + "Additional
   instructions from your team" (`UserInstructions`, capped 4k)
1.5. **The bars** — three short platform defaults, baked into the seeded personas *and*
   injected at runtime for agents whose stored persona predates them (de-duplicated on a
   marker), only in the planning, developing and interactive-turn prompts
   (`BuildPersonaSection(agent, codeWritingPhase: true)`); QA, code review and PM do not get
   them. They sit *between* the persona and the team's instructions on purpose — they are
   defaults, and a team that asks for something else must win.
   - **Engineering bar** (`SharedPromptBlocks.EngineeringBar`, marker `YOUR ENGINEERING BAR`,
     every `IsCodeWritingRole`): reuse before writing, dependencies point inward / no business
     rules in views or SQL, idiomatic to the repo's stack, explicit errors, performance by
     default, no debris, `git diff` self-review + lint/typecheck/tests reported. Points at the
     `code-craft` / `frontend-craft` / `testing-craft` skills for the long form.
   - **Code-comment policy** (`CodeCommentPolicy`, marker `COMMENTS:`, every
     `IsCodeWritingRole`): no comments by default, two exceptions (repo/lint conventions,
     genuine "why").
   - **Visual bar** (`VisualBar`, marker `YOUR VISUAL BAR`, `IsUiBuildingRole` only): brand
     tokens, the five states, focus rings, 375/1280 check. Long form: `ui-craft`.
2. Project memory (`## Project memory`, budgeted 4k — see "Project memory" below)
3. Phase assignment + response format (planning JSON — the `approach` must carry *Existing
   code to reuse* / *Design* / *Testing strategy* / *Risks, refactor-first* sections; developing
   rules incl. the self-review and, when the repo has a `CheckCommand`, the notice that the
   platform will run it; QA report; review JSON with the orchestrator-owned **"What blocks"**
   list of objective engineering defects that justify `request_changes`)
4. `ClaudeMdGuidelines` (planning and developing)
5. `AgentLanguage.Instruction(language)` — the language is the PROJECT's (`ProjectSettings.Language`, set at creation from the creator's UI locale, edited on the project Settings page), resolved at prompt-build time via `ProjectLanguage.Resolve`; the requester's UI locale is only the fallback for projects that predate the setting. The navbar language switch changes UI text, never what agents write.

The user message carries untrusted content (task title/description, conversation, context
files) wrapped in `PromptSafety.Notice` + `PromptSafety.Wrap` fences.

**Agent chat** (`SendMessageEndpoint.BuildSystemPrompt`): persona → user instructions
(capped 8k) → role capability injections (PM tasks / Designer assets / Architect diagrams) →
document + interactive-question blocks → **`PromptSafety.ChatNotice`** (chat messages stay
instructions, but can never override platform rules) → repos → memories → locale → summary →
history. User-authored messages (history and the current turn) are fenced in
`UNTRUSTED USER-MESSAGE` markers; prompt caps keep the total under the Windows
command-line limit.

Create/update endpoints enforce the same caps up front (`CreateAgentValidator`:
name ≤ 100, `SystemPrompt` ≤ 12k, `UserInstructions` ≤ 4k) so nothing a user writes is
silently truncated at execution time.

### Project memory (shared, inspectable)

`agents.project_memories` — project-scoped rows with an optional `AgentId` (null = the whole
team), a `Title`, a `Type` (`Decision` / `Convention` / `Preference` are the project's standing
rules; `Learning` / `Mistake` / `Pattern` are experience), `Source` (Agent / User / System),
`IsPinned`, `IsArchived`, `UseCount`/`LastUsedAt`, and an optional `DedupeKey` (upsert).
Retrieval (`MemoryRetriever`, exposed as `IAgentsApi.RetrieveMemoriesAsync`) is by relevance,
never "newest N": (1) pinned rows + every rule, (2) SQLite FTS5 `bm25` rank
(`MemorySearch`: the content words of the task title+description or the chat message, each
quoted and OR-joined, matched against `project_memories_fts`; DATABASE.md → Full-text search), (3) the agent's newest own notes — deduped and capped
by `Take` and a char budget; retrieved rows get `UseCount++`. `MemoryPromptSection.Render`
(Agents.Contracts) produces the block both the pipeline and chat inject, so they can't drift.

Writers: agents call `remember(title, content, type, scope)` / `recall(query)` (chat tool
server + pipeline tool server; team-wide by default), `MemoryExtractor` writes one private
outcome note per (task, lane, agent) after each run (dedupe key replaces on retry), and humans
add/edit/pin/archive/delete on the Memory page (`AgentsConfigure`; audited `MemoryChanged`).
`GET /api/projects/{id}/memories/retrieve?q&agentId` previews exactly what a run would get.
Deleting an agent sets `AgentId` null (its notes become team knowledge) rather than cascading.

### Skills (per-project SKILL.md library)

`agents.agent_skills` — slug, name, one-line description, markdown body (≤ 32k), enabled
flag; twelve built-ins are seeded per project (`SkillSeeder.BuiltIns`; permanent like agents,
disable rather than delete; `SkillBackfillService` tops older projects up at startup):

| Built-in | Default roles |
|---|---|
| `groom-backlog`, `release-notes` | PM |
| `security-audit`, `design-review`, `engineering-review` | CodeReviewer |
| `dependency-update` | DevOps |
| `code-craft` | every code-writing role (dev roles + DevOps) |
| `frontend-craft` | FrontendDev, FullStackDev |
| `testing-craft` | dev roles |
| `ui-craft`, `brand-consistency` | FrontendDev, FullStackDev, MobileDev (+ Designer for brand) |
| `ux-direction` | Designer |

`ProjectAgent.SkillIds` is the assignment. Injection follows Claude Code's
progressive-disclosure model: assigned + enabled skills are written to the worktree as
`.orkka/context/skills/<slug>/SKILL.md` (`TaskOrchestrator.LoadSkillsSectionAsync`) and the
prompt carries only a `## Skills available` list (slug — description — path) with "read the
file when the task matches", so prompt cost stays flat however long the skill is. Chat: a
message starting with `/<slug>` (`ChatTurnPreparer` → `SkillInvocation`) prepends the full
body as `<skill name="…">…</skill>` — the same path schedules use to run playbooks.

### Schedules, reminders, triggers

`agents.agent_schedules` (+ `agent_schedule_runs`): kinds `Playbook` (agent + skill +
message → a chat turn in a dedicated conversation, requester = the schedule's creator),
`AutopilotStart`/`AutopilotStop` (with a spend cap), `Reminder` (one-shot, set by an agent
via the `set_reminder` chat tool, fired back into the originating conversation). Cronos
parses cron + IANA time zone; `ScheduleProcessor` (the host — one process, no
election; 30 s tick, row leases) fires at most 10 per tick and auto-disables after 5
consecutive failures (`ScheduleFailed` notification). `ProjectSettings.AutomationEnabled`
is the master switch. (The GitHub issue / CI-failure triggers went with the GitHub App in P1;
`TaskEntity.ExternalRef` is now the dedupe key for tracker tickets — see CLAUDE.md → Ticket source.)

### Task chat ("Discuss", P8)

The task detail's **Discuss** tab is an immediate back-and-forth with an agent about ONE task —
the ticket, the repo, the plan — instead of a thread message the next pipeline run reads.

- **Model.** `agents.agent_conversations.TaskId` (nullable; unique filtered index on
  `(TaskId, AgentId)`): one conversation per (task, agent), titled `#N <title>` at creation (no
  generated title). Never listed on the Team page (`ListConversationsEndpoint` filters
  `TaskId == null`) and never driven through the Team route (`SendMessageEndpoint` refuses it).
  Deleting a (Drafting) task deletes its conversations (`IAgentsApi.DeleteTaskConversationsAsync`).
- **Endpoints** (`Features/TaskChat`): `GET /api/tasks/{taskId}/chat?agentId=` (the
  conversation, created lazily, + messages, the project's agents for the picker, `defaultAgentId`,
  `lane`, `hasActiveRun`); `POST /api/tasks/{taskId}/chat` `{content, agentId?}` (SSE, the agent
  chat's event contract; cancel = the existing `…/conversations/{id}/cancel-run`);
  `POST /api/tasks/{taskId}/chat/apply` `{messageId? | text? | summarize: true, agentId?}`. Writes
  need `TasksExecute` (the same as posting a task message).
- **Agent** (`TaskChatAgentSelector`): the task's `CurrentAgentId`; else the PM before planning
  (Drafting/Ready, or questions asked from there) and the dev agent after (assigned agent, else
  the repo-scoped then any dev agent by `AgentAssigner` priority). The UI may pick any project agent.
- **Turn.** The agent chat machinery end to end — `ChatTurnPreparer` (memories, the base folder's
  sibling repos, resume, tools) → `ChatRunCoordinator`/`ChatTurnRunner` (detached run, persistence,
  usage with `TaskId` and `FlatCostBase 0`) → `HybridChatAgentInvoker` (the requester's runner).
  `ChatTurnInput.Task` (`TaskChatTurn`) adds the **task-context block** (`TaskChatContextBuilder`,
  injected after the persona/skills by `BuildSystemPrompt(taskContext:)`, capped 9k): description,
  the tracker ticket (fenced `TRACKER-TICKET`), the story section (`ITaskStoryContextSource` — the
  P7 hook, empty until Execution registers it), the current plan (pending, else latest approved),
  open questions, the last 20 thread messages, the diffstat from `TaskCodeChangeSnapshot`.
  Everything human-, tracker- or agent-written is fenced like the pipeline prompts.
- **Workspace: read-only, on the task branch.** `IChatAgentInvoker.EnsureChatWorkspaceAsync(…,
  taskId)` + `ChatWorkspaceRepo.Branch` → relay `TaskId`/`Branch` → runner
  `EnsureChatWorkspaceRequest.TaskId`/`ChatWorkspaceRepoDto.Branch`: the runner builds
  `{repos}/{org}/task-chat-workspace/{taskId}/` (outside the project chat workspace), each repo
  detached at `origin/{task branch}` when that exists on the remote (vetted name), else the
  default branch; token `runner-chat:{org}:{project}:{task}`. The task's running worktree is never
  touched; `CleanupWorktree` removes the Discuss copy with it. Turns run in the Research phase:
  Claude pre-approves only Read/Glob/Grep/WebSearch/WebFetch and keeps the CLI's permission gate
  up (`ClaudeAgentInvoker.AllowedToolsFor`/`LiftsPermissionGate` — how planning stays read-only),
  Codex `--sandbox read-only` (`SandboxArgs`), Copilot `--deny-tool write --deny-tool shell`
  (`ToolPermissionArgs`).
- **Runner offline:** `IChatAgentInvoker.GetRouteBlockerAsync` fails the turn up front with an
  error chunk naming `./orkka start`; nothing is saved.
- **Apply to task** (`TaskChatService.ApplyAsync`): the chosen reply, free text, or a utility-model
  summary of the discussion (`summarize`, on the runner, $0, 503 naming `./orkka start` when it
  cannot run) is posted as a HUMAN message by the user, prefixed `From discussion:`, through
  `ITasksApi.AnswerTaskAsync`. PendingQuestions → it is the answer and the task resumes
  (`answered`); PlanReady → the pending plan is rejected through `ReviewPlanAsync` pointing at that
  message, so the task re-plans (`planChangesRequested`); otherwise a thread message the next run
  reads (`posted`, with AnswerTaskAsync's usual resumes for WaitingUserVerification/WaitingCodeReview).
  Returns `{lane, status, action, messageId, content}`.

---

## API Endpoints

```
# Agent management
GET    /api/projects/{id}/agents              # List all agents in the pod
GET    /api/projects/{id}/agents/{agentId}    # Get agent details + stats
POST   /api/projects/{id}/agents              # Add a new agent to the pod
PUT    /api/projects/{id}/agents/{agentId}    # Update name, instructions, model, avatar
PUT    /api/projects/{id}/agents/model        # Set one model on every agent (QA skipped without MCP)
POST   /api/projects/{id}/agents/switch-provider  # Remap every agent to a provider's tier defaults
PATCH  /api/projects/{id}/agents/{agentId}/toggle  # Enable/disable agent
DELETE /api/projects/{id}/agents/{agentId}    # Remove agent (cannot remove last dev agent)

# Project memory
GET    /api/projects/{id}/memories?q&type&scope&pinned&archived&cursor&limit
GET    /api/projects/{id}/memories/retrieve?q&agentId       # what a run would see
POST   /api/projects/{id}/memories                          # AgentsConfigure
PUT/DELETE /api/projects/{id}/memories/{memoryId}

# Skills
GET/POST   /api/projects/{id}/skills                    # List / create (AgentsConfigure to write)
GET/PUT/DELETE /api/projects/{id}/skills/{skillId}
PUT    /api/projects/{id}/agents/{agentId}/skills       # Assign { skillIds[] }

# Schedules (TasksExecute)
GET/POST   /api/projects/{id}/schedules
GET/PUT/DELETE /api/projects/{id}/schedules/{scheduleId}
POST   /api/projects/{id}/schedules/{scheduleId}/run    # Run now
GET    /api/projects/{id}/schedules/{scheduleId}/runs

# Task assignment
POST   /api/tasks/{id}/assign/{agentId}       # Manually assign agent to task
POST   /api/tasks/{id}/reassign               # Trigger auto-reassignment

# Agent interaction
POST   /api/tasks/{id}/ask/{agentId}          # Direct a question to a specific agent
```

---

## Frontend: Team Page

Each project has a `/projects/{id}/team` page showing the pod:

- Grid of agent cards with avatar, name, role, model badge, active/inactive toggle
- Click an agent → detail panel with: edit name, edit instructions, change model, view stats
- "Add Agent" button → select role, name it, choose model
- Each card shows: tasks completed, success rate, total cost, sparkline of recent activity

### Task Card Updates

On the kanban board, each task card now shows:
- Small avatar of the currently assigned agent
- Agent name in the task detail view
- All messages in the conversation thread tagged with agent avatar + name

---

## Multi-Agent Task Flow (Full Pipeline)

```
1. User creates task in Drafting lane
2. User moves to Ready lane
3. PM (Luna) picks up:
   - Reviews description for completeness
   - Asks questions or suggests improvements
   - When satisfied, approves task (system moves to Planning)
4. Dev agent (auto-assigned) picks up Planning:
   - Creates structured execution plan
   - System moves to PlanReady
5. User reviews plan, approves
6. Same Dev agent executes in Developing:
   - Writes code in worktree (to the engineering bar; self-reviews the diff, runs lint/tests)
   - Can ask questions → PendingQuestions → user replies → back to Developing
   - Commits and pushes branch
   - Check gate: if the repo has a `CheckCommand`, the platform runs it in the worktree;
     a non-zero exit posts the output to the thread and sends the task back to Developing
     (`Worker:MaxCheckCycles`, default 2, then parked for a human)
   - Creates PR
7. QA (Scout) picks up Testing:
   - Reads the diff
   - Starts the app from the repo's QA run command and drives it on localhost via Playwright MCP
   - Interacts with the live app: clicks, types, navigates like a real user
   - Validates each acceptance criterion against the live app
   - Takes screenshots as evidence
   - Runs existing test suite for regression
   - Writes NEW persistent E2E test cases for the changes
   - Reports pass/fail with evidence → if fail, task goes back to Developing with details
8. Code Reviewer (Sentinel) picks up Code Review:
   - Reviews the diff (correctness, security, and the objective engineering defects in
     "What blocks" / the `engineering-review` skill; taste stays [Suggestion])
   - Leaves line comments on Orkka's own pull request (`IPullRequestService`)
   - Approves or requests changes → if changes needed, back to Developing
9. Task moves to Done — the merge sweep squash-merges it into a linked folder, or publishes it
   as a forge PR opened by the developer's own CLI (`TaskEntity.PublishedPrUrl`)
```

Each handoff is logged in the task thread with the agent's identity so the user sees the full team collaboration history.

---

## QA Agent: Playwright MCP Integration

### Overview

Scout (QA) uses the Playwright MCP server to test the live application in a real headless browser. Instead of just reading code and writing test files blindly, Scout actually navigates to the running app, interacts with it, and validates behavior — exactly like a human QA tester would.

Playwright MCP uses the browser's accessibility tree (~2-5KB structured data) instead of screenshots for assertions, keeping token usage low and responses fast.

### How Scout reaches the app

Scout starts the app itself, inside the task's checkout, and tests it on `localhost`. The run
command is resolved before Scout is invoked (`TaskOrchestrator`, Testing phase), first match wins:

1. `ProjectRepo.QaRunCommand` — per repo, so a project with a frontend and a backend can start each
2. `ProjectSettings.QaRunCommand` — the older project-level setting
3. Derived from the project's run targets (`QaRunCommandDerivation.Derive` — the Run button's own
   config, or the repo's `orkka.json`)

No command at all escalates the task to human verification **before** Scout is started — cheaper
than letting the agent discover it cannot start the app. The prompt tells Scout to start the
command in the background, poll the local URL with `curl`, drive it with the Playwright tools,
check every changed screen at 375x812 and 1280x800 (horizontal-scroll test + one screenshot
each), and kill the process before finishing. There is no preview-deployment path: Orkka
deploys nothing.

### Agent SDK Configuration for Scout

```csharp
public ClaudeAgentOptions BuildQAAgentOptions(
    ProjectAgent agent, string worktreePath, string? testUrl)
{
    var options = new ClaudeAgentOptions
    {
        Cwd = worktreePath,
        Model = agent.Model,
        SystemPrompt = BuildSystemPrompt(agent),
        AllowedTools = ["Read", "Write", "Edit", "Grep", "Glob", "Bash"],
        PermissionMode = PermissionMode.AcceptEdits,
        MaxTurns = 50,
        Env = new Dictionary<string, string>
        {
            ["TEST_URL"] = testUrl ?? "http://localhost:3000"
        },
        McpServers = new Dictionary<string, McpServerConfig>
        {
            ["playwright"] = new McpStdioServerConfig
            {
                Command = "npx",
                Args = ["@playwright/mcp", "--headless"]
            }
        }
    };

    return options;
}
```

### Scout's Available Playwright MCP Tools

| Tool | Purpose |
|------|---------|
| `browser_navigate` | Go to a URL |
| `browser_click` | Click an element (by accessibility label/role) |
| `browser_type` | Type text into an input field |
| `browser_select_option` | Select from a dropdown |
| `browser_snapshot` | Get accessibility tree (~2-5KB, cheap) |
| `browser_take_screenshot` | Capture PNG screenshot (for evidence) |
| `browser_resize` | Change viewport (test mobile: 375x812, desktop: 1280x720) |
| `browser_go_back` | Navigate back |
| `browser_go_forward` | Navigate forward |
| `browser_wait` | Wait for network idle or element to appear |
| `browser_press_key` | Press keyboard keys (Enter, Tab, Escape) |
| `browser_hover` | Hover over elements (test tooltips, dropdowns) |

### Example QA Session (streamed to frontend via SignalR)

```
Scout: Starting QA validation for task "Fix login redirect on mobile"
Scout: Testing acceptance criteria: "User can log in on mobile and reach dashboard"

[browser_resize] Setting viewport to 375x812 (mobile)
[browser_navigate] Navigating to http://localhost:3000/login
[browser_snapshot] Login page loaded. Found: email input, password input, "Sign In" button
[browser_type] Entering test@example.com in email field
[browser_type] Entering password in password field
[browser_click] Clicking "Sign In" button
[browser_snapshot] Page navigated to /dashboard. Found: "Welcome back" heading, sidebar nav
[browser_take_screenshot] Saved: dashboard-mobile-login-success.png

✅ Acceptance criterion met: User can log in on mobile and reach dashboard

[browser_resize] Setting viewport to 1280x720 (desktop)
[browser_navigate] Navigating to /login
... (repeats flow for desktop)

✅ Desktop login also works

Running existing test suite: npx playwright test
  ✅ 23 tests passed
  ❌ 0 tests failed

Writing new test case: tests/auth/mobile-login-redirect.spec.ts
  - Navigates to /login on mobile viewport
  - Enters credentials
  - Asserts redirect to /dashboard
  - Asserts welcome heading is visible

FINAL REPORT:
✅ All acceptance criteria met
✅ Regression suite passes (23/23)
✅ 1 new E2E test added
📸 2 screenshots attached
```

### Where the browser runs

On the developer's machine. Playwright MCP, Node.js and the browsers are the runner's
(run by `./orkka start`) prerequisites; the host (Orkka.Local) runs no agent.

### Cost Considerations

Playwright MCP is token-efficient because it uses accessibility tree snapshots (~2-5KB) instead of screenshots (~500KB-2MB) for assertions. A typical QA session:

| Action | Token Cost |
|--------|-----------|
| `browser_snapshot` (accessibility tree) | ~200-500 tokens |
| `browser_take_screenshot` (only for evidence) | ~1500-3000 tokens |
| Reading code diff | ~500-2000 tokens |
| Writing new test file | ~500-1500 tokens |
| Full QA session (navigate + 5 validations + write tests) | ~8,000-15,000 tokens |

The session runs on the developer's own AI subscription; Orkka records the tokens (Usage module) and bills nothing.

### Screenshots Storage

Screenshots taken by Scout are uploaded to blob storage (a directory under the data dir on Orkka.Local) and linked to the task:

```csharp
// After Scout completes, extract screenshots from the agent's output
foreach (var screenshot in agentOutput.Screenshots)
{
    var url = await blobStorage.UploadAsync(
        screenshot.Data, $"qa-{task.Id}-{screenshot.Name}.png", "image/png", ct);

    db.TaskAttachments.Add(new TaskAttachment
    {
        OrgId = task.OrgId,
        TaskId = task.Id,
        FileUrl = url,
        FileType = "image/png",
        FileName = screenshot.Name
    });
}
```

These screenshots appear in the task detail view alongside Scout's test report, giving users visual proof of what was tested.

---

## Designer Agent: The Design Feature

### Overview

Muse (Designer) creates visual assets from **Team chat** — UI mockups, logos, icons, favicons, illustrations, hero art, banners and animated SVG, always as SVG. Assets land on the project's **Design page** (`/p/{projectId}/design`) where the user approves/rejects them; approved assets are injected into frontend dev-agent task context for use in code. Muse never commits to repositories.

**SVG only.** Muse (and Sprite, the Game Designer) author the markup themselves, on the developer's runner, in the reply or the `create_design_assets` tool call. Orkka's servers never call an image model: there is no raster image or GIF generation, no image provider, and no per-agent image-provider setting (`ProjectAgent.ImageProvider` is a retired column kept so old rows load). The prompt blocks (`DefaultPrompts.DesignerAssetCreation` / `DesignerAssetCreationTools`) and the tool schema (kind `svg` only) tell the agent never to promise photographs, bitmaps or GIFs. Uploaded PNG/JPEG/WebP/GIF files are still filed as `Image`/`Gif` assets, and assets generated before the removal keep loading.

### The Flow

```
1. User chats with Muse: "Create a hero image for the landing page"
2. Muse reads brand context (tailwind config, CLAUDE.md) with Read/Glob/Grep
3. Muse ends its reply with a ```design block (JSON: assets[] of kind svg), or calls
   create_design_assets
4. DesignAssetGenerator validates each asset (must start <svg, ≤200 KB) and uploads it
   as-is. Any other kind is refused with an instruction to re-deliver it as SVG; a legacy
   fenced kind "image" with only a prompt is drawn as an SVG on the requester's runner
5. Blobs land in container "design-assets" ({orgId}/{projectId}/{assetId}.{ext});
   SSE events stream progress + ready cards into the chat
6. User reviews on the Design page: Approve / Reject (with feedback) / Iterate / Delete
7. Iterate deep-links back into Muse's chat (?agent=&tab=chat&asset=) — the client prepends
   a visible [design-asset:<id> "name" vN] marker line; the server resolves it into prompt
   context and Muse sets basedOnAssetId and sends the complete updated SVG as the next version
8. Approved assets are injected into FrontendDev/FullStackDev Developing prompts as
   ready-to-run curl commands (3-hour read-signed URLs, cap 30) — the agent downloads
   what the task needs into public/ and references the committed files
```

### Entity (Agents module, table `agents.design_assets`)

```csharp
public sealed class DesignAsset : TenantEntity
{
    public Ulid ProjectId { get; set; }            // gallery scope
    public Ulid AgentId { get; set; }              // Muse
    public Ulid? ConversationId { get; set; }      // chat provenance
    public Ulid? MessageId { get; set; }
    public Ulid? TaskId { get; set; }              // reserved, unused
    public required string Name { get; set; }      // "hero-banner"
    public required string FileName { get; set; }  // "hero-banner.png"
    public DesignAssetKind Kind { get; set; }      // Svg | Image | Gif
    public required string MimeType { get; set; }
    public required string BlobContainer { get; set; }
    public required string BlobName { get; set; }  // signed URLs generated on demand
    public long SizeBytes { get; set; }
    public string? Prompt { get; set; }            // legacy: image prompt / GIF frames of pre-removal assets
    public required string SourceModel { get; set; } // "claude-chat" (legacy rows: an image model id)
    public Ulid? ParentAssetId { get; set; }       // iteration lineage
    public Ulid RootAssetId { get; set; }          // groups versions
    public int Version { get; set; } = 1;
    public DesignAssetStatus Status { get; set; }  // Pending | Approved | Rejected
    public string? Feedback { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
}
```

### Endpoints (project-scoped; reads open to members, writes need `Permission.DesignReview`)

```
GET    /api/projects/{projectId}/design-assets                    # ?status= filter
GET    /api/projects/{projectId}/design-assets/{assetId}
POST   /api/projects/{projectId}/design-assets/{assetId}/approve  # supersedes older approved versions in the lineage
POST   /api/projects/{projectId}/design-assets/{assetId}/reject   # body: { feedback? }
DELETE /api/projects/{projectId}/design-assets/{assetId}          # row + blob
```

### Usage

There is no image-model spend and no storage charge. The chat turn itself runs on the runner like every turn.

A legacy fenced kind "image" request is drawn as an SVG on the **requester's runner** (`IUserRunnerUtility`, recorded at $0), never on Orkka's key. With the runner offline the asset fails with "Your runner is offline or unavailable — run `./orkka start` to let it draw this asset as an SVG". The same runner-only rule holds for the chat's own housekeeping (conversation titles, the rolling summary): a runner miss skips it, and the next turn tries again. See CLAUDE.md → Usage for every utility call and what a miss does.

---

## Solution Architect (Archie) — Non-Pipeline Agent

### Overview

Archie is unique among the agent team — he is NOT part of the task pipeline. He does not get assigned to lanes, does not process tasks, and does not write application code. Instead, Archie is available **anytime via Agent Chat** on the teams page for architectural analysis, system design discussions, and diagram creation.

### Capabilities

- **Architecture Analysis**: Reads all project repos to understand system components, dependencies, and data flow
- **Diagram Generation**: Outputs Excalidraw-compatible JSON diagrams that the frontend renders in a whiteboard component
- **Bottleneck Identification**: Analyzes code patterns for N+1 queries, missing caching, connection pool exhaustion, etc.
- **Scalability Planning**: Proposes horizontal/vertical scaling strategies with cloud-specific recommendations
- **Cloud Expertise**: Deep knowledge of AWS, Azure, GCP services and pricing
- **System Design**: Networking, caching, message queues, microservices patterns, database scaling

### Diagram Output Format

Archie outputs diagrams as Excalidraw JSON wrapped in fenced code blocks:

````
```excalidraw
{
  "type": "excalidraw",
  "version": 2,
  "source": "orkka-archie",
  "elements": [...],
  "appState": { "viewBackgroundColor": "#ffffff", "gridSize": 20 }
}
```
````

The system parses these blocks and stores them as `ArchitectureDiagram` entities with the JSON as text. Users can view and edit diagrams in the frontend whiteboard tool.

### Diagram Types

| Type | Description |
|------|-------------|
| SystemArchitecture | High-level system components and connections |
| DataFlow | How data moves through the system |
| NetworkTopology | Network layout, VPCs, subnets, load balancers |
| SequenceDiagram | Request/response flow between components |
| ERDiagram | Entity relationships and database schema |
| DeploymentDiagram | Infrastructure and deployment topology |
| ComponentDiagram | Internal component structure of a service |

### Database: ArchitectureDiagrams Table

```sql
CREATE TABLE architecture_diagrams (
    id UUID PRIMARY KEY,
    org_id UUID NOT NULL,
    project_id UUID NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    agent_id UUID NOT NULL REFERENCES project_agents(id) ON DELETE CASCADE,
    conversation_id UUID NOT NULL REFERENCES agent_conversations(id) ON DELETE CASCADE,
    message_id UUID REFERENCES agent_messages(id) ON DELETE SET NULL,
    title VARCHAR(500) NOT NULL,
    type VARCHAR(30) NOT NULL,         -- DiagramType enum as string
    diagram_data TEXT NOT NULL,        -- Excalidraw-compatible JSON
    version INTEGER NOT NULL DEFAULT 1,
    description TEXT,
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL
);

CREATE INDEX idx_arch_diagrams_org ON architecture_diagrams(org_id);
CREATE INDEX idx_arch_diagrams_project_agent ON architecture_diagrams(project_id, agent_id);
CREATE INDEX idx_arch_diagrams_conversation ON architecture_diagrams(conversation_id);
```

### API Endpoints

| Method | Endpoint | Purpose |
|--------|----------|---------|
| GET | `/api/projects/{projectId}/diagrams` | List diagrams (optional `?conversationId=` filter) |
| GET | `/api/projects/{projectId}/diagrams/{diagramId}` | Get single diagram with full data |
| PUT | `/api/projects/{projectId}/diagrams/{diagramId}` | Update diagram (title, data, description) |
| DELETE | `/api/projects/{projectId}/diagrams/{diagramId}` | Delete diagram |

### Seeding

Archie is seeded for **every project**. He is always cross-repo (`RepoId = null`).
