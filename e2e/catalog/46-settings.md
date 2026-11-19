---
area: SET
title: Settings — account, preferences, organization, project settings
modes-default: [all]
---

Org/account settings live under `/settings/*` (tab strip in `settings/layout.tsx`); project settings under `/p/{slug}/settings` (General + agent language, pipeline automation, environment variables for Managed apps, danger zone). Pipeline toggles save instantly with optimistic update + revert; `PATCH /api/projects/{id}` replaces the whole `settings` JSON, so the UI always spreads the current object. Billing and Runner tabs have their own areas.

## SET-001 Render the settings tab strip with correct order and highlighting
```yaml
priority: P1
surface: [ui]
automation: planned
refs:
  - orkka-ui:/settings
  - orkka-ui:/settings/workspace
  - orkka-ui:/settings/machine
  - orkka-ui:/settings/trackers
  - orkka-ui:/settings/usage
  - orkka-ui:/settings/api-keys
  - orkka-ui:/settings/preferences
  - orkka-ui:/settings/notifications
  - orkka-ui:apps/local/app/[locale]/(dashboard)/settings/layout.tsx
  - i18n:settingsNav
```
**Preconditions**: signed in.
**Steps**:
1. Open `/settings`; read the tabs; click each in turn.
**Expected**:
- `/settings` opens `/settings/workspace`. Heading "Settings"; tabs in order: Workspace, Machine, Trackers, Usage, MCP keys, Preferences, Notifications.
- The active tab is white with an underline, by prefix match on its path.
**Notes**: Account, Billing, Organization, Integrations, My Runner and Audit log tabs went with the cloud product (2026-11-17).

## SET-004 Keep the workspace name read-only for API keys
```yaml
priority: P2
surface: [api]
automation: planned
refs:
  - orkka-api:PUT /api/org
  - orkka-api:GET /api/org/me
```
**Preconditions**: Owner-minted key with all permissions.
**Steps**:
1. `PUT /api/org {name:"hijack"}` with the key.
2. `GET /api/org/me`.
**Expected**:
- Step 1 → 403 (renaming is Owner-only and a key is never Owner); the name is unchanged.
**Notes**: renaming in the UI is LWF-013.

## SET-005 Render the Preferences tab
```yaml
priority: P1
surface: [ui]
automation: planned
refs:
  - orkka-ui:/settings/preferences
  - i18n:preferencesPage
```
**Preconditions**: owner signed in.
**Steps**:
1. Open `/settings/preferences`.
**Expected**:
- Heading "Preferences" / "How Orkka looks and feels for you.".
- Row "Show technical details" / "Show repositories, skills, memory, diagrams and other developer-level settings across the app." with a toggle.
- Row "Replay the product tour" / "Walks you through the app again, page by page." with button "Start tour" (or text "Available once a project has finished setting up." and the button disabled when no Ready project exists).
**Notes**: none.

## SET-006 Persist the Show technical details preference
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:PUT /api/org/me/preferences
  - orkka-api:GET /api/org/me
  - orkka-ui:/settings/preferences
  - orkka-ui:packages/ui/src/components/settings/TechnicalDetailsToggle.tsx
```
**Preconditions**: preference currently `true`.
**Steps**:
1. Switch "Show technical details" off; reload the page.
2. `GET /api/org/me`.
3. Switch it back on.
**Expected**:
- `PUT /api/org/me/preferences {showTechnicalDetails:false}` → 200; after reload the toggle is still off.
- Step 2 `preferences.showTechnicalDetails:false`; `onboardingTourSeen` untouched (partial update).
- Step 3 persists `true`.
**Notes**: none.

## SET-008 Replay the product tour from Preferences
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-api:PUT /api/org/me/preferences
  - orkka-ui:/settings/preferences
  - orkka-ui:packages/ui/src/components/settings/ReplayTourButton.tsx
  - i18n:tour
```
**Preconditions**: a Ready project; tour previously finished/skipped (`onboardingTourSeen:true`).
**Steps**:
1. Click "Start tour".
**Expected**:
- `PUT /api/org/me/preferences {onboardingTourSeen:false}` → 200; navigation to `/p/{slug}`; `data-testid="product-tour"` appears with "Welcome to Orkka" and "1 of N".
- Skipping (NAV-018) re-sets `onboardingTourSeen:true`.
**Notes**: none.

