---
area: AUTO
title: Automation — schedules, playbooks, Autopilot windows, reminders, GitHub triggers
modes-default: [all]
---

Page `/p/{slug}/automation` → `AutomationPage` (906 lines). Cards by testid: `how-it-works-card`, `autonomy-card` (Autonomy `Toggle` → `settings.automationEnabled`, ProjectsManage; amber "Paused — nothing here is firing on its own."), `templates-section` (starter templates, **Add**; only while `TasksExecute` and at least one template is not yet installed), `playbooks-section` (**New playbook**, `playbook-list`), `windows-section` (**New window**, `unpaired-window`, `window-list`), "Agent reminders" (read + **Delete**; only when a Reminder exists), `triggers-card` (hosted only: "New issue → task" toggle + label input, "CI failure on the default branch → task" toggle; ProjectsManage), `guardrails`. `ScheduleRow` (`schedule-row`): name, kind badge, last-run badge, "N consecutive failures", what / when / "next {when}" / "last {when}", **Open conversation**, **Runs**, **Run now**, **Edit**, **Delete**, enable `Toggle` (TasksExecute). `ScheduleEditor` modal "New schedule"/"Edit schedule": Name (≤ 120), "What" `role="radiogroup"` Playbook / Start Autopilot / Stop Autopilot, Playbook → Agent, "Skill (playbook)" (default "No skill — just the message"), "Message to the agent" (≤ 4000); Start Autopilot → "Spend cap (USD)" (default 10), "Run uncapped (no spend ceiling)" (BillingManage); Stop → pair hint; "When": **Recurring** (preset Every hour / Every day / Weekdays / Every week / Custom cron + time / weekday / raw cron, "Time zone" default browser zone, "Cron: {cron} — {human}") or **Once** (`datetime-local`). `RunsDrawer` "Runs — {name}". Server (`ScheduleRules`): 20 schedules/project (409), playbooks ≥ 15 min apart, auto-disable after 5 consecutive failures, name ≤ 120, message ≤ 4000. All schedule writes need `TasksExecute` (uncapped additionally BillingManage); reads open. "Run now" bypasses the Autonomy pause (`trigger == "manual"`).

## AUTO-001 Render the Automation page cards
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/automation
  - orkka-api:GET /api/projects/{projectId}/schedules
  - i18n:automationPage
```
**Preconditions**: fresh project; Owner session. No GitHub triggers card (removed with the GitHub App).
**Steps**:
1. Open `/p/{slug}/automation`.
**Expected**:
- Sidebar "Automation" highlighted; heading "Automation" + subtitle "Standing orders for this project — work that happens while nobody is watching."
- `how-it-works-card` titled "How automation works" with steps "A schedule fires", "An agent takes a turn", "You get something back" and the boundary line "Playbook agents read your code, they never write it…".
- `autonomy-card` "Autonomy" with the toggle on; `templates-section` "Starter automations" listing "Groom the backlog weekly", "Friday release notes", "Weekly security sweep", "Dependency check", "Autopilot night shift" each with a kind badge, cron phrasing (e.g. "Weekly on Monday at 09:00") and **Add**.
- `playbooks-section` "Playbooks" with **New playbook** and "No playbooks yet."; `windows-section` "Autopilot windows" with **New window** and "No Autopilot windows yet."; `triggers-card` "GitHub triggers"; `guardrails` text "Limits: up to 20 schedules per project · a run is skipped when the agent is still busy or the org budget is spent · 5 failures in a row disable a schedule."
- `GET /api/projects/{p}/schedules` → 200 `[]`.

## AUTO-002 Pause and resume automation with the Autonomy toggle
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:PATCH /api/projects/{id}
  - orkka-api:GET /api/projects/{id}
  - orkka-ui:/p/[projectId]/board
  - i18n:automationPage
  - i18n:autopilotToggle
```
**Preconditions**: AUTO-001; one enabled schedule (AUTO-008).
**Steps**:
1. Click the Autonomy toggle off.
2. `GET /api/projects/{id}`.
3. Open `/p/{slug}/board`.
4. Return and click the toggle on.
**Expected**:
- `PATCH /api/projects/{id} {settings:{…, automationEnabled:false}}` → 200; amber line "Paused — nothing here is firing on its own." appears; schedule rows dim and hide their "next …" text.
- GET → `settings.automationEnabled: false`.
- Board Autopilot control shows the paused badge `autopilot-automation-paused` with title "Automation is paused for this project — scheduled Autopilot windows won't fire (manual start still works)."
- Toggle on → `automationEnabled:true`; paused line and badge disappear. A failed PATCH reverts the toggle and toasts "Couldn't save."

