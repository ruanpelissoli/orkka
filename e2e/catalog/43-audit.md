---
area: AUD
title: Audit log
modes-default: [all]
---

Org-level, hash-chained audit trail at `/settings/audit` (`AuditLogPage`, `AuditView` permission). Rows come from `GET /api/audit` (cursor paged, newest first), `POST /api/audit/verify` walks the SHA-256 chain, `GET /api/audit/export` downloads CSV/JSON and is itself audited. Management endpoints across modules emit the events asserted below.

## AUD-010 Record ApiKeyCreated and ApiKeyRevoked
```yaml
priority: P1
surface: [api, ui]
automation: planned
refs:
  - orkka-api:POST /api/me/api-keys
  - orkka-api:DELETE /api/me/api-keys/{keyId}
  - orkka-api:GET /api/audit
```
**Preconditions**: owner session.
**Steps**:
1. Mint key `e2e-audit-{stamp}`; revoke it.
2. `GET /api/audit?eventType=ApiKeyCreated,ApiKeyRevoked&limit=5`.
**Expected**:
- Two rows, newest first `ApiKeyRevoked` then `ApiKeyCreated`, `actorKind:"User"`, summaries naming the key; metadata carries the key id/prefix, never the raw key.
**Notes**: none.

## AUD-012 Record settings changes and ticket-source changes
```yaml
priority: P2
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/audit
  - orkka-api:PATCH /api/projects/{id}
  - orkka-api:PUT /api/projects/{projectId}/tracker
since: 2026-09-15
```
**Preconditions**: An `e2e-*` project with one repo; a tracker connection of the caller.
**Steps**:
1. `PATCH /api/projects/{P} {settings:{…, autoApprovePlan:true}}`.
2. `PUT /api/projects/{P}/tracker {…}`.
3. `GET /api/audit?projectId={P}`.
**Expected**:
- One `ProjectSettingsChanged` row and one `TrackerConfigChanged` row ("Ticket source set: jira <site> / <scope>, 1 epic(s)"); no token or secret appears anywhere in the audit response.

## AUD-013 Record MCP tool calls with ActorKind ApiKey
```yaml
priority: P1
surface: [api, ui]
automation: planned
refs:
  - orkka-api:POST /mcp
  - orkka-api:GET /api/audit
```
**Preconditions**: KEY-016 executed with `K-full`.
**Steps**:
1. `GET /api/audit?eventType=McpToolCall&limit=5`.
2. UI: filter Type = "Access (API keys, MCP, runners)".
**Expected**:
- Rows `eventType:"McpToolCall"`, `actorKind:"ApiKey"`, `tool:"create_tasks"` (and `list_tasks`/`get_task`), project id set when bound; UI actor badge reads "ApiKey" (warning colour).
**Notes**: none.