## SET-009 Render the Notifications tab and toggle desktop notifications
```yaml
priority: P2
surface: [ui]
automation: planned
refs:
  - orkka-ui:/settings/notifications
  - orkka-ui:packages/ui/src/components/notifications/DesktopNotificationsToggle.tsx
  - i18n:notifications
```
**Preconditions**: browser context with the `notifications` permission pre-granted (Playwright `grantPermissions`); a second context with it denied.
**Steps**:
1. Open `/settings/notifications`; switch "Desktop notifications" on; reload.
2. In the denied context open the page.
**Expected**:
- Heading "Notification Preferences" / "You'll always see task notifications in the bell. Desktop notifications are optional and per device."; row "Desktop notifications" / "Also show notifications from your operating system, like Teams or Slack.".
- Granted: toggle stays on after reload (localStorage, per device — no API call).
- Denied: toggle disabled with "Notifications are blocked in your browser settings for this site.".
**Notes**: none.

## SET-012 Render the project settings page
```yaml
priority: P1
surface: [ui]
automation: automated
refs:
  - orkka-ui:/p/[projectId]/settings
  - i18n:ticketSource
since: 2026-09-15
```
**Preconditions**: A project with one repo.
**Steps**:
1. Open `/p/{slug}/settings`.
**Expected**:
- Cards "General" (Project Name, Description, "Save Changes", "Agent language"), "Ticket source" (TSRC), "Pipeline automation", "Danger Zone"; no environment-variable or custom-domain cards exist.
- Sidebar "Settings" highlighted.

## SET-013 Save the project name and description
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:PATCH /api/projects/{id}
  - orkka-api:GET /api/projects/{id}
  - orkka-ui:/p/[projectId]/settings
  - i18n:settingsPage
```
**Preconditions**: project `U`.
**Steps**:
1. Change "Project Name" to `e2e-renamed-{stamp}` and "Description" to `desc {stamp}`; click "Save Changes".
2. `GET /api/projects/{id}`.
**Expected**:
- Button reads "Saving..." then returns; `PATCH /api/projects/{id} {name, description}` → 200.
- Sidebar project name and top-bar breadcrumb update to the new name; API returns both fields.
**Notes**: the slug does not change with the name (project URLs stay valid).

## SET-014 Gate general settings on ProjectsManage
```yaml
priority: P1
surface: [api, ui]
automation: planned
refs:
  - orkka-api:PATCH /api/projects/{id}
  - orkka-ui:/p/[projectId]/settings
```
**Preconditions**: key `K-ro` (no ProjectsManage).
**Steps**:
1. `PATCH /api/projects/{id} {name:"x"}` with `K-ro`.
2. (Member session without ProjectsManage, manual) open the page.
**Expected**:
- Step 1 → 403.
- Step 2: no "Save Changes" button; every pipeline toggle and the language select disabled.
**Notes**: none.

## SET-015 Set the agent language for the project
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:PATCH /api/projects/{id}
  - orkka-ui:/p/[projectId]/settings
  - orkka-ui:packages/ui/src/components/project/ProjectLanguageSetting.tsx
  - i18n:settingsPage
```
**Preconditions**: project with `settings.language` null (or note the current value).
**Steps**:
1. Read the "Agent language" row.
2. Choose "Portuguese" in the select; `GET /api/projects/{id}`.
3. Choose "Automatic (each person's own language)".
**Expected**:
- Null language shows the amber warning "No language is set for this project, so each agent reply follows the language the requester's browser asks for…".
- Step 2: `PATCH … {settings:{…all current settings…, language:"pt"}}` → 200; API `settings.language:"pt"`; warning gone.
- Step 3 saves `language:null` (not `""`); warning returns.
**Notes**: none.

## SET-016 Make agents answer in the project language
```yaml
priority: P2
modes: [hybrid-repo]
surface: [ui, api]
automation: planned
costUsd: 0
refs:
  - orkka-api:PATCH /api/projects/{id}
  - orkka-api:POST /api/projects/{projectId}/agents/{agentId}/conversations
  - orkka-ui:/p/[projectId]/team
```
**Preconditions**: SET-015 saved `language:"pt"`; UI locale left as English.
**Steps**:
1. Open `/p/{slug}/team`, pick Luna, "New conversation", send "Say hello in one short sentence.".
**Expected**:
- Reply is in Portuguese even though the dashboard is English (project language wins over `Accept-Language`).
- Restoring "Automatic" makes replies follow the requester's locale.
**Notes**: none.

