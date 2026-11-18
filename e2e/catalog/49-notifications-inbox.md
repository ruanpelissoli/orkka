---
area: NTF
title: Notifications and the attention inbox
modes-default: [all]
---

Cross-project "what needs me" surfaces: the Inbox (`/inbox`, `GET /api/inbox`, ranked queue of waiting tasks grouped by kind), the notification feed (`/notifications`, bell in the top bar, `GET /api/notifications`, live via `TaskHub.NotificationCreated`), per-device desktop toasts, and mobile push devices (`/api/me/devices`).

## NTF-001 Render the inbox page and its empty state
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:GET /api/inbox
  - orkka-ui:/inbox
  - orkka-ui:packages/ui/src/components/inbox/InboxPageClient.tsx
  - i18n:inbox
```
**Preconditions**: signed in; no task is waiting on a human.
**Steps**:
1. Open `/inbox`.
**Expected**:
- Heading "Inbox", subtitle "Everything waiting on you, most urgent first"; pill switch "Everyone" (active) / "Mine".
- `GET /api/inbox?scope=all` → 200 `{items:[], counts:[], total:0}`; body "Nothing needs you right now" / "When an agent needs a decision, it shows up here.".
- Sidebar "Inbox" highlighted.
**Notes**: none.

## NTF-002 List waiting items grouped by kind with scope and kind filters
```yaml
priority: P0
modes: [hybrid-repo]
surface: [ui, api]
automation: planned
costUsd: 0
refs:
  - orkka-api:GET /api/inbox
  - orkka-api:POST /api/projects/{projectId}/tasks
  - orkka-api:POST /api/tasks/{id}/submit
  - orkka-ui:/inbox
  - i18n:inbox
```
**Preconditions**: project with the fixture repo; `autoApprovePlan` off; a task submitted and now `WaitingPlanApproval` (or `WaitingAnswer`).
**Steps**:
1. Open `/inbox`.
2. Click the "Approve plan" kind chip; click it again; click "Mine".
3. `GET /api/inbox?scope=mine&kind=plan_approval`.
**Expected**:
- Subtitle gains "· 1 item"; chip row "All" + one chip per kind with its count.
- Row: kind badge ("Approve plan" / "Answer question"), task code, project name (and repo), title, agent preview, "waiting {time}", optional "N tasks blocked".
- Chip click filters to that kind (others dimmed), second click clears; "Mine" limits to tasks the caller created (owner created it → still listed).
- Step 3 → the same item.
**Notes**: none.

## NTF-003 Open an inbox item on the screen that resolves it
```yaml
priority: P1
modes: [hybrid-repo]
surface: [ui]
automation: planned
refs:
  - orkka-ui:/inbox
  - orkka-ui:packages/ui/src/components/inbox/inbox-routes.ts
  - orkka-ui:/p/[projectId]/tasks/[taskId]
```
**Preconditions**: NTF-002 item present.
**Steps**:
1. Press `j`/`ArrowDown` then `Enter` on `/inbox`.
2. Go back; click the row with the mouse.
3. Press `r`.
**Expected**:
- `plan_approval`/`question` → `/p/{slug}/tasks/{number}`; `code_review` → `…/review`; `merge_conflict`/`merge_failed` → `…/merge` (routing table).
- Keyboard: cursor row highlighted (`aria-current="true"`), Enter opens; `r` refetches; hint "↑ ↓ to move · Enter to open · R to refresh" under the list.
**Notes**: none.

## NTF-004 Render the notifications page and mark items read
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:GET /api/notifications
  - orkka-api:POST /api/notifications/{id}/read
  - orkka-api:POST /api/notifications/read-all
  - orkka-ui:/notifications
  - i18n:notifications
```
**Preconditions**: at least two unread notifications (from NTF-006 or an earlier suite).
**Steps**:
1. Open `/notifications`.
2. Click the first row.
3. Click "Mark all as read".
**Expected**:
- Heading "Notifications"; rows: coloured unread dot, type label (e.g. "Plan ready for your review"), task title — body, relative time; empty state "You're all caught up" / "Notifications appear here when your tasks need attention.".
- Step 2: `POST /api/notifications/{id}/read` → 200; row dims (read style); navigates to `/p/{slug}/tasks/{n}` when it has a task.
- Step 3: `POST /api/notifications/read-all` → 200; "Mark all as read" link disappears; `GET /api/notifications` → `unreadCount:0`.
**Notes**: none.

## NTF-005 Show the bell panel with recent items and shortcuts
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:GET /api/notifications
  - orkka-ui:packages/ui/src/components/notifications/NotificationBell.tsx
  - i18n:notifications
