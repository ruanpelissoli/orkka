---
area: SKL
title: Skills — project skill library (SKILL.md playbooks)
modes-default: [all]
---

Page `/p/{slug}/skills` → `SkillsPage`. Header intro "Assign skills to agents on the Team page. Built-in skills can be edited or disabled, not deleted." + **New skill** (AgentsConfigure). List `data-testid="skills-list"`: card per skill with name, `/slug`, "Built-in" badge, enable `Toggle` (title "Enabled"/"Disabled"), "Assigned to" chips or "no one yet", **View**, **Edit**, **Delete** (custom skills only). Editor modal ("New skill" / "Edit skill"): Name (≤ 120), Slug (auto from name until touched; ≤ 64; hint "Lowercase letters, digits and dashes. Chat command: /slug"; inline error "Use lowercase letters, digits and dashes only."; disabled for built-ins), Description (≤ 400, counter), "Instructions (SKILL.md, markdown)" textarea (≤ 32000) with **Preview**/**Edit** toggle, "Assign to agents" checkboxes, Cancel / **Save**. Assignment is stored on agents (`PUT …/agents/{agentId}/skills`). Built-ins are seeded by `SkillSeeder` (e.g. `groom-backlog`, `release-notes`, `security-audit`, `dependency-update`). Nav key `skills` hidden in the simplified experience.

## SKL-001 Render the Skills page and the seeded built-ins
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/skills
  - orkka-api:GET /api/projects/{projectId}/skills
  - i18n:skillsPage
```
**Preconditions**: Ready project (technical details on if Managed).
**Steps**:
1. `GET /api/projects/{p}/skills`.
2. Open `/p/{slug}/skills`.
**Expected**:
- Sidebar "Skills" highlighted; heading "Skills" with subtitle "Playbooks your agents follow when the work matches — or that you run in chat with /slug."; intro text; **New skill** button.
- `skills-list` has one card per API row sorted by name; built-ins carry the "Built-in" badge, `/{slug}` in mono, description, an enabled toggle, "Assigned to" chips (agent names) or "no one yet", buttons **View** and **Edit** (no **Delete**).
- With zero skills the card reads "No skills yet. Create one to give your agents a repeatable playbook."

## SKL-002 Create a custom skill and assign it to an agent
```yaml
priority: P0
surface: [ui, api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/skills
  - orkka-api:GET /api/projects/{projectId}/skills
  - orkka-api:GET /api/projects/{projectId}/agents/{agentId}
  - i18n:skillsPage
```
**Preconditions**: SKL-001; agent `Luna` exists.
**Steps**:
1. Click **New skill** → modal "New skill".
2. Type Name `E2E Checklist` — observe the Slug input shows `e2e-checklist`.
3. Type Description `Run the e2e checklist.`; type Instructions `# Checklist\n\n1. Lint\n2. Test`.
4. Under "Assign to agents" tick `Luna`; click **Save**.
5. `GET /api/projects/{p}/agents/{lunaId}`.
**Expected**:
- **Save** is disabled until Name is non-empty and the slug is valid.
- `POST /api/projects/{p}/skills {name:"E2E Checklist", slug:"e2e-checklist", description, content, agentIds:[lunaId]}` → 201 with `isBuiltIn: false`, `isEnabled: true`, `assignedAgentIds: [lunaId]`.
- Toast "Skill saved"; card "E2E Checklist" `/e2e-checklist` appears in alphabetical position with chip "Luna".
- Agent GET → `skillIds` contains the new skill id.
**Notes**: cleanup deletes the skill (SKL-009) or the project.

## SKL-003 Validate the slug inline and on the API
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/skills
  - i18n:skillsPage
```
**Preconditions**: SKL-002 skill exists.
**Steps**:
1. **New skill** → Name `Temp`; edit Slug to `Bad Slug!`.
2. Fix Slug to `temp-ok`.
3. `POST …/skills {name:"x", slug:"Bad Slug!", content:""}`.
4. `POST …/skills {name:"x", slug:"e2e-checklist", content:""}`.
**Expected**:
- Inline error "Use lowercase letters, digits and dashes only." under Slug and **Save** disabled; typed uppercase is lower-cased automatically; error clears with `temp-ok`.
- API invalid slug → 400 "Slug must be lowercase letters, digits and dashes (max 64)."; duplicate slug → 409 `A skill with slug "e2e-checklist" already exists in this project.`
**Notes**: omitting `slug` on POST derives it from the name (`SkillRules.SlugFromName`).

## SKL-004 Toggle the markdown preview in the editor
```yaml
priority: P2
surface: [ui]
automation: planned
refs:
  - orkka-ui:packages/ui/src/components/skills/SkillsPage.tsx
  - i18n:skillsPage
```
**Preconditions**: SKL-002 skill.
**Steps**:
1. Click **Edit** on "E2E Checklist"; click **Preview** above the instructions.
2. Click **Edit** (the same toggle).
**Expected**:
- Textarea is replaced by rendered markdown (an `h1` "Checklist" and an ordered list); the toggle label switches to "Edit".
- Textarea returns with the unchanged content; the toggle reads "Preview" again.

## SKL-005 Keep built-in skills undeletable and their slugs fixed
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:DELETE /api/projects/{projectId}/skills/{skillId}
  - orkka-api:PUT /api/projects/{projectId}/skills/{skillId}
  - i18n:skillsPage
```
**Preconditions**: built-in `groom-backlog` present (from `GET …/skills`).
**Steps**:
1. On the "Built-in" card confirm there is no **Delete**; click **Edit**.
2. Change Name to `Groom backlog (edited)`; click **Save**.
3. `DELETE …/skills/{builtInId}`.
4. `GET …/skills/{builtInId}`.
**Expected**:
- Editor: Slug input is disabled; save sends `PUT …/skills/{id} {name, description, content}` **without** `slug` → 200; card shows the new name and still `/groom-backlog`.
- DELETE → 409 "Built-in skills can be disabled but not deleted."; GET still 200.

## SKL-006 Disable and re-enable a skill
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:PUT /api/projects/{projectId}/skills/{skillId}
  - orkka-api:GET /api/projects/{projectId}/skills
  - i18n:skillsPage
  - i18n:automationPage
```
**Preconditions**: SKL-002 skill enabled.
**Steps**:
1. Click the toggle on "E2E Checklist".
2. Open `/p/{slug}/automation` → **New playbook** → open the "Skill (playbook)" select.
3. Back on Skills, click the toggle again.
**Expected**:
- `PUT …/skills/{id} {isEnabled:false}` → 200; card dims (opacity), toggle wrapper title "Disabled".
- The Automation skill select no longer lists "E2E Checklist (/e2e-checklist)" (only enabled skills are offered); the chat slash menu also omits it.
- Re-enable → `{isEnabled:true}`; card back to normal. A failed PUT reverts the toggle and toasts "Couldn't save the skill."

## SKL-007 View a skill read-only
```yaml
priority: P2
surface: [ui]
automation: planned
refs:
  - orkka-ui:packages/ui/src/components/skills/SkillsPage.tsx
  - i18n:skillsPage
```
**Preconditions**: SKL-002 skill.
**Steps**:
1. Click **View** on "E2E Checklist".
2. Press Escape.
**Expected**:
- Modal titled "E2E Checklist" with the line `/e2e-checklist · Run the e2e checklist.` and the rendered instructions; no inputs.
- Escape closes it without a request.

## SKL-008 Edit a custom skill and change its assignment
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:PUT /api/projects/{projectId}/skills/{skillId}
  - orkka-api:PUT /api/projects/{projectId}/agents/{agentId}/skills
  - orkka-api:GET /api/projects/{projectId}/agents/{agentId}
  - i18n:skillsPage
```
**Preconditions**: SKL-002 (assigned to Luna); second agent `Archie`.
**Steps**:
1. **Edit** on "E2E Checklist" → modal "Edit skill" prefilled; change Slug to `e2e-list`; untick Luna, tick Archie; click **Save**.
2. `GET …/agents/{lunaId}` and `…/agents/{archieId}`.
**Expected**:
- `PUT …/skills/{id} {name, description, content, slug:"e2e-list"}` → 200, then `PUT …/agents/{lunaId}/skills {skillIds:[…without id]}` and `PUT …/agents/{archieId}/skills {skillIds:[…with id]}` → 200 each.
- Toast "Skill saved"; card shows `/e2e-list` and chip "Archie" only.
- Luna's `skillIds` lacks the skill; Archie's contains it.

## SKL-009 Delete a custom skill after confirming
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:DELETE /api/projects/{projectId}/skills/{skillId}
  - orkka-api:GET /api/projects/{projectId}/skills/{skillId}
  - i18n:skillsPage
```
**Preconditions**: a disposable custom skill `Temp`.
**Steps**:
1. Click **Delete** → dialog "Delete skill?" with `"Temp" will be removed from every agent. This cannot be undone.`; click **Cancel**.
2. Repeat; click **Delete**.
3. `GET …/skills/{id}`.
**Expected**:
- Cancel: no request.
- Delete → `DELETE …/skills/{id}` → 204; toast "Skill deleted"; card gone; GET → 404; agents previously assigned no longer list the id.

## SKL-010 Assign skills to an agent from the Team page picker
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/team
  - orkka-api:PUT /api/projects/{projectId}/agents/{agentId}/skills
  - orkka-api:GET /api/projects/{projectId}/skills
  - i18n:skillsPage
```
**Preconditions**: SKL-002 skill unassigned; technical details on.
**Steps**:
1. Open `/p/{slug}/team?agent={lunaId}`; open the **Configure** tab.
2. In the "Skills" picker (`agent-skills-picker`, hint "Playbooks this agent follows when the work matches.") tick "E2E Checklist"; save the agent.
3. `GET …/skills`.
**Expected**:
- `PUT …/agents/{lunaId}/skills {skillIds:[…]}` → 200.
- Skills page card shows chip "Luna"; API `assignedAgentIds` contains Luna. "Manage skills" link in the picker goes to `/p/{slug}/skills`.
**Notes**: the picker is hidden in the simplified experience.

## SKL-011 Invoke a skill in chat with /slug
```yaml
priority: P2
modes: [hybrid-repo]
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/team
  - orkka-ui:packages/ui/src/components/skills/SkillSlashMenu.tsx
  - orkka-api:POST /api/projects/{projectId}/agents/{agentId}/conversations/{conversationId}/messages
  - i18n:skillsPage
```
**Preconditions**: custom skill `/e2e-checklist` whose instructions say "Reply with exactly: CHECKLIST-OK <the user's text>"; assigned to Luna; runner online.
**Steps**:
1. Open Luna's chat; type `/e2e` in the composer.
2. Pick "E2E Checklist" from the menu; type `token-123`; press **Send**.
3. Type `/nonexistent` and observe the menu.
**Expected**:
- `skill-slash-menu` opens listing matching skills with assigned ones first (label "assigned"); selection completes the composer to `/e2e-checklist `.
- Reply contains `CHECKLIST-OK token-123` (the skill's instructions were injected for the turn).
- No match → menu shows "No matching skill".

## SKL-012 Hide skill writes and return 403 without AgentsConfigure
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/skills
  - orkka-api:PUT /api/projects/{projectId}/skills/{skillId}
  - orkka-api:DELETE /api/projects/{projectId}/skills/{skillId}
  - orkka-api:PUT /api/projects/{projectId}/agents/{agentId}/skills
  - orkka-api:GET /api/projects/{projectId}/skills
```
**Preconditions**: API key minted without `AgentsConfigure`; member session without it.
**Steps**:
1. With the key call `POST`, `PUT`, `DELETE`, `PUT …/agents/{id}/skills` (valid bodies) and `GET …/skills`.
2. As the member open `/p/{slug}/skills`.
**Expected**:
- Writes → 403; GET → 200.
- UI: no **New skill**, **Edit**, **Delete**; toggles rendered disabled; **View** still opens the modal.
