---
area: TCLI
title: Tracker CLI — `./orkka tracker add|list|test|remove`
modes-default: [offline]
---

## TCLI-001 Add a Jira connection: token piped on stdin, tested, stored
```yaml
priority: P0
surface: [cli]
automation: automated
refs:
  - cli:tracker
since: 2026-10-26
```
**Preconditions**: A Jira site the runner can reach (a real Jira Cloud test site; the fake tracker cannot be added through the CLI — see Notes).
**Steps**:
1. `echo <token> | ./orkka tracker add jira --site acme --email dana@acme.com --workspace e2e`.
**Expected**:
- Prints "Checking the token against https://acme.atlassian.net ..." then "Added jira-acme — signed in as <name>." and "The token is stored in <secret store>, for the workspace 'e2e'."; exit 0.
- `~/.orkka/workspaces/e2e/trackers.json` has `{id:"jira-acme", kind:"jira", siteUrl:"https://acme.atlassian.net", email, accountId, displayName, healthy:true}` and NO token.
**Notes**: `--site` is normalised to `https://<host>` (scheme and port dropped), so `http://127.0.0.1:<port>` cannot be added this way; the harness seeds trackers.json + the secret directly instead (see the harness README).

## TCLI-002 Add an Azure DevOps connection with --org
```yaml
priority: P1
surface: [cli]
automation: manual
refs:
  - cli:tracker
since: 2026-10-26
```
**Preconditions**: An Azure DevOps organization and a PAT with Work Items (Read & write) + Project and Team (Read).
**Steps**:
1. `echo <pat> | ./orkka tracker add ado --org https://dev.azure.com/acme/Shop` (a project path is accepted).
**Expected**:
- Stored as `ado-acme` with `siteUrl:"https://dev.azure.com/acme"`; `acme`, `https://acme.visualstudio.com` normalise the same; an on-prem host → "Give your Azure DevOps organization: --org https://dev.azure.com/acme (or just acme)."

## TCLI-003 A token that fails the test is not saved
```yaml
priority: P0
surface: [cli]
automation: planned
refs:
  - cli:tracker
since: 2026-10-26
```
**Preconditions**: A Jira site; a wrong token.
**Steps**:
1. `echo wrong | ./orkka tracker add jira --site acme --email dana@acme.com`.
**Expected**:
- Exit 1; "Jira rejected the email + API token for https://acme.atlassian.net — …" followed by "Nothing was saved."; trackers.json and the secret store unchanged; the token text never appears in the output.

## TCLI-004 Argument errors: kind, site, email, empty token
```yaml
priority: P1
surface: [cli]
automation: planned
refs:
  - cli:tracker
since: 2026-10-26
```
**Preconditions**: None.
**Steps**:
1. `./orkka tracker add`; `… add github`; `… add jira --email a@b.c`; `… add jira --site acme --email nope`; `echo | … add jira --site acme --email a@b.c`.
**Expected**:
- Exit 1 each: "Say which tracker to add: jira or ado."; same; "Give your Jira Cloud site: --site acme.atlassian.net"; "Give the email of your Atlassian account: --email you@acme.com …"; "No token given — nothing was saved."

## TCLI-005 Interactive add prompts without echo and says the token stays local
```yaml
priority: P1
surface: [cli]
automation: manual
refs:
  - cli:tracker
since: 2026-10-26
```
**Preconditions**: A terminal (stdin not redirected).
**Steps**:
1. `./orkka tracker add jira --site acme --email dana@acme.com`; type the token.
**Expected**:
- Prints the token URL and "The token stays on this machine — Orkka's servers never see it."; the prompt "Jira API token: " shows no characters while typing; there is no `--token` flag.

## TCLI-006 Adding the same account again replaces the token, keeps the id
```yaml
priority: P1
surface: [cli]
automation: planned
refs:
  - cli:tracker
since: 2026-10-26
```
**Preconditions**: TCLI-001 done.
**Steps**:
1. Pipe a new valid token into the same `add jira --site acme --email dana@acme.com`.
**Expected**:
- "Updated the token for jira-acme — …"; still one entry; `addedAt` unchanged; a different email on the same site gets `jira-acme-2`.

## TCLI-007 List the connections on this machine
```yaml
priority: P1
surface: [cli]
automation: automated
refs:
  - cli:tracker
since: 2026-10-26
```
**Preconditions**: Zero, then two connections.
**Steps**:
1. `./orkka tracker list` (alias `ls`).
**Expected**:
- Empty: "No tracker connections on this machine yet. Add one with:" + both add commands.
- Two: table `ID  KIND  SITE  ACCOUNT  HEALTH` with `ok` / `failing: <detail>` / `unchecked`; no token column.