## SET-017 Change Parallel tasks and observe the same-repo toggle gate
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:PATCH /api/projects/{id}
  - orkka-ui:/p/[projectId]/settings
  - orkka-ui:packages/ui/src/components/project/PipelineAutomationSettings.tsx
  - i18n:pipelineSettings
```
**Preconditions**: technical details on; project `U`.
**Steps**:
1. In "Pipeline automation" set the "Parallel tasks" select to `1`; observe "Parallel tasks in the same repo".
2. Set it to `3`.
3. `GET /api/projects/{id}`.
**Expected**:
- Options 1–5; at `1` the same-repo toggle is disabled; at `3` it is enabled.
- Each change → `PATCH {settings:{…, maxParallelTasks:n}}`; API `settings.maxParallelTasks:3`.
**Notes**: none.

## SET-018 Toggle Parallel tasks in the same repo
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-api:PATCH /api/projects/{id}
  - orkka-ui:/p/[projectId]/settings
  - i18n:pipelineSettings
```
**Preconditions**: `maxParallelTasks ≥ 2`.
**Steps**:
1. Switch "Parallel tasks in the same repo" on; `GET /api/projects/{id}`; switch off.
**Expected**:
- `settings.sameRepoParallelism` true then false; description text "When off, tasks touching the same repository run one at a time…".
**Notes**: none.

## SET-019 Toggle Automatic plan approval
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:PATCH /api/projects/{id}
  - orkka-ui:/p/[projectId]/settings
  - i18n:pipelineSettings
```
**Preconditions**: project `U`.
**Steps**:
1. Switch "Automatic plan approval" on; `GET /api/projects/{id}`; switch off.
**Expected**:
- `settings.autoApprovePlan` true → false; toggle state matches after reload.
**Notes**: effect on the pipeline (PlanReady skipped) is covered by the task-lifecycle area.

## SET-020 Toggle Automatic code review approval
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:PATCH /api/projects/{id}
  - orkka-ui:/p/[projectId]/settings
  - i18n:pipelineSettings
```
**Preconditions**: project `U`.
**Steps**:
1. Switch "Automatic code review approval" on; `GET`; switch off.
**Expected**:
- `settings.autoApproveCodeReview` true → false.
**Notes**: none.

## SET-022 Toggle the QA testing lane and set the QA run command
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:PATCH /api/projects/{id}
  - orkka-ui:/p/[projectId]/settings
  - i18n:pipelineSettings
```
**Preconditions**: project `U`.
**Steps**:
1. Switch "QA testing lane" on.
2. In "QA run command" (`id="qa-run-command"`, placeholder `npm run dev`) type `npm run dev -- --port 3000` and blur.
3. Focus and blur without changes; then clear it and blur.
4. `GET /api/projects/{id}`.
**Expected**:
- Command input appears only while QA is on.
- Blur with a change → `PATCH {settings:{…, qaRunCommand:"npm run dev -- --port 3000"}}`; unchanged blur sends nothing; cleared → `qaRunCommand:null`.
- API `qaEnabled:true`; the Testing lane appears on the board (board area asserts the lane).
**Notes**: none.

## SET-023 Toggle Manual verification
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-api:PATCH /api/projects/{id}
  - orkka-ui:/p/[projectId]/settings
  - i18n:pipelineSettings
```
**Preconditions**: project `U`.
**Steps**:
1. Switch "Manual verification" on; `GET`; off.
**Expected**:
- `settings.manualVerificationEnabled` true → false.
**Notes**: none.

## SET-024 Toggle Live documentation
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-api:PATCH /api/projects/{id}
  - orkka-ui:/p/[projectId]/settings
  - i18n:pipelineSettings