## AUTO-003 Disable the Autonomy toggle without ProjectsManage
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-api:PATCH /api/projects/{id}
  - i18n:automationPage
```
**Preconditions**: member session and API key without `ProjectsManage`.
**Steps**:
1. Open `/p/{slug}/automation` as the member.
2. `PATCH /api/projects/{id} {settings:{automationEnabled:false}}` with the key.
**Expected**:
- The Autonomy toggle is rendered disabled; GitHub trigger toggles and label input are disabled too.
- PATCH → 403.

## AUTO-004 Install a starter template as a disabled playbook
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/schedules
  - orkka-api:DELETE /api/projects/{projectId}/schedules/{scheduleId}
  - orkka-ui:packages/ui/src/components/automation/templates.ts
  - i18n:automationPage
```
**Preconditions**: AUTO-001; project has a PM agent and the built-in `groom-backlog` skill enabled.
**Steps**:
1. On "Groom the backlog weekly" click **Add**.
2. Delete the created row (AUTO-024).
**Expected**:
- `POST /api/projects/{p}/schedules {name:"Weekly backlog grooming", kind:"Playbook", cronExpression:"0 9 * * 1", timeZoneId:<browser zone>, agentId:<PM id>, skillId:<groom-backlog id>, message:"Groom the Drafting lane: …", isEnabled:false}` → 201.
- Toast "Added — switch it on when you are ready."; a `schedule-row` "Weekly backlog grooming" appears in `playbook-list` with its toggle **off**, "Playbook" badge, what-line `Luna · /groom-backlog · “Groom the Drafting lane…”`, when-line "Weekly on Monday at 09:00 · {tz}".
- The template card disappears from `templates-section`; after deleting the row it reappears.
**Notes**: templates match by schedule name (case-insensitive); a disabled/renamed skill installs message-only.

## AUTO-005 Install the Autopilot night-shift pair from a template
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/schedules
  - i18n:automationPage
```
**Preconditions**: AUTO-001.
**Steps**:
1. On "Autopilot night shift" click **Add**.
**Expected**:
- Two sequential POSTs: `{name:"Night shift — start", kind:"AutopilotStart", cronExpression:"0 22 * * *", autopilotCapUsd:20, isEnabled:false}` then `{name:"Night shift — stop", kind:"AutopilotStop", cronExpression:"0 6 * * *", isEnabled:false}` → 201 each.
- `window-list` shows both rows: "Start Autopilot with a $20 cap · Daily at 22:00 · {tz}" and "Stop the running Autopilot session · Daily at 06:00 · {tz}"; both toggles off; no `unpaired-window` warning (nothing enabled).
- The `templates-section` disappears entirely once every template has a row.

## AUTO-006 Open the playbook editor and check its controls
```yaml
priority: P1
surface: [ui]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/automation
  - i18n:automationPage