## TCLI-008 Test stored tokens now
```yaml
priority: P1
surface: [cli]
automation: automated
refs:
  - cli:tracker
since: 2026-10-26
```
**Preconditions**: One healthy and one revoked connection.
**Steps**:
1. `./orkka tracker test`; `./orkka tracker test jira-acme`; `./orkka tracker test nope`.
**Expected**:
- "jira-acme: ok — signed in as <name>" / "<id>: failing — <detail>"; exit 1 when any fails; the entries' `healthy`/`checkedAt` are updated.
- Unknown id: "No tracker connection 'nope' on this machine — see `./orkka tracker list`."

## TCLI-009 Remove a connection deletes its token too
```yaml
priority: P0
surface: [cli]
automation: automated
refs:
  - cli:tracker
since: 2026-10-26
```
**Preconditions**: TCLI-001 done.
**Steps**:
1. `./orkka tracker remove jira-acme` (aliases `rm`, `delete`).
2. Inspect the OS secret store for `orkka/<workspace>/tracker/jira-acme`.
**Expected**:
- "Removed jira-acme and its token. Projects using it in Orkka stop picking up tickets from it."; gone from trackers.json and from the secret store; the host marks the connection missing on the next Hello (never deletes the row).

## TCLI-010 --workspace resolution
```yaml
priority: P1
surface: [cli]
automation: planned
refs:
  - cli:tracker
  - cli:workspace
since: 2026-10-26
```
**Preconditions**: Machine with workspaces `acme` and `side`.
**Steps**:
1. `./orkka tracker add jira …` without `--workspace`.
2. `… --workspace Bad_Name`; `… --workspace ghost`.
3. `./orkka tracker list` without `--workspace`.
**Expected**:
- "This machine has several workspaces (acme, side) — say which job this is for: --workspace <name>."
- "'bad_name' is not a workspace name — use lowercase letters, digits and hyphens."; "There is no workspace 'ghost' on this machine (it has: acme, side). Create it in Orkka first; a running `./orkka start` adds it to this machine within a minute."
- List prints `[acme]` and `[side]` sections.

## TCLI-011 The token never leaves the machine
```yaml
priority: P0
surface: [cli, hub, api]
automation: automated
refs:
  - cli:tracker
  - orkka-api:GET /api/trackers/connections
since: 2026-10-26
```
**Preconditions**: A connection added with a recognisable token `tok-<stamp>`; runner connected; runner log at debug level.
**Steps**:
1. Grep `trackers.json`, the runner log, `GET /api/trackers/connections`, `GET /api/audit`, and every request the fake tracker server received except its Authorization header.
2. Make the fake tracker answer 400 with the token echoed in its error body; run `tracker test`.
**Expected**:
- `tok-<stamp>` appears only in the OS secret store and in the Basic Authorization header to the tracker.
- The CLI/host error shows `***` where the token was (`TrackerCredential.Scrub`).

## TCLI-012 The runner reports connection metadata in every Hello
```yaml
priority: P1
modes: [tracker]
surface: [hub, api]
automation: planned
refs:
  - orkka-api:GET /api/trackers/connections
since: 2026-10-26
```
**Preconditions**: Runner connected; a connection added with the CLI while it runs.
**Steps**:
1. Wait ≤ 60 s; `GET /api/trackers/connections`.
**Expected**:
- The connection appears with `machineName` = this machine and `healthy`; removing it on the machine marks it missing on the next Hello (not deleted).

## TCLI-013 `./orkka uninstall` deletes nothing; tracker tokens go with `workspace remove`
```yaml
priority: P2
modes: [offline]
surface: [cli]
automation: planned
refs:
  - cli:uninstall
  - cli:workspace
  - cli:tracker
since: 2026-10-26
```
**Preconditions**: A throwaway `ORKKA_HOME` with `ORKKA_SECRET_STORE=file`; two workspaces, each with a connection.
**Steps**:
1. `./orkka uninstall`.
2. `./orkka tracker list`.
3. `./orkka workspace remove side`; `./orkka tracker list`.
**Expected**:
- Step 1: exit 0 with "Nothing to uninstall: Orkka installed nothing. Stop it (`./orkka stop`), then delete" / "this folder and Orkka's data folder (<ORKKA_HOME>). See README.md → Uninstall."; no file under `ORKKA_HOME` and no secret-store entry changes.
- Step 2: both connections are still listed.
- Step 3: `side`'s tracker tokens are deleted from the secret store with its folder; `acme`'s connection remains.
**Notes**: Orkka runs from the clone (R4), so there is no binary to remove; before R4 `uninstall` deleted every workspace's tracker tokens itself. Deleting the data folder by hand leaves secret-store entries behind on a real home — remove workspaces first.