```
**Preconditions**: ≥ 1 notification.
**Steps**:
1. Click the bell (`aria-label="Notifications"`).
2. Press Escape; click the bell; click outside.
3. Reopen; click "View all".
**Expected**:
- Panel header "Notifications" + "Mark all as read" (only when unread > 0); at most 10 rows; footer with the compact "Desktop notifications" toggle and "View all".
- Escape and outside-click close it; "View all" → `/notifications` and closes.
- Badge on the bell shows the unread count (`99+` cap) and is absent at zero.
**Notes**: none.

## NTF-006 Update the bell badge live when a notification is created
```yaml
priority: P1
modes: [hybrid-repo]
surface: [ui, hub, api]
automation: planned
costUsd: 0
refs:
  - hub:TaskHub.NotificationCreated
  - orkka-api:POST /api/tasks/{id}/submit
  - orkka-api:GET /api/notifications
```
**Preconditions**: all notifications read; a Drafting task on the fixture project; `autoApprovePlan` off.
**Steps**:
1. Keep `/p/{slug}/board` open; `POST /api/tasks/{id}/submit`.
2. Wait for the PM/dev run to reach a human gate.
**Expected**:
- Without reloading, the bell badge appears with `1` (spring pop animation) as `NotificationCreated` arrives on `user:{clerkUserId}`.
- `GET /api/notifications` → `unreadCount:1`, type `PlanApprovalRequired` (or `QuestionAsked`), `projectSlug`, `taskNumber` set.
**Notes**: none.

## NTF-007 Toggle desktop notifications from the bell and receive an OS toast
```yaml
priority: P2
surface: [ui]
automation: planned
refs:
  - orkka-ui:packages/ui/src/components/notifications/DesktopNotificationsToggle.tsx
  - orkka-ui:/settings/notifications
  - i18n:notifications
```
**Preconditions**: Playwright context with `notifications` permission granted; a way to trigger a notification (NTF-006) or a mocked hub event.
**Steps**:
1. Open the bell panel; switch "Desktop notifications" on.
2. Trigger a notification.
**Expected**:
- Toggle persists (localStorage) and mirrors the one on `/settings/notifications`.
- `showDesktopNotification` fires a `Notification` whose title is the type label ("Plan ready for your review"); clicking it routes to the task.
**Notes**: assert via a stubbed `window.Notification` in the page context.

## NTF-009 Create a notification for each human gate and route it on click
```yaml
priority: P1
modes: [hybrid-repo]
surface: [ui, api]
automation: planned
costUsd: 0
refs:
  - orkka-api:GET /api/notifications
  - orkka-api:POST /api/tasks/{taskId}/plans/{planId}/review
  - orkka-api:POST /api/tasks/{taskId}/messages
  - orkka-ui:/notifications
  - i18n:notifications
```
**Preconditions**: fixture project; a task driven through the pipeline by the task-lifecycle suite.
**Steps**:
1. After each gate, `GET /api/notifications` and click the newest row on `/notifications`.
**Expected**:
- `PlanApprovalRequired` ("Plan ready for your review") → `/p/{slug}/tasks/{n}`.
- `QuestionAsked` ("An agent has questions for you") → task page.
- `CodeReviewRequired` ("Code ready for your review") → task page (review reachable from there).
- `TaskMergeFailed` ("Merge failed") → task page.
- `TaskCompleted` ("Task completed") on merge.
- `ScheduleFailed` → `/p/{slug}/automation`; `WebhookDisabled` → `/p/{slug}/settings`; `AgentReplied` → `/p/{slug}/team?agent={id}` (bell only).
- Rows without `projectSlug`/`taskNumber`/`agentId` are not clickable.
**Notes**: none.

## NTF-010 Clamp the notifications list limit and report the unread count
```yaml
priority: P2
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/notifications
```
**Preconditions**: several notifications.
**Steps**:
1. `GET /api/notifications?limit=1`, `?limit=0`, `?limit=100000`.
**Expected**:
- 200 each; `limit=1` returns one item; out-of-range values are clamped (never 400); `unreadCount` counts ALL unread regardless of `limit`; newest first.
**Notes**: none.

## NTF-011 Refuse reading another member's notification
```yaml
priority: P2
surface: [api]
automation: manual
refs:
  - orkka-api:POST /api/notifications/{id}/read
```
**Preconditions**: secondary Member with a notification id `X`; owner token.
**Steps**:
1. As owner: `POST /api/notifications/{X}/read`.
2. `POST /api/notifications/not-a-ulid/read`.
**Expected**:
- Step 1 → 404 (notifications are per recipient); step 2 → 404 (route constraint).
**Notes**: none.

## NTF-012 Scope the inbox to a single project
```yaml
priority: P2
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/inbox
```
**Preconditions**: two projects, one with a waiting item (NTF-002).
**Steps**:
1. `GET /api/inbox?projectId={withItem}` and `?projectId={other}`.
2. `GET /api/inbox?projectId=01ARZ3NDEKTSV4RRFFQ69G5FAV`.
**Expected**:
- First → the item; second → empty; step 2 → 200 empty (unknown project simply matches nothing) or 404 — never 500.
**Notes**: none.
