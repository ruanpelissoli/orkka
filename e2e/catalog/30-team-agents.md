---
area: TEAM
title: Team — agent roster, configuration, chat
modes-default: [all]
---

Fixture: project `e2e-team-<stamp>`. Seeded roster for an **Unmanaged** project (14): Luna (PM), Muse (Designer), Nova (Full Stack Dev), Archie (Solution Architect), Pixel (Frontend Dev), Atlas (Backend Dev), Forge (DevOps), Sentinel (Code Reviewer), Scout (QA), Rune (Game Dev), Sprite (Game Designer), Sage (DBA), Lex (Legal), Nomad (Mobile Dev). **Managed** projects omit Pixel, Atlas and Forge (11). Chat cases that spend tokens are tagged `hybrid-repo` so the turn runs on the user's runner at $0.

## TEAM-001 Render the team page with the full seeded roster
```yaml
priority: P0
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/team
  - orkka-api:GET /api/projects/{projectId}/agents
  - orkka-api:GET /api/projects/{projectId}/agent-stats
  - i18n:agentCard
```
**Preconditions**: fresh Unmanaged project.
**Steps**:
1. Open `/p/{slug}/team`; `GET /api/projects/{p}/agents`.
**Expected**:
- Heading "Agent Team" with the "Time window" selector; sidebar Team highlighted.
- 14 cards, each with avatar, name, role badge, model badge, repo scope ("All repos" or repo names), stats Tokens / Tasks / Credits and the ops block (`data-testid="agent-ops-stats"` or "No runs in this window").
- API returns 14 agents with `isDefault:true`, `role`, `model`, `repoIds`, `skillIds`.
**Notes**: Pixel/Atlas are scoped to detected repos; the rest are cross-repo.

## TEAM-003 Offer no add, disable or delete controls for seeded agents
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/team
  - orkka-api:DELETE /api/projects/{projectId}/agents/{agentId}
  - i18n:agentConfig
```
**Preconditions**: team page open.
**Steps**:
1. Scan the page for an add-agent button or enable toggle.
2. Open any agent's Configure tab; scroll to the bottom.
3. `DELETE /api/projects/{p}/agents/{luna}`.
**Expected**:
- No "Add agent" control and no enable/disable toggle anywhere.
- The bottom row shows the role badge and the text "Default agent" instead of a "Delete Agent" button.
- API → 400 "Default agents cannot be deleted. You can rename or reconfigure them instead."
**Notes**: the product rule is "the team page only lists, configures and chats".

## TEAM-004 Open the agent detail panel and switch tabs
```yaml
priority: P1
surface: [ui]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/team
  - orkka-ui:apps/local/app/[locale]/(dashboard)/p/[projectId]/team/AgentDetailPanel.tsx
  - i18n:agentChat
```
**Preconditions**: team page open.
**Steps**:
1. Click Nova's card.
2. Click the "Chat" tab, then "Configure".
3. Press Escape; reopen via the card's chat icon (title "Chat"); click the backdrop.
**Expected**:
- A right-side panel slides in with Nova's avatar, name, role badge, × button and tabs "Configure" (active) / "Chat".
- Tabs switch content; Escape and backdrop click close the panel; the chat icon opens the panel directly on "Chat".
- Body scroll is locked while the panel is open.
**Notes**: —

## TEAM-005 Rename an agent from the Configure tab
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/team
  - orkka-api:PUT /api/projects/{projectId}/agents/{agentId}
  - i18n:agentConfig
```
**Preconditions**: owner session.
**Steps**:
1. Open Nova → Configure; change "Name" to "Nova Prime"; click "Save Changes".
2. `GET …/agents/{id}`.
**Expected**:
- Button reads "Saving..." then the panel closes; the card shows "Nova Prime".
- `PUT /api/projects/{p}/agents/{id}` carried `name`; GET returns the new name.
**Notes**: restore the name afterwards.