```
**Preconditions**: project `U`.
**Steps**:
1. Switch "Live documentation" on; `GET`; off.
**Expected**:
- `settings.documentationSyncEnabled` true → false.
**Notes**: none.

## SET-025 Toggle Archive done tasks after two weeks
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-api:PATCH /api/projects/{id}
  - orkka-ui:/p/[projectId]/settings
  - i18n:pipelineSettings
```
**Preconditions**: project `U`.
**Steps**:
1. Switch "Archive done tasks after two weeks" on; `GET`; off.
**Expected**:
- `settings.autoArchiveDoneTasks` true → false.
**Notes**: none.

## SET-026 Enable the task runtime limit and clamp the minutes
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:PATCH /api/projects/{id}
  - orkka-ui:/p/[projectId]/settings
  - i18n:pipelineSettings
```
**Preconditions**: project `U`, limit off.
**Steps**:
1. Switch "Task runtime limit" on.
2. In "Runtime limit (minutes)" (`id="task-runtime-limit"`) type `0` and blur; type `5000` and blur; type `abc` and blur; type `90` and blur.
3. `GET /api/projects/{id}`.
4. Switch the limit off.
**Expected**:
- Enabling sends `taskRuntimeLimitEnabled:true, taskRuntimeLimitMinutes:30` (default carried with the toggle); the box appears with hint "…Between 1 and 1440 minutes…".
- `0` → snaps to `1`; `5000` → `1440`; `abc` → snaps back to the stored value; `90` → saved.
- API `taskRuntimeLimitMinutes:90`; after step 4 `taskRuntimeLimitEnabled:false` and the box hides.
**Notes**: none.

## SET-027 Enable the task spend limit and clamp the credits
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:PATCH /api/projects/{id}
  - orkka-ui:/p/[projectId]/settings
  - i18n:pipelineSettings
```
**Preconditions**: project `U`, limit off.
**Steps**:
1. Switch "Task spend limit" on.
2. In "Spend limit (credits)" (`id="task-spend-limit"`, `step=100`) type `50` → blur; `99999999` → blur; `1,500` → blur; `2500` → blur.
3. `GET /api/projects/{id}`; switch off.
**Expected**:
- Enabling sends `taskSpendLimitEnabled:true, taskSpendLimitCredits:5000`; hint "…Between 100 and 10000000; 1,000 credits = US$1 of usage…".
- `50` → `100`; `99999999` → `10000000`; `1,500` → non-digits stripped → `1500`; `2500` saved.
- API `taskSpendLimitCredits:2500`; then `taskSpendLimitEnabled:false`.
**Notes**: none.

## SET-028 Toggle Show Agent tool calls
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-api:PATCH /api/projects/{id}
  - orkka-ui:/p/[projectId]/settings
  - i18n:pipelineSettings
```
**Preconditions**: project `U`.
**Steps**:
1. Switch "Show Agent tool calls" on; `GET`; off.
**Expected**:
- `settings.showAgentToolCalls` true → false.
**Notes**: none.

## SET-029 Revert a pipeline toggle when the save fails
```yaml
priority: P1
surface: [ui]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/settings
  - i18n:pipelineSettings
```
**Preconditions**: Playwright route interception returning 500 for `PATCH /api/projects/{id}`.
**Steps**:
1. Switch "Automatic plan approval" on.
**Expected**:
- Toggle flips on optimistically, all rows disable while saving, then it reverts to off and the text "Failed to save settings. Please try again." appears.
- `GET /api/projects/{id}` (without interception) still has `autoApprovePlan:false`.
**Notes**: none.

## SET-030 Preserve untouched settings across instant saves (whole-JSON PATCH regression)
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:PATCH /api/projects/{id}
  - orkka-api:GET /api/projects/{id}
  - orkka-ui:/p/[projectId]/settings
```
**Preconditions**: project `U` with `maxParallelTasks:4`, `qaRunCommand:"npm run dev"`, `language:"pt"` set via API.
**Steps**:
1. In the UI toggle "Live documentation" on, then "Show Agent tool calls" on, then change "Agent language" to "English".
2. `GET /api/projects/{id}`.
3. API: `PATCH /api/projects/{id} {settings:{autoApprovePlan:true}}` (partial object).
4. `GET /api/projects/{id}`.
**Expected**:
- Step 2: `maxParallelTasks:4`, `qaRunCommand:"npm run dev"`, `documentationSyncEnabled:true`, `showAgentToolCalls:true`, `language:"en"` — every UI save spread the full object.
- Step 4 documents the API contract: `settings` is replaced wholesale, so fields omitted in step 3 fall back to their defaults (`maxParallelTasks` back to default, `qaRunCommand` null). Clients must always send the complete object.
**Notes**: none.