```
**Preconditions**: AUTO-001; ≥ 1 agent and ≥ 1 enabled skill.
**Steps**:
1. Click **New playbook**.
2. Click each option in the "What" radiogroup and observe the form.
3. Click **Cancel**.
**Expected**:
- Modal "New schedule": "Name" input (`maxlength=120`, autofocus); radiogroup `aria-label="What"` with radios "Playbook" (checked), "Start Autopilot", "Stop Autopilot" each with a description line.
- Playbook: "Agent" select (first agent preselected), "Skill (playbook)" select with "No skill — just the message" first and enabled skills as "{name} (/{slug})", hint "Runs like typing /slug in chat…", "Message to the agent" textarea (`maxlength=4000`, placeholder "e.g. Review what shipped this week and draft release notes."), hint "What the agent should do this time…".
- Start Autopilot: "Spend cap (USD)" number input value `10`, hint "Autopilot stops submitting new work once the session has spent this much."; "Run uncapped (no spend ceiling)" checkbox for BillingManage holders.
- Stop Autopilot: text "Pair this with a start so Autopilot runs for a fixed stretch instead of until the money runs out."
- "When": **Recurring** selected, preset select "Every day", time input `09:00`, "Time zone" prefilled with the browser zone, preview "Cron: 0 9 * * * — Daily at 09:00", hint "Recurring playbooks fire at most every 15 minutes." (Playbook only).
- **Save** is disabled until Name is filled and (skill or message) is provided; **Cancel** closes without a request.

## AUTO-007 Create a recurring playbook
```yaml
priority: P0
surface: [ui, api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/schedules
  - orkka-api:GET /api/projects/{projectId}/schedules
  - i18n:automationPage
```
**Preconditions**: AUTO-006.
**Steps**:
1. **New playbook** → Name `E2E daily digest`; Agent `Luna`; Skill "No skill — just the message"; Message `Summarise the board in two lines.`; When: Recurring, "Every day", time `09:00`, Time zone `UTC`.
2. Click **Save**.
3. `GET /api/projects/{p}/schedules`.
**Expected**:
- `POST …/schedules {name:"E2E daily digest", kind:"Playbook", cronExpression:"0 9 * * *", timeZoneId:"UTC", fireAt:null, agentId:<Luna>, skillId:null, message:"Summarise the board in two lines.", autopilotCapUsd:null, uncapped:false, isEnabled:true}` → 201 with `nextRunAt` = next 09:00 UTC.
- Row in `playbook-list`: "E2E daily digest", badge "Playbook", `Luna · “Summarise the board in two lines.”`, `Daily at 09:00 · UTC · next in {n}h`, toggle on.
- GET lists the schedule with `isEnabled: true`, `consecutiveFailures: 0`, `lastRunAt: null`.
**Notes**: cleanup deletes the schedule (AUTO-024) or the project.

## AUTO-008 Validate name, message and playbook requirements on the API
```yaml
priority: P2
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/schedules
```
**Preconditions**: Owner API key; agent id `A`, foreign agent id `F` (another project).
**Steps**:
1. `POST …/schedules {name:"", kind:"Playbook", cronExpression:"0 9 * * *", agentId:A, message:"x"}`.
2. Same with `name:<121 chars>`.
3. `{name:"x", kind:"Reminder", …}`.
4. `{name:"x", kind:"Playbook", cronExpression:"0 9 * * *", agentId:A}` (no skill, no message).
5. `{…, agentId:F, message:"x"}`.
6. `{…, agentId:A, message:<4001 chars>}`.
**Expected**:
- 400 validation problems with field errors: `Name` "Name is required (max 120 characters)." (1, 2); `Kind` "Kind must be Playbook, AutopilotStart or AutopilotStop." (3); `Message` "A playbook needs a skill, a message, or both." (4); `AgentId` "The agent doesn't belong to this project." (5); `Message` "Message must be at most 4000 characters." (6).

## AUTO-009 Reject a playbook cron that fires more often than every 15 minutes
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/schedules
  - i18n:automationPage
```
**Preconditions**: AUTO-006.
**Steps**:
1. **New playbook** → Name `Too often`; Message `x`; When: Recurring, preset "Custom cron", raw `*/5 * * * *`; **Save**.
2. Same request via API.
**Expected**:
- Preview reads "Cron: */5 * * * * — */5 * * * *" (unphrased custom cron).
- Save → 400 surfaced inline in the modal: "Playbooks may run at most every 15 minutes."; the modal stays open.
- API → 400 with field `CronExpression` and the same message. An AutopilotStart with the same cron is accepted (the floor applies to playbooks only).

## AUTO-010 Create a capped Autopilot start window
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/schedules
  - i18n:automationPage
```
**Preconditions**: AUTO-001.
**Steps**:
1. **New window** → Name `E2E start`; "Start Autopilot" is preselected; set "Spend cap (USD)" to `5`; When: Recurring, "Weekdays", time `22:00`; **Save**.
**Expected**:
- `POST …/schedules {name:"E2E start", kind:"AutopilotStart", cronExpression:"0 22 * * 1-5", timeZoneId, autopilotCapUsd:5, uncapped:false, isEnabled:true}` → 201.
- Row in `window-list`: badge "Start Autopilot", what "Start Autopilot with a $5 cap", when "Weekdays at 22:00 · {tz}".
- `unpaired-window` warning appears: "This start has no matching stop. Autopilot keeps going until its cap or your org budget stops it."

## AUTO-011 Schedule an uncapped Autopilot start (BillingManage only)
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/schedules
  - i18n:automationPage
```
**Preconditions**: Owner session; API key `K1` with BillingManage and `K2` with TasksExecute but without BillingManage.
**Steps**:
1. **New window** → Name `E2E uncapped`; tick "Run uncapped (no spend ceiling)"; **Save**.
2. `POST …/schedules {name:"x", kind:"AutopilotStart", cronExpression:"0 23 * * *", uncapped:true}` with `K2`.
3. `POST …/schedules {name:"y", kind:"AutopilotStart", cronExpression:"0 23 * * *"}` (no cap, not uncapped) with `K1`.
4. `POST …/schedules {name:"z", kind:"AutopilotStart", cronExpression:"0 23 * * *", autopilotCapUsd:0}` with `K1`.
**Expected**:
- Ticking the box disables the cap input and shows the amber hint "No ceiling on this session — only your org budget will stop it."; POST `{autopilotCapUsd:null, uncapped:true}` → 201; row what-line "Start Autopilot (uncapped)".
- `K2` → 400 field `Uncapped` "Only a member with BillingManage can schedule an uncapped autopilot session."
- Step 3 → 400 field `AutopilotCapUsd` "Set a spend cap, or explicitly opt into an uncapped session."; step 4 → 400 "The cap must be positive."
- A member without BillingManage does not see the "Run uncapped" checkbox at all.

## AUTO-012 Create the matching Autopilot stop and clear the unpaired warning
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/schedules
  - orkka-api:PATCH /api/projects/{projectId}/schedules/{scheduleId}/enabled
  - i18n:automationPage
```
**Preconditions**: AUTO-010 (enabled start, no stop).
**Steps**:
1. **New window** → select "Stop Autopilot"; Name `E2E stop`; When: Recurring, "Weekdays", `06:00`; **Save**.
2. Toggle the stop row off; toggle it on again.
**Expected**:
- POST `{kind:"AutopilotStop", cronExpression:"0 6 * * 1-5", …}` → 201; row what-line "Stop the running Autopilot session".
- `unpaired-window` disappears while both an enabled start and an enabled stop exist; it comes back the moment the stop is disabled (`PATCH …/enabled {enabled:false}`) and goes away when re-enabled.

## AUTO-013 Compose recurring presets into cron with a live preview
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:packages/ui/src/components/automation/cron.ts
  - orkka-api:POST /api/projects/{projectId}/schedules
  - i18n:automationPage
```
**Preconditions**: AUTO-006.
**Steps**:
1. In the editor set preset "Every hour".
2. Set "Weekdays", time `18:30`.
3. Set "Every week", weekday "Monday", time `09:00`.
4. Set "Custom cron", raw `0 9 * * 1-5`.
5. Change "Time zone" to `America/Sao_Paulo`; fill Name/Message; **Save**.
**Expected**:
- Preview lines: "Cron: 0 * * * * — Hourly"; "Cron: 30 18 * * 1-5 — Weekdays at 18:30"; "Cron: 0 9 * * 1 — Weekly on Monday at 09:00"; "Cron: 0 9 * * 1-5 — Weekdays at 09:00" (the custom string is recognised as the weekdays shape).
- The hourly preset hides the time input; weekly shows a weekday select (Sunday…Saturday).
- POST carries `timeZoneId:"America/Sao_Paulo"`; the row's when-line ends `· America/Sao_Paulo`; reopening **Edit** re-derives the preset from the saved cron.

## AUTO-014 Reject an invalid cron expression or time zone
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/schedules
  - i18n:automationPage
```
**Preconditions**: AUTO-006.
**Steps**:
1. Editor → Custom cron `every monday`; fill Name/Message; **Save**.
2. Editor → valid cron, Time zone `Mars/Olympus`; **Save**.
3. API: `{…, cronExpression:"0 9 * * *", fireAt:"2030-01-01T00:00:00Z"}`; and `{…}` with neither.
**Expected**:
- 400 inline "Invalid cron expression: …" (field `CronExpression`).
- 400 inline `Unknown time zone "Mars/Olympus".` (field `TimeZoneId`).
- Both-or-neither → 400 "Provide either a cron expression (recurring) or a fireAt time (one-shot)."

## AUTO-015 Create a one-shot schedule with a future time
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/schedules
  - i18n:automationPage
```
**Preconditions**: AUTO-006.
**Steps**:
1. **New playbook** → Name `E2E once`; Message `x`; When: click **Once**; pick tomorrow 10:00 in the `datetime-local` input; **Save**.
2. API: same body with `fireAt` = now − 1 minute.
**Expected**:
- POST `{cronExpression:null, timeZoneId:null, fireAt:"<ISO UTC>", …}` → 201 with `nextRunAt` = `fireAt`.
- Row when-line "Once at {local date time}".
- Past `fireAt` → 400 field `FireAt` "fireAt must be in the future." (a 30 s grace applies).

## AUTO-016 Show every schedule-row field and action
```yaml
priority: P1
surface: [ui]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/automation
  - i18n:automationPage
```
**Preconditions**: AUTO-007 playbook after one manual run (AUTO-017) and AUTO-010 start window.
**Steps**:
1. Inspect the `schedule-row` of "E2E daily digest".
**Expected**:
- Name, "Playbook" badge, last-run badge ("Succeeded"/"Failed"/"Skipped"/"Running"), "{n} consecutive failures" only when `consecutiveFailures > 0`.
- What-line (agent · /skill · “message” truncated at 60 chars); when-line "Daily at 09:00 · UTC · next in …h · last {relative}".
- Buttons in order: **Open conversation** (link to `/p/{slug}/team?agent={agentId}&tab=chat&c={conversationId}`, present after the first run), **Runs**, **Run now**, **Edit**, **Delete**, enable toggle.
- A disabled row (or all rows while Autonomy is paused) renders at reduced opacity and without "next …".

## AUTO-017 Run a playbook now
```yaml
priority: P1
modes: [hybrid-repo]
surface: [ui, api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/schedules/{scheduleId}/run
  - orkka-api:GET /api/projects/{projectId}/schedules
  - orkka-api:GET /api/projects/{projectId}/schedules/{scheduleId}/runs
  - i18n:automationPage
```
**Preconditions**: AUTO-007 playbook; runner online with `chat-tools`; Autonomy **off** (to prove manual runs bypass the pause).
**Steps**:
1. Click **Run now** on "E2E daily digest"; wait for the button to stop spinning.
2. `GET …/schedules/{id}/runs`.
3. Click **Open conversation**.
**Expected**:
- `POST …/schedules/{id}/run` → 200 `{status:"Succeeded"|"Skipped"|"Failed", trigger:"manual", summary|error, conversationId}`; toast "Ran successfully." (or "Skipped: {reason}" / "Failed: {error}").
- Rows refresh via `GET …/schedules`: last-run badge and "last just now"; runs list has one entry with `trigger: "manual"`.
- The conversation link opens Luna's chat with the playbook turn (`?c=`).
**Notes**: with the runner offline the run finishes Skipped/Failed with the reason in `summary`/`error` — still a valid assertion path.

## AUTO-018 Refuse Run now while the schedule is already running
```yaml
priority: P3
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/schedules/{scheduleId}/run
```
**Preconditions**: AUTO-007 playbook; runner online.
**Steps**:
1. Fire `POST …/schedules/{id}/run` twice concurrently (second within the lease of the first).
**Expected**:
- One → 200; the other → 409 "This schedule is running right now."
- A run against an unknown schedule id → 404.

## AUTO-019 Inspect runs in the Runs drawer
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/schedules/{scheduleId}/runs
  - i18n:automationPage
```
**Preconditions**: AUTO-017 (≥ 1 run); a never-run schedule.
**Steps**:
1. Click **Runs** on "E2E daily digest".
2. Close; click **Runs** on the never-run schedule.
**Expected**:
- Modal "Runs — E2E daily digest" lists runs newest first: status badge (Succeeded/Failed/Skipped/Running), started timestamp, `· manual` or `· cron` trigger, summary text (or red error), and **Open conversation** when a `conversationId` exists (→ `/team?agent=…&tab=chat&c=…`).
- Never-run → "No runs yet."; a failed fetch shows "Couldn't save." (shared error string).
- API returns at most 50 runs.

## AUTO-020 Enable and disable a schedule with the row toggle
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:PATCH /api/projects/{projectId}/schedules/{scheduleId}/enabled
  - orkka-api:GET /api/projects/{projectId}/schedules/{scheduleId}
  - i18n:automationPage
```
**Preconditions**: AUTO-007.
**Steps**:
1. Click the toggle on "E2E daily digest" (off).
2. `GET …/schedules/{id}`.
3. Click it again (on).
**Expected**:
- `PATCH …/schedules/{id}/enabled {enabled:false}` → 200 with `isEnabled:false`, `nextRunAt:null`; row dims and drops "next …".
- GET agrees; toggle on → `{enabled:true}` → `isEnabled:true`, `nextRunAt` recomputed.
- A failed PATCH reverts the toggle and toasts "Couldn't save."

## AUTO-021 Edit a schedule
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:PUT /api/projects/{projectId}/schedules/{scheduleId}
  - i18n:automationPage
```
**Preconditions**: AUTO-007.
**Steps**:
1. Click **Edit** → modal "Edit schedule" prefilled (Name, kind, agent, message, preset "Every day", `09:00`, `UTC`).
2. Change time to `10:15`, Name to `E2E daily digest 2`; **Save**.
3. API: `PUT …/schedules/{reminderId} {…}` on a Reminder-kind schedule (from AUTO-025), if present.
**Expected**:
- `PUT …/schedules/{id}` with `cronExpression:"15 10 * * *"` and the new name, `isEnabled` preserved → 200; row shows "Daily at 10:15 · UTC".
- Editing a Reminder → 409 "Reminders are managed by the agent that set them — delete it instead."

## AUTO-022 Delete a schedule after confirming
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:DELETE /api/projects/{projectId}/schedules/{scheduleId}
  - orkka-api:GET /api/projects/{projectId}/schedules/{scheduleId}
  - i18n:automationPage
```
**Preconditions**: AUTO-015 one-shot schedule.
**Steps**:
1. Click **Delete** → dialog "Delete schedule?" with `"E2E once" will stop firing. Past runs stay in the conversation.`; **Cancel**.
2. Repeat; **Delete**.
3. `GET …/schedules/{id}`.
**Expected**:
- Cancel: no request. Delete → `DELETE …/schedules/{id}` → 204; row gone; GET → 404.

## AUTO-023 Enforce the guardrails: 20 per project and auto-disable after 5 failures
```yaml
priority: P2
surface: [api, ui]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/schedules
  - orkka-api:src/Modules/Orkka.Modules.Agents/Features/Schedules/ScheduleRules.cs
  - i18n:automationPage
```
**Preconditions**: fresh project; Owner API key.
**Steps**:
1. Create 20 disabled AutopilotStop schedules via `POST …/schedules` (distinct names, cron `0 6 * * *`).
2. `POST` a 21st.
3. Open the page.
**Expected**:
- 20 × 201; the 21st → 409 "A project can have at most 20 schedules."
- `guardrails` paragraph states the 20 / 15-minute / 5-failure limits; `window-list` has 20 rows.
**Notes**: the 5-consecutive-failure auto-disable (`ScheduleRules.AutoDisableAfterFailures`) is processor behaviour — observe "5 consecutive failures" + toggle off on a playbook whose agent is offline after five cron ticks (long-running, `manual`).

## AUTO-024 Show and delete agent reminders
```yaml
priority: P3
modes: [hybrid-repo]
surface: [ui, api]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/schedules
  - orkka-api:DELETE /api/projects/{projectId}/schedules/{scheduleId}
  - orkka-api:POST /api/projects/{projectId}/agents/{agentId}/conversations/{conversationId}/messages
  - i18n:automationPage
```
**Preconditions**: runner online.
**Steps**:
1. In Luna's chat send "Use your set_reminder tool to remind yourself in 2 hours to check the board; reply 'set' when done."
2. Open `/p/{slug}/automation`.
3. Click **Delete** on the reminder; confirm.
**Expected**:
- `GET …/schedules` contains a `kind:"Reminder"` row; the "Agent reminders" section appears with "Luna: {message}" and "Fires {local time}", plus **Delete**.
- Delete → 204; section disappears when no reminders remain. Reminders are never listed in `playbook-list`/`window-list`.

## AUTO-027 Hide schedule controls and return 403 without TasksExecute
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/schedules
  - orkka-api:PUT /api/projects/{projectId}/schedules/{scheduleId}
  - orkka-api:PATCH /api/projects/{projectId}/schedules/{scheduleId}/enabled
  - orkka-api:DELETE /api/projects/{projectId}/schedules/{scheduleId}
  - orkka-api:POST /api/projects/{projectId}/schedules/{scheduleId}/run
  - orkka-api:GET /api/projects/{projectId}/schedules
  - orkka-api:GET /api/projects/{projectId}/schedules/{scheduleId}/runs
```
**Preconditions**: API key minted without `TasksExecute`; member session without it; one schedule.
**Steps**:
1. With the key call every write in refs (valid bodies) and both GETs.
2. As the member open `/p/{slug}/automation`.
**Expected**:
- Writes → 403; GETs → 200.
- UI: no `templates-section`, no **New playbook** / **New window**, rows show only **Open conversation** and **Runs** (no **Run now**, **Edit**, **Delete**, toggle); reminders show no **Delete**.

## AUTO-028 List schedules through MCP
```yaml
priority: P2
surface: [api]
automation: planned
refs:
  - orkka-api:POST /mcp
  - orkka-api:src/Modules/Orkka.Modules.Agents/Features/Mcp/OrkkaMcpToolServer.cs
```
**Preconditions**: Owner API key; AUTO-007 and AUTO-010 schedules exist.
**Steps**:
1. `POST http://127.0.0.1:<port>/mcp` `tools/list` with `Authorization: Bearer orkka_key_…`.
2. `tools/call list_schedules {project:"{slug}"}`.
**Expected**:
- `list_schedules` is present in the tool list (description "Schedules on the project's Automation page (playbooks, autopilot windows, reminders).").
- The call returns the same schedules as `GET …/schedules` (names, kinds, enabled flags, next run).