## TEAM-006 Pick the agent model from grouped, billing-annotated options
```yaml
priority: P1
modes: [hybrid-repo, hybrid-folder]
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/team
  - orkka-api:GET /api/models
  - orkka-api:GET /api/me/runner
  - orkka-api:PUT /api/projects/{projectId}/agents/{agentId}
  - i18n:modelBilling
  - i18n:agentConfig
```
**Preconditions**: runner online with Claude ready and GitHub Copilot signed in; Codex not signed in.
**Steps**:
1. Open Nova → Configure → "Model".
2. Pick a Copilot model; read the note; click "Save Changes".
3. Open Scout (QA) → Model.
**Expected**:
- `<optgroup>` per provider including a Copilot group (present only because the runner reports Copilot); rows read "{model} · your runner, 0 credits" or "{model} — provider not connected" (disabled; there is no cloud option) — except the agent's own saved model, which stays selectable.
- The note under the picker follows the current selection; Copilot adds "Copilot models spend premium requests on your Copilot plan, never Orkka credits."
- Save → PUT with `model`; the card's model badge updates.
- Scout: non-MCP models read "{name} — no browser tools" and are disabled; hint "QA agents drive a browser through MCP tools…".
**Notes**: without a catalog the block reads "The model catalog is unavailable right now — this agent keeps its current model…".

## TEAM-007 Edit custom instructions with the 4000-character counter
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/team
  - orkka-api:PUT /api/projects/{projectId}/agents/{agentId}
  - i18n:agentConfig
```
**Preconditions**: owner session.
**Steps**:
1. Open Luna → Configure; type "Always answer in bullet points." into "Custom Instructions"; watch the counter; click "Save Changes".
2. `GET …/agents/{id}`.
**Expected**:
- Counter shows `n/4000`; the textarea caps at 4000.
- PUT carries `userInstructions`; GET returns it; hint "Appended to the agent's system prompt for this project."
**Notes**: "View System Prompt" (TEAM-014) shows the assembled prompt.

## TEAM-008 Scope a dev agent to repositories
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/team
  - orkka-api:PUT /api/projects/{projectId}/agents/{agentId}
  - i18n:agentConfig
```
**Preconditions**: two repos linked.
**Steps**:
1. Open Pixel → Configure; under "Repositories" tick only fixture-web; Save.
2. Reopen; untick everything; Save.
3. Open Luna (PM) → Configure.
**Expected**:
- Step 1 → PUT `repoIds:[web]`; the card lists "fixture-web".
- Step 2 → PUT `repoIds:[]` (marker `repoIdsPresent=1`); card reads "All repos".
- Luna has no Repositories block (non-dev role).
- `PUT … {repoIds:["01NOTAREPO…"]}` → 400 "RepoIds must contain valid repository ids."
**Notes**: hint "Tasks for checked repositories are routed to this agent first…".

## TEAM-009 Assign skills with the skills picker
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/team
  - orkka-ui:packages/ui/src/components/skills/AgentSkillsPicker.tsx
  - orkka-api:GET /api/projects/{projectId}/skills
  - orkka-api:PUT /api/projects/{projectId}/agents/{agentId}
  - i18n:skillsPage
```
**Preconditions**: project has built-in skills (seeded) plus one custom skill.
**Steps**:
1. Open Nova → Configure; in `data-testid="agent-skills-picker"` ("Skills", "Manage skills" link, hint "Playbooks this agent follows when the work matches.") tick the custom skill; Save.
2. `GET …/agents/{id}`.
3. `PUT … {skillIds:["<skill of another project>"]}`.
**Expected**:
- PUT carries `skillIds:[…]` with marker `skillIdsPresent=1`; GET shows the id; the Skills page lists Nova under "Assigned to".
- Disabled skills carry a "Disabled" tag in the picker.
- Step 3 → 400 "SkillIds must belong to this project."
**Notes**: an empty project shows "No skills in this project yet."

## TEAM-011 Hide Save and return 403 without AgentsConfigure
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/team
  - orkka-api:PUT /api/projects/{projectId}/agents/{agentId}
  - orkka-api:PUT /api/projects/{projectId}/agents/{agentId}/skills
  - orkka-api:POST /api/projects/{projectId}/agents/switch-provider
```
**Preconditions**: restricted key without AgentsConfigure; (optional) member account without it.
**Steps**:
1. Key: `PUT …/agents/{id} {name:"x"}`; `PUT …/agents/{id}/skills {skillIds:[]}`; `POST …/agents/switch-provider {provider:"Anthropic"}`.
2. Member: open Configure.
**Expected**:
- All three → 403.
- The form renders read-only fields with no "Save Changes" button.
**Notes**: —