## SET-036 Delete a project through the three-step danger-zone flow
```yaml
priority: P0
surface: [ui, api]
automation: planned
refs:
  - orkka-api:DELETE /api/projects/{id}
  - orkka-api:GET /api/projects/{id}
  - orkka-ui:/p/[projectId]/settings
  - i18n:settingsPage
```
**Preconditions**: disposable Unmanaged project `D` named `e2e-del-{stamp}`.
**Steps**:
1. In "Danger Zone" ("Irreversible actions. Proceed with caution.") click "Delete Project".
2. Step 1 modal: red lead "This action is permanent." + "All project data, tasks, and agent history will be permanently deleted and cannot be recovered."; text "Are you sure you want to delete e2e-del-{stamp}?" → click "Yes, continue".
3. Step 2: "This is your final warning. The project and everything in it will be gone forever. Do you want to proceed?" → click "I understand, continue".
4. Step 3: "To confirm, type the project name e2e-del-{stamp} below:"; type `e2e-del` (partial) — observe; type the exact name → click "Delete Project".
5. `GET /api/projects/{D.id}`.
**Expected**:
- The final button is disabled until the typed name matches exactly (case-sensitive); label "Deleting..." while pending.
- `DELETE /api/projects/{id}` → 204; redirect to `/dashboard`; step 5 → 404; project gone from the switcher.
**Notes**: none.

## SET-037 Cancel the delete flow at every step
```yaml
priority: P2
surface: [ui]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/settings
  - i18n:settingsPage
```
**Preconditions**: project `U`.
**Steps**:
1. "Delete Project" → "Cancel" at step 1.
2. Reopen → "Yes, continue" → "Cancel" at step 2.
3. Reopen → advance to step 3, type the name, press "Cancel"; reopen.
**Expected**:
- Each Cancel closes the modal; no `DELETE` request is sent.
- Reopening always starts at step 1 with an empty name box (state reset).
**Notes**: none.

## SET-038 Gate deletion on ProjectsDelete
```yaml
priority: P1
surface: [api, ui]
automation: planned
refs:
  - orkka-api:DELETE /api/projects/{id}
  - orkka-ui:/p/[projectId]/settings
```
**Preconditions**: key `K-mgr` with `[ProjectsManage]` but not `ProjectsDelete`.
**Steps**:
1. `DELETE /api/projects/{U.id}` with `K-mgr`.
2. (Member session without ProjectsDelete, manual) open settings.
**Expected**:
- Step 1 → 403; project still exists.
- Step 2: no "Danger Zone" card at all (`RequirePermission ProjectsDelete`).
**Notes**: none.

## SET-041 Show the settings tab strip on a phone viewport
```yaml
priority: P3
surface: [ui]
automation: planned
refs:
  - orkka-ui:/settings
  - i18n:settingsNav
```
**Preconditions**: viewport 390×844.
**Steps**:
1. Open `/settings/integrations`; scroll the tab strip horizontally.
**Expected**:
- Tabs overflow in a single horizontally scrollable row (no wrapping, no clipped active tab after scroll); "Integrations" underlined; page content fits without horizontal page scroll.
**Notes**: none.

## SET-042 Persist the interface locale from the top bar as a member preference
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-api:PUT /api/org/me/preferences
  - orkka-api:GET /api/org/me
  - orkka-ui:/settings/preferences
  - orkka-ui:packages/ui/src/components/ui/LocaleSwitcher.tsx