## TEAM-012 Validate agent updates over the API
```yaml
priority: P2
surface: [api]
automation: planned
refs:
  - orkka-api:PUT /api/projects/{projectId}/agents/{agentId}
  - orkka-api:GET /api/models
```
**Preconditions**: models list with one `supportsMcp:false` chat model.
**Steps**:
1. `PUT …/agents/{scout} {model:"<non-MCP model>"}`.
2. `PUT …/agents/{nova} {imageProvider:"gemini"}`.
3. `PUT …/agents/{muse} {imageProvider:"not-a-provider"}`.
4. `PUT …/agents/{nova} {model:"made-up"}`.
**Expected**:
- 1 → 400 "QA agents drive a browser through MCP tools — pick a model with MCP support."
- 2 → 400 "Only design agents (Designer, Game Designer) generate images."
- 3 → 400 listing the allowed image providers.
- 4 → 400 (not a selectable model).
**Notes**: —

## TEAM-013 Choose the image model for design agents
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/team
  - orkka-api:PUT /api/projects/{projectId}/agents/{agentId}
  - i18n:agentConfig
```
**Preconditions**: enabled image models in the catalog.
**Steps**:
1. Open Muse → Configure; read "Image Model" (default "Automatic (host preference)"); pick a provider; Save.
2. Open Sprite → same select exists; open Nova → absent.
**Expected**:
- PUT carries `imageProvider`; GET returns it; choosing "Automatic" sends `""` and clears it.
- Hint "Used for generated raster images. A specific choice is strict…".
**Notes**: —

## TEAM-014 Read agent statistics and the system prompt
```yaml
priority: P2
surface: [ui]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/team
  - i18n:agentConfig