```
**Preconditions**: owner signed in on `/settings/preferences`, locale `en`.
**Steps**:
1. In the top bar `select[aria-label="switch language"]` choose `PT`.
2. `GET /api/org/me`.
3. Switch back to `EN`.
**Expected**:
- URL becomes `/pt/settings/preferences`; heading reads "Preferências"; `PUT /api/org/me/preferences {locale:"pt"}` → 200; step 2 `preferences.locale:"pt"`.
- Back to `EN`: URL unprefixed again, `locale:"en"`.
**Notes**: none.

## SET-043 Choose the workspace AI provider on Settings → Workspace
```yaml
priority: P1
surface: [ui]
automation: planned
since: 2026-11-19
refs:
  - orkka-ui:/settings/workspace
  - orkka-ui:apps/local/components/settings/WorkspaceAiProviderCard.tsx
  - orkka-api:GET /api/org/ai-provider
  - orkka-api:PUT /api/org/ai-provider
  - orkka-api:GET /api/me/runner
  - i18n:workspaceSettings
```
**Preconditions**: runner online with Claude and Copilot signed in, Codex installed but signed out, Gemini not installed; two seeded projects in the workspace.
**Steps**:
1. Open `/settings/workspace`; read the "AI provider" card.
2. Pick "Copilot"; click "Save provider"; read the dialog; leave "Also switch the agents in existing projects" checked; click "Save".
3. Open each project's team page.
4. Back on the card pick "Claude"; Save; uncheck "Also switch…"; confirm.
**Expected**:
- 1 → choices Automatic, Claude, Codex, Copilot, Gemini, Grok; Automatic selected; Claude and Copilot "Ready", Codex "Not signed in" with `codex login`, Gemini "Not installed" with `gemini`; "New projects' agents use Claude."; "Save provider" disabled.
- 2 → nothing is written before the dialog's "Save"; while Copilot is picked the card says QA stays on Claude. After it: toast "Saved. 26 agents moved to Copilot across 2 projects. 2 QA agents stayed on Claude for browser testing."
- 3 → every agent shows Copilot Auto except Scout (Claude).
- 4 → toast "Saved. New projects use Claude."; the existing teams keep Copilot Auto.
**Notes**: Owner-only (SET-045). Creating a project afterwards is SET-044.

## SET-044 New projects follow the workspace AI provider (seeding, fallback, isolation)
```yaml
priority: P1
surface: [api]
automation: planned
since: 2026-11-19
refs:
  - orkka-api:PUT /api/org/ai-provider
  - orkka-api:GET /api/org/ai-provider
  - orkka-api:POST /api/projects/from-folder
  - orkka-api:GET /api/projects/{projectId}/agents
```
**Preconditions**: owner (install-secret bearer); a second workspace (WSP-002); runner with Claude and Copilot signed in.
**Steps**:
1. `PUT /api/org/ai-provider {provider:"copilot", applyToExistingProjects:false}`; create a project from a folder; `GET …/agents`.
2. Sign Copilot out on the runner (`copilot logout`), wait for the next Hello; `GET /api/org/ai-provider`; create another project.
3. In the second workspace (`X-Orkka-Workspace`) `GET /api/org/ai-provider`; create a project there.
4. `PUT /api/org/ai-provider {provider:"ollama"}`; `{provider:"nope"}`.
**Expected**:
- 1 → 200 `{provider:"copilot", effectiveProvider:"copilot", blocker:null, applied:false, projectCount:0, …}`; every agent `copilot/auto` except QA on `claude-sonnet-5`.
- 2 → `{provider:"copilot", effectiveProvider:"claude", blocker:"<the runner's reason>"}`; the new project is seeded on Claude (nothing is stranded); the card shows the blocker in amber.
- 3 → the other workspace is still `automatic` and its project is seeded on Claude.
- 4 → 400 each ("not an AI provider choice"); the setting is unchanged.
**Notes**: the runner's last report decides readiness; a runner that never connected leaves the choice trusted. Merge-conflict fixes and utility one-shots (titles, plans) also prefer the chosen provider when it can run.

## SET-045 Keep the workspace AI provider Owner-only
```yaml
priority: P2
surface: [api]
automation: planned
since: 2026-11-19
refs:
  - orkka-api:PUT /api/org/ai-provider
  - orkka-api:GET /api/org/ai-provider
```
**Preconditions**: Owner-minted key with all permissions.
**Steps**:
1. `GET /api/org/ai-provider` with the key.
2. `PUT /api/org/ai-provider {provider:"copilot"}` with the key.
**Expected**:
- 1 → 200 with the current choice.
- 2 → 403 (a key is never Owner); no agent changes.
**Notes**: like the workspace name (SET-004).