```
**Preconditions**: agent with at least one run (or zero — values render as 0).
**Steps**:
1. Open Configure; read "Statistics"; click "View System Prompt".
**Expected**:
- Boxes Tokens In, Tokens Out, (Cache Read / Cache Write when billed), Tasks Completed, Tasks Failed, Total Credits, Active Since.
- The system prompt expands in a monospace block and collapses on a second click.
**Notes**: —

## TEAM-015 Switch the whole team to another provider
```yaml
priority: P2
modes: [hybrid-repo, hybrid-folder]
surface: [api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/agents/switch-provider
  - orkka-api:GET /api/projects/{projectId}/agents
```
**Preconditions**: owner key.
**Steps**:
1. `POST …/agents/switch-provider {provider:"OpenAI"}`; `GET …/agents`.
2. `POST … {provider:"Anthropic"}`.
3. `POST … {provider:"PixelLab"}`; `{provider:"Nope"}`.
**Expected**:
- 1 → 200 `{switchedCount:13, skippedQaCount:1}`; every agent except Scout now has an OpenAI model (PM on the fast tier, others capable); Scout keeps Claude.
- 2 → 200 with `skippedQaCount:0` and all 14 on Anthropic.
- 3 → 400 "'PixelLab' is not a valid provider…" / "'Nope' is not a valid provider…".
**Notes**: AgentsConfigure; the UI trigger is the "Switch team to {provider}" button (TEAM-033).

## TEAM-016 Read windowed ops stats per agent
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/team
  - orkka-api:GET /api/projects/{projectId}/agent-stats
  - i18n:teamStats
```
**Preconditions**: project with a few completed runs.
**Steps**:
1. On the team page click "24h", "7d", "30d" in "Time window".
2. `GET /api/projects/{p}/agent-stats?window=30d`.
**Expected**:
- Each click refetches `agent-stats?window=…`; cards show "Runs", "Success", "Avg run" and "Last error" inside `agent-ops-stats`, or "No runs in this window".
- API → `{window:"30d", from, to, agents:[{agentId, runs, succeeded, failed, running, avgDurationSeconds, lastError}]}`.
**Notes**: reads open to any member.

## TEAM-017 Manage conversations: new, list, back, delete
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/team
  - orkka-api:GET /api/projects/{projectId}/agents/{agentId}/conversations
  - orkka-api:POST /api/projects/{projectId}/agents/{agentId}/conversations
  - orkka-api:DELETE /api/projects/{projectId}/agents/{agentId}/conversations/{conversationId}
  - i18n:agentChat
```
**Preconditions**: Luna has no conversations.
**Steps**:
1. Open Luna → Chat: empty state "Start a conversation with Luna" / "Type a message below to begin".
2. Click "New Chat" twice.
3. Click the "2 conversations" header button; read the list; click "Back"; reopen and hover a row, click its trash icon.
**Expected**:
- Each "New Chat" → `POST …/conversations` 201 and focuses the composer; empty conversation shows "Send a message to Luna".
- List rows show "Untitled", "0 messages · just now"; "Back" returns to the thread; delete → `DELETE …/conversations/{id}` 204 and the row disappears.
- `GET …/conversations` lists remaining conversations with `messageCount`, `lastMessageAt`, `hasActiveRun`.
**Notes**: AgentsChat for create/delete; the header shows "No conversations yet" when empty.

## TEAM-018 Send a message and watch the reply stream
```yaml
priority: P0
modes: [hybrid-repo]
surface: [ui, api, hub]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/team
  - orkka-api:POST /api/projects/{projectId}/agents/{agentId}/conversations/{conversationId}/messages
  - orkka-api:GET /api/projects/{projectId}/agents/{agentId}/conversations/{conversationId}/messages
  - hub:TaskHub.AgentChatMessage
  - i18n:agentChat
```
**Preconditions**: runner online, Claude ready; Luna → Chat open.
**Steps**:
1. Type "Reply with the single word pong." in "Ask Luna something..."; press Shift+Enter (newline) then Enter.
2. Wait for the stream to end; `GET …/messages`.
**Expected**:
- The user bubble appears immediately; the send button becomes the stop square; typing dots, optional violet "Thinking..." block (collapsible to "Thought"), then the streamed assistant text with the caption "Luna is still working — this can take a few minutes".
- On done the assistant bubble persists with a timestamp; the conversation title updates; `GET …/messages` returns both messages.
- The textarea auto-grows up to 128 px and is disabled while streaming.
**Notes**: $0 on the runner.

## TEAM-019 Stop a reply in flight
```yaml
priority: P1
modes: [hybrid-repo]
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/team
  - orkka-api:POST /api/projects/{projectId}/agents/{agentId}/conversations/{conversationId}/cancel-run
  - i18n:agentChat
```
**Preconditions**: a long prompt streaming (e.g. "List 200 numbered lines").
**Steps**:
1. Click the button titled "Stop generating".
**Expected**:
- `POST …/cancel-run` → 200 `{cancelled:true}`; the stream ends; the partial text (if any) is kept, an empty reply adds no bubble; the composer is enabled again.
**Notes**: —

## TEAM-020 Keep the composer disabled while a detached run is still replying
```yaml
priority: P2
modes: [hybrid-repo]
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/team
  - orkka-api:GET /api/projects/{projectId}/agents/{agentId}/conversations
  - orkka-api:POST /api/projects/{projectId}/agents/{agentId}/conversations/{conversationId}/messages
```
**Preconditions**: a reply streaming; close the panel mid-reply.
**Steps**:
1. Reopen Luna → Chat.
2. From the API key `POST …/conversations/{id}/messages {content:"again"}` while the run is active.
**Expected**:
- The conversation is marked `hasActiveRun:true`; typing dots show, the textarea and attach buttons are disabled, the stop button is offered; when the reply lands (hub `AgentChatMessage` or the 10 s poll) the composer re-enables.
- Step 2 → 409 "The agent is still working on a reply — wait for it to finish or cancel it first."
**Notes**: —

## TEAM-021 Surface a streamed error in the chat
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/team
  - orkka-api:POST /api/projects/{projectId}/agents/{agentId}/conversations/{conversationId}/messages
  - i18n:agentChatTab
```
**Preconditions**: a way to make the turn fail (harness: stub the SSE to emit `data: {"error":"boom","done":true}`; live: an org paused by budget → 402).
**Steps**:
1. Send a message.
**Expected**:
- The red banner under the thread shows the error text; streaming state clears; the composer is enabled.
- A transport failure shows "Failed to send message."; a failed conversation load shows "Failed to load conversations."
**Notes**: —

## TEAM-022 Show the "Preparing the workspace" notice on a first hybrid chat turn
```yaml
priority: P2
modes: [hybrid-repo]
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/team
  - orkka-api:POST /api/projects/{projectId}/agents/{agentId}/conversations/{conversationId}/messages
```
**Preconditions**: runner online; a freshly linked repo never cloned on the runner (`~/.orkka/repos/{org}/chat-workspace/{project}` absent).
**Steps**:
1. Open Nova → Chat; send "What files are in the repo root?".
**Expected**:
- After ~20 s the thinking block shows "Preparing the workspace — the first time on a large project this includes cloning its repositories and can take a few minutes."; the reply lists real files from the fixture repo.
- A second turn skips the notice.
**Notes**: the chat runs on the runner's chat workspace, $0.

## TEAM-023 Load earlier messages in a long conversation
```yaml
priority: P3
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/team
  - orkka-api:GET /api/projects/{projectId}/agents/{agentId}/conversations/{conversationId}/messages
  - i18n:agentChat
```
**Preconditions**: a conversation with more messages than one page (seed via repeated turns or DB).
**Steps**:
1. Open it; click "Load earlier messages".
**Expected**:
- Request carries `before={oldest createdAt}`; older messages prepend and the scroll position is preserved; the button disappears when `hasMore:false`.
**Notes**: —

## TEAM-024 Autocomplete a skill with the slash menu
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/team
  - orkka-ui:packages/ui/src/components/skills/SkillSlashMenu.tsx
  - orkka-api:GET /api/projects/{projectId}/skills
  - i18n:skillsPage
```
**Preconditions**: enabled skills, one assigned to Luna.
**Steps**:
1. In Luna's composer type `/`; then `/gro`; press ArrowDown, ArrowUp, then Tab; type `/zzz`; press Escape.
**Expected**:
- `data-testid="skill-slash-menu"` opens listing `/slug` + name, assigned skills first with the tag "assigned"; typing filters; arrows move the active row; Tab/Enter completes the input to `/slug ` and closes the menu.
- `/zzz` shows "No matching skill"; Escape closes.
**Notes**: the backend parses `/slug` itself; the menu is a convenience.

## TEAM-025 Attach a file to the next chat message
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/team
  - orkka-ui:packages/ui/src/components/chat/ChatContextControls.tsx
  - orkka-api:POST /api/projects/{projectId}/chat-attachments
  - i18n:agentChat
```
**Preconditions**: a 1 KB `notes.md`, an 11 MB file, a `.exe`.
**Steps**:
1. Click "Attach a file" and pick `notes.md`; click the chip's "Remove attachment"; re-attach.
2. `POST /api/projects/{p}/chat-attachments` with the 11 MB file; with the `.exe`.
**Expected**:
- Upload → 200 `{id, fileName…}`; a chip "notes.md" appears above the composer; sending prepends the marker `[attachment:{id} "notes.md"]` to the message.
- Step 2 → 400 "The file exceeds the 10 MB attachment limit." / 400 "File type \".exe\" is not supported as chat context."
**Notes**: AgentsChat.

## TEAM-026 Link documents and design assets as chat context
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/team
  - orkka-api:GET /api/projects/{projectId}/documents
  - orkka-api:GET /api/projects/{projectId}/design-assets
  - i18n:agentChat
```
**Preconditions**: one document and one design asset in the project.
**Steps**:
1. Click "Link project context"; read the sections "Documents" and "Design assets"; click the document; reopen and click the asset.
2. Send a message.
**Expected**:
- Popover shows "Loading…" then both lists; picked items become chips (design chips show "vN") and are disabled in the picker; empty sections read "Nothing here yet".
- The sent content starts with `[document:{id} "{title}"]` and `[design-asset:{id} "{name}" vN]` marker lines.
**Notes**: —

## TEAM-027 Create tasks from the PM chat and jump to the board
```yaml
priority: P1
modes: [hybrid-repo]
surface: [ui, api, hub]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/team
  - orkka-api:POST /api/projects/{projectId}/agents/{agentId}/conversations/{conversationId}/messages
  - orkka-api:GET /api/projects/{projectId}/board
  - hub:TaskHub.TaskCreated
  - i18n:agentChat
```
**Preconditions**: Luna → Chat; fixture repo linked.
**Steps**:
1. Send "Create one task titled 'e2e chat task' to add a list item 'chat-token' to the features list. Do not ask questions."
2. Click "View task" on the result card.
**Expected**:
- While streaming a "Creating tasks..." row shows; on done a green card "#n e2e chat task" with "View task" (or "Tasks added to the board" / "View on the board" when extraction failed).
- The board has the new card in Drafting; the link opens `/p/{slug}/tasks/{id}`.
- Cards created from chat count toward TasksCreate (a key without it cannot create).
**Notes**: $0 on the runner.

## TEAM-028 Create an architecture diagram from Archie's chat
```yaml
priority: P2
modes: [hybrid-repo]
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/team
  - orkka-ui:/p/[projectId]/architect
  - orkka-api:GET /api/projects/{projectId}/diagrams
  - i18n:agentChat
```
**Preconditions**: Archie → Chat.
**Steps**:
1. Send "Draw a two-box diagram: Browser → API. Create it as a diagram now, no questions."
2. Click "View on the Architecture Board".
**Expected**:
- "Drawing diagram..." while the ```excalidraw block streams; then a card with the diagram title and "View on the Architecture Board" → `/p/{slug}/architect?d={id}`.
- `GET …/diagrams` lists it; the Architect page opens it selected.
**Notes**: —

## TEAM-029 Create an SVG design asset from Muse without an image API key
```yaml
priority: P2
modes: [hybrid-repo]
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/team
  - orkka-ui:/p/[projectId]/design
  - orkka-api:GET /api/projects/{projectId}/design-assets
  - i18n:agentChat
```
**Preconditions**: Muse → Chat; no Gemini/OpenAI image key configured for the org.
**Steps**:
1. Send "Create a simple SVG icon of a circle named 'e2e-circle'. Deliver it as an svg design block."
2. Click "View in Design gallery".
**Expected**:
- "Creating designs..." then a violet card "e2e-circle" with a thumbnail and "View in Design gallery" → `/p/{slug}/design?a={id}`; `GET …/design-assets` lists the asset with `kind:"Svg"`.
- Asking for a raster image instead ends with "Design generation failed — no assets were saved…" or a Claude-authored SVG fallback, never a spinner.
**Notes**: raster generation needs an image provider; SVGs are authored by the agent directly.

## TEAM-030 Save a document from Luna's drawer on the Documents page
```yaml
priority: P2
modes: [hybrid-repo]
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/documents
  - orkka-ui:apps/local/app/[locale]/(dashboard)/p/[projectId]/team/AgentChatDrawer.tsx
  - orkka-api:GET /api/projects/{projectId}/documents
  - i18n:agentChat
```
**Preconditions**: `/p/{slug}/documents` open.
**Steps**:
1. Click the floating "Chat with Luna" launcher.
2. Send "Save a one-paragraph document titled 'e2e note'."
3. Click "View in Documents"; click "Close chat".
**Expected**:
- A slide-over opens (no backdrop; the page stays usable) with Luna's name and role.
- "Saving document..." then an amber card "e2e note" / "View in Documents" → `/p/{slug}/documents?d={id}`; the browser behind refreshes and lists the document.
- Close hides the drawer and restores the launcher.
**Notes**: the Architect page hosts the same drawer for Archie (TEAM-036).

## TEAM-031 Open the designer chat from a Design "Iterate" deep link
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/team
  - orkka-ui:/p/[projectId]/design
  - orkka-api:GET /api/projects/{projectId}/design-assets/{assetId}
  - i18n:agentChat
```
**Preconditions**: a design asset A (TEAM-029).
**Steps**:
1. Open `/p/{slug}/team?agent={muse}&tab=chat&asset={A}`.
2. Click the chip's "Remove attachment"; reload; keep it and send "Make it blue."
**Expected**:
- Muse's panel opens on Chat with a chip showing the asset thumbnail, name and "vN".
- Remove clears the chip; with it kept the sent message starts with `[design-asset:{A} "…" vN]` and the user bubble renders the name as an attachment chip instead of the raw marker.
**Notes**: the Design page's "Iterate" button produces this URL.

## TEAM-032 Drive conversations over the API and enforce AgentsChat
```yaml
priority: P1
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/agents/{agentId}/conversations
  - orkka-api:POST /api/projects/{projectId}/agents/{agentId}/conversations
  - orkka-api:GET /api/projects/{projectId}/agents/{agentId}/conversations/{conversationId}/messages
  - orkka-api:DELETE /api/projects/{projectId}/agents/{agentId}/conversations/{conversationId}
  - orkka-api:POST /api/projects/{projectId}/agents/{agentId}/conversations/{conversationId}/cancel-run
  - orkka-api:POST /api/projects/{projectId}/chat-attachments
```
**Preconditions**: owner key; key K without AgentsChat.
**Steps**:
1. Owner: `POST …/conversations` → id; `GET …/conversations`; `GET …/conversations/{id}/messages`; `POST …/cancel-run`; `DELETE …/conversations/{id}`.
2. K: `POST …/conversations`; `DELETE …`; `POST …/messages`; `POST …/cancel-run`; `POST /api/projects/{p}/chat-attachments`; then `GET …/conversations`.
**Expected**:
- 1 → 201; 200 list; 200 `{messages:[], hasMore:false}`; 200 `{cancelled:false}`; 204.
- 2 → 403 for every write; 200 for the read.
**Notes**: —

## TEAM-033 Show the provider-mismatch banner and switch the team
```yaml
priority: P2
modes: [hybrid-repo, hybrid-folder]
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/team
  - orkka-ui:apps/local/components/runner/ProviderMismatchBanner.tsx
  - orkka-api:POST /api/projects/{projectId}/agents/switch-provider
  - i18n:providerMismatch
```
**Preconditions**: runner online with only Claude ready; Nova moved to an OpenAI model.
**Steps**:
1. Open the team page.
2. Click "Switch team to Anthropic".
**Expected**:
- Banner "1 agent uses a model from a provider that isn't signed in on your runner — its tasks will wait until you sign in there." with the button.
- Click → `POST …/agents/switch-provider {provider:"Anthropic"}`; toast "Switched N agents to Anthropic."; the banner disappears.
- No banner when no agent mismatches.
**Notes**: the board shows the compact variant (BRD-037).

## TEAM-034 Keep DBA and Legal advisory-only
```yaml
priority: P2
surface: [ui]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/team
  - orkka-ui:/p/[projectId]/tasks/new
```
**Preconditions**: team page and create form.
**Steps**:
1. Open Sage → Configure and Lex → Configure.
2. Open `/tasks/new` → "Assign to agent".
**Expected**:
- Sage/Lex have Name, Model, Custom Instructions and a Chat tab but no Repositories block.
- The assign select lists only dev roles (Nova, Pixel, Atlas, Rune, Nomad) — Sage, Lex, Luna, Muse, Scout, Sentinel, Forge, Archie, Sprite are absent.
**Notes**: they answer in chat like any agent (TEAM-018 applies).

## TEAM-035 List the model catalog
```yaml
priority: P2
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/models
```
**Preconditions**: any member key.
**Steps**:
1. `GET /api/models`.
**Expected**:
- 200 array of `{id, name, provider, kind ("Chat"|"Image"), description, supportsMcp, isEnabled, requiresRunner, sortOrder}`; at least one Anthropic chat model with `supportsMcp:true`; no pricing fields.
**Notes**: the Team and task forms filter `kind==="Chat"`.

## TEAM-036 Use the Architect companion drawer
```yaml
priority: P3
surface: [ui]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/architect
  - orkka-ui:apps/local/app/[locale]/(dashboard)/p/[projectId]/team/AgentChatDrawer.tsx
  - i18n:agentChat
```
**Preconditions**: `/p/{slug}/architect` open.
**Steps**:
1. Click the launcher "Chat with Archie"; click "New Chat"; click "Close chat".
**Expected**:
- Drawer header shows "Archie" and the role label; New Chat creates a conversation for Archie (visible later on the Team page); Close restores the launcher.
- Documents page shows Luna (PM); a project without an agent of the role renders no launcher.
**Notes**: —

## TEAM-037 Deep-link into an agent, tab, conversation and message
```yaml
priority: P2
surface: [ui]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/team
```
**Preconditions**: Luna has a conversation C with message M.
**Steps**:
1. Open `/p/{slug}/team?agent={luna}`.
2. Open `/p/{slug}/team?agent={luna}&tab=chat&c={C}&m={M}`.
3. Open `/p/{slug}/team?agent=01UNKNOWN`.
**Expected**:
- 1 → Luna's panel opens on the Configure tab.
- 2 → Chat tab with conversation C selected (not the most recent) and message M scrolled into view with a 3 s highlight ring.
- 3 → no panel opens; the grid renders normally.
**Notes**: —

## TEAM-038 Set every agent's model at once
```yaml
priority: P2
modes: [hybrid-repo, hybrid-folder]
surface: [ui, api]
automation: planned
since: 2026-11-18
refs:
  - orkka-api:PUT /api/projects/{projectId}/agents/model
  - orkka-ui:/p/[projectId]/team
  - orkka-ui:apps/local/app/[locale]/(dashboard)/p/[projectId]/team/TeamModelControl.tsx
  - i18n:teamModel
```
**Preconditions**: runner online with Claude and Copilot signed in, Codex not; seeded project.
**Steps**:
1. Open `/p/{slug}/team`; open the "Set every agent's model" select.
2. Pick "Copilot Auto"; click "Apply to all agents"; read the dialog; click "Set model".
3. Repeat step 2 with "Copilot Auto" again.
4. Pick "Claude Sonnet 5"; apply and confirm.
5. `PUT …/agents/model {model:"no-such-model"}`; `{model:"copilot/claude-sonnet-4.5"}` (retired); `{model:""}`.
**Expected**:
- 1 → only models the runner has ready are listed (Claude and Copilot groups; no OpenAI).
- 2 → nothing changes before "Set model"; the dialog names Copilot Auto and says QA keeps its model. After it: 200 `{updatedCount:13, unchangedCount:0, skippedQaCount:1}`, toast "13 agents now use Copilot Auto. 1 QA agent kept its model."; every card except Scout shows Copilot Auto; one `AgentConfigChanged` audit event per changed agent; another project's agents are untouched.
- 3 → `{updatedCount:0, unchangedCount:13, skippedQaCount:1}`, no new audit events.
- 4 → no QA note in the dialog; all 14 agents on Claude Sonnet 5.
- 5 → 400 each; no agent changes.
**Notes**: AgentsConfigure, like the per-agent update (TEAM-005). Agents' saved models never change on their own — a new Copilot default (copilot/auto on every tier) only reaches agents seeded afterwards; this control (one project) and the workspace AI provider with “Also switch the agents in existing projects” (every project, SET-043) are how existing teams move.

## TEAM-039 Say on the team page that browser QA needs Claude, and that the provider can be chosen
```yaml
priority: P2
modes: [hybrid-repo, hybrid-folder]
surface: [ui]
automation: planned
since: 2026-11-19
refs:
  - orkka-ui:/p/[projectId]/team
  - orkka-ui:apps/local/app/[locale]/(dashboard)/p/[projectId]/team/TeamProviderNotes.tsx
  - orkka-api:GET /api/org/ai-provider
  - orkka-api:GET /api/me/runner
  - i18n:teamProvider
```
**Preconditions**: seeded project; workspace AI provider Automatic.
**Steps**:
1. Runner with Claude and Copilot signed in → open `/p/{slug}/team`.
2. Choose Copilot on Settings → Workspace; reopen the team page.
3. Sign Claude out on the runner (Copilot stays) → reopen the team page.
**Expected**:
- 1 → one grey line "Both Copilot and Claude are signed in, and this workspace's teams use Claude. To use your Copilot plan instead, choose it in Settings → Workspace" (the link opens `/settings/workspace`); no QA line.
- 2 → no hint.
- 3 → one amber line "Automated browser QA needs Claude: Scout stays on a Claude model, and the Testing lane can't run until Claude Code is signed in on this machine. Run `claude`".
**Notes**: nothing shows while the runner's status is unknown. QA is always seeded on Claude, whatever the workspace provider.
