---
area: DSN
title: Design — brand kit and design-asset gallery
modes-default: [all]
---

Page `/p/{slug}/design` (hosted `apps/remote/.../design/*`, Local `/p/{id}/design`). Composition: `BrandKitPanel` (renders nothing when `GET …/brand` is 404) → image-billing note → `DesignGallery` (status filter pills, **Upload**, `FolderTreePanel`, `DesignAssetCard` grid, `MoveToFolderDialog`, folder-delete `ConfirmDialog`, `AgentChatDrawer` for the Designer, `DesignHistoryModal`). One card per lineage (`rootAssetId`); older versions live in History. Uploads are created **Approved** (`UploadDesignAssetEndpoint` sets `Status = Approved`). All writes need `DesignReview`; every read is open to members.

Fixture used below: `fixture.png` (small PNG), `fixture.svg` (small SVG), `big.png` (> 10 MB).

## DSN-001 Render the Design page with its landmarks
```yaml
priority: P1
surface: [ui]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/design
  - orkka-api:GET /api/projects/{projectId}/design-assets
  - i18n:design
```
**Preconditions**: Ready project with at least one design asset (upload `fixture.png` via DSN-008 first).
**Steps**:
1. Open `/p/{slug}/design`.
**Expected**:
- Sidebar item "Design" is highlighted (`data-tour="nav-design"`, `aria-current`).
- Heading "Design"; filter pills "All", "Pending", "Approved", "Rejected" each followed by a count; **Upload** button; folder pane titled "Folders" with "All items".
- One card per asset lineage with the asset preview image, name, kind badge (`PNG`/`SVG`/…), status badge.
**Notes**: the filter pill group only renders when the project has ≥ 1 asset.

## DSN-002 Show the brand kit palette above the gallery
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/brand
  - orkka-ui:packages/ui/src/components/design/BrandKitPanel.tsx
  - i18n:brandPanel
  - i18n:brandKit
```
**Preconditions**: project whose `GET /api/projects/{p}/brand` returns 200 (a project with a repository-derived kit).
**Steps**:
1. `GET /api/projects/{p}/brand` — note `palette`, `displayFont`, `bodyFont`, `tone`, `source`.
2. Open `/p/{slug}/design`.
**Expected**:
- Card titled "Brand" with the source line matching `source` ("Read from the existing codebase…", or "Set by you. Never regenerated.").
- Nine swatches labelled Primary, Primary hover, Secondary, Accent, Background, Surface, Text, Muted text, Border, each showing its hex value from the response.
- "Display font" / "Body font" values when set; **Edit** button visible to DesignReview holders.
**Notes**: on a project where the endpoint is 404 the panel renders nothing (no card, no error) — assert absence on a fresh Unmanaged project.

## DSN-003 Edit and save the brand kit
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:PUT /api/projects/{projectId}/brand
  - orkka-api:GET /api/projects/{projectId}/brand
  - i18n:brandPanel
```
**Preconditions**: DSN-002.
**Steps**:
1. On the Brand card click **Edit**.
2. Change the "Primary" input to `#2563eb`, set "Display font" to `Inter`.
3. Click **Save**.
4. `GET /api/projects/{p}/brand`.
**Expected**:
- `PUT /api/projects/{p}/brand {palette, displayFont, bodyFont, tone}` → 200.
- Card leaves edit mode; Primary swatch shows `#2563eb`; source line becomes "Set by you. Never regenerated."
- GET returns `source: "Manual"`, `palette.primary: "#2563eb"`, `displayFont: "Inter"`.
**Notes**: **Cancel** discards the draft without a request.

## DSN-004 Reject an invalid hex value in the brand kit editor
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-api:PUT /api/projects/{projectId}/brand
  - i18n:brandPanel
```
**Preconditions**: DSN-002.
**Steps**:
1. Click **Edit**; set "Accent" to `blue`; click **Save**.
2. `PUT /api/projects/{p}/brand` with `palette.accent: "blue"` (other fields valid).
**Expected**:
- UI: amber message "Accent must be a 6-digit hex value like #2563eb." and no PUT request is sent.
- API: 400 validation problem naming `Palette.Accent`.

## DSN-005 Refuse brand kit writes without DesignReview
```yaml
priority: P2
surface: [api, ui]
automation: planned
refs:
  - orkka-api:PUT /api/projects/{projectId}/brand
  - orkka-api:GET /api/projects/{projectId}/brand
```
**Preconditions**: API key minted without `DesignReview`.
**Steps**:
1. `GET /api/projects/{p}/brand` with that key.
2. `PUT /api/projects/{p}/brand` (valid body) with that key.
3. Sign in as a member without DesignReview; open `/p/{slug}/design`.
**Expected**:
- GET → 200; PUT → 403.
- Brand card shows no **Edit** button (`RequirePermission` hides it).

## DSN-006 Show the image-billing note on the Design page
```yaml
priority: P3
surface: [ui]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/design
  - i18n:design
```
**Preconditions**: any Ready project.
**Steps**:
1. Open `/p/{slug}/design`.
**Expected**:
- Text "Raster images and GIFs are generated with platform image models and billed to your workspace — image APIs can't run on a connected subscription. SVG assets authored in chat can run on your own runner at no cost." is visible between the Brand card and the gallery header.

## DSN-007 Filter the gallery by status
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/design
  - orkka-api:GET /api/projects/{projectId}/design-assets
  - i18n:design
```
**Preconditions**: one Approved asset (upload), one Rejected asset (DSN-014), one Pending asset (agent-authored, or skip the Pending assertion).
**Steps**:
1. Open the page; read the counts on each pill.
2. Click "Rejected"; then "Approved"; then "All".
3. `GET /api/projects/{p}/design-assets?status=Approved`.
**Expected**:
- Counts equal the number of lineages per status; "All" equals total lineages.
- "Rejected" shows only cards with the "Rejected" badge; a status with no assets shows the empty state "No assets match this filter" (no "Go to Team" button).
- API with `?status=Approved` returns only `status: "Approved"` rows.

## DSN-008 Upload an image into the selected folder
```yaml
priority: P0
surface: [ui, api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/design-assets/upload
  - orkka-api:GET /api/projects/{projectId}/design-assets
  - i18n:design
  - i18n:folders
```
**Preconditions**: Ready project; Owner session.
**Steps**:
1. Open `/p/{slug}/design`; in the folder pane click **New folder**, type `logos`, confirm; select "logos".
2. Click **Upload**; pick `fixture.png`.
3. `GET /api/projects/{p}/design-assets`.
**Expected**:
- Request `POST /api/projects/{p}/design-assets/upload?folderPath=logos` (multipart) → 200 with `status: "Approved"`, `folderPath: "logos"`, `version: 1`, `url` and `downloadUrl`.
- Button reads "Uploading..." while in flight, then the card appears under "logos" with "uploaded by {your name}", kind badge `PNG`, status "Approved".
- With "All items" selected the upload goes to the root (`folderPath: ""`).
**Notes**: cleanup deletes the asset (DSN-020) or the project.

## DSN-009 Block oversized or unsupported uploads
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/design-assets/upload
  - i18n:design
```
**Preconditions**: DSN-008 project.
**Steps**:
1. Click **Upload**; pick `big.png` (> 10 MB).
2. `POST …/design-assets/upload` with an 11 MB PNG.
3. `POST …/design-assets/upload` with `notes.txt`.
4. `POST …/design-assets/upload` with no file part.
**Expected**:
- UI: toast "big.png exceeds the 10 MB upload limit." and no request (client guard `MAX_UPLOAD_BYTES`).
- API: 400 "The file exceeds the 10 MB upload limit."; 400 "File type ".txt" is not supported as a design asset."; 400 "No file was uploaded."
**Notes**: accepted extensions `.png,.jpg,.jpeg,.webp,.gif,.svg`.

## DSN-010 Browse folders in the folder tree
```yaml
priority: P1
surface: [ui]
automation: planned
refs:
  - orkka-ui:packages/ui/src/components/assets/FolderTreePanel.tsx
  - i18n:folders
```
**Preconditions**: assets in root and in `logos` (DSN-008).
**Steps**:
1. Open the page; click "logos" in the folder pane.
2. Click "All items".
3. Click **New folder**, type `icons/small`, confirm.
**Expected**:
- "logos" shows only its assets (and nested ones); each folder row shows its count; "All items" shows the total.
- The new empty folder appears client-side (no request) until something is filed in it.

## DSN-011 Rename a design folder
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/design-assets/folders/rename
  - orkka-api:GET /api/projects/{projectId}/design-assets
  - i18n:folders
```
**Preconditions**: folder `logos` with ≥ 1 asset.
**Steps**:
1. Hover "logos"; click the **Rename folder** icon; type `brand`; press Enter.
2. `GET /api/projects/{p}/design-assets`.
3. `POST …/design-assets/folders/rename {fromPath:"", toPath:"x"}`.
4. `POST …/design-assets/folders/rename {fromPath:"brand", toPath:"brand/sub"}`.
**Expected**:
- `POST …/folders/rename {fromPath:"logos", toPath:"brand"}` → 200; tree shows "brand" and every asset has `folderPath: "brand"`.
- 400 "The folder to rename must not be the root."; 400 "A folder cannot be moved into itself."

## DSN-012 Delete a design folder with confirmation
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/design-assets/folders/delete
  - i18n:design
  - i18n:folders
```
**Preconditions**: folder `brand` with 2 assets; an empty client-side folder `tmp`.
**Steps**:
1. Hover "tmp"; click **Delete folder**.
2. Hover "brand"; click **Delete folder**.
3. In the dialog "Delete folder" read the message; click **Cancel**; repeat and click **Delete folder and assets**.
4. `POST …/design-assets/folders/delete {path:"other", deleteContents:false}` where `other` holds an asset.
**Expected**:
- Empty folder disappears immediately with no confirm (request with `deleteContents:false` → 200).
- Dialog text "Delete “brand” and the 2 assets in it? This cannot be undone."; Cancel keeps everything; confirm sends `{path:"brand", deleteContents:true}` → 200, folder and its cards are gone, selection falls back to "All items".
- API without `deleteContents` on a non-empty folder → 409 `{folderPath, assetCount}`; `path:""` → 400 "The root folder cannot be deleted."

## DSN-013 Approve a pending asset
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/design-assets/{assetId}/approve
  - orkka-api:GET /api/projects/{projectId}/design-assets/{assetId}
  - i18n:design
```
**Preconditions**: a Pending asset (agent-authored via the Designer chat, or an asset moved to Pending by `reject`→`restore` flow is not possible — use an agent asset; otherwise run the API step only against a Pending row).
**Steps**:
1. On the Pending card click **Approve**.
2. `GET /api/projects/{p}/design-assets/{id}`.
**Expected**:
- `POST …/design-assets/{id}/approve` → 200; the card's status badge becomes "Approved"; **Approve**/**Reject** buttons disappear (only Pending cards show them).
- GET returns `status: "Approved"`, `feedback: null`, `reviewedByUserName` = you.
- Any older Approved version of the same lineage is superseded (`feedback: "Superseded by a newer approved version."`, visible in History).

## DSN-014 Reject an asset with feedback, or cancel the rejection
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/design-assets/{assetId}/reject
  - i18n:design
```
**Preconditions**: a Pending asset (see DSN-013).
**Steps**:
1. Click **Reject** on the card — a textarea with placeholder "What should change? (optional)" appears with buttons **Reject** (danger) and **Cancel**.
2. Click **Cancel** — the textarea closes, nothing sent.
3. Click **Reject** again, type `Make the mark bolder`, click the danger **Reject**.
**Expected**:
- `POST …/design-assets/{id}/reject {feedback:"Make the mark bolder"}` → 200 with `status: "Rejected"`, `feedback` echoed.
- Card shows the "Rejected" badge and the feedback text beneath it; the "Rejected" filter count increments.
**Notes**: `feedback` is optional; an empty textarea sends no feedback.

## DSN-015 Restore an older version from History
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/design-assets/{assetId}/restore
  - orkka-api:GET /api/projects/{projectId}/design-assets
  - i18n:design
```
**Preconditions**: a lineage with ≥ 2 versions (iterate an asset in the Designer chat once; hybrid-repo keeps it at $0).
**Steps**:
1. Click **History** on the card; on the older version row click **Restore this version**.
2. `GET /api/projects/{p}/design-assets`.
3. `POST …/design-assets/{latestId}/restore`.
**Expected**:
- `POST …/design-assets/{olderId}/restore` → 200 with `version` = previous latest + 1, `status: "Approved"`, same `rootAssetId` and `folderPath`; button reads "Restoring..." meanwhile.
- The card now shows the restored artwork with badge `v{n}`; History lists the new version on top.
- Restoring the current version → 400 "This is already the current version."
**Notes**: restore is non-destructive (copies content into a new latest version), so there is no confirm step.

## DSN-016 Iterate on an asset in the Designer chat with the asset attached
```yaml
priority: P1
surface: [ui]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/design
  - orkka-ui:/p/[projectId]/team
  - i18n:design
  - i18n:agentChat
```
**Preconditions**: DSN-008 asset; project has a Designer agent (Muse).
**Steps**:
1. Click **Iterate** on the card.
2. Close the drawer; open `/p/{slug}/team?agent={designerAgentId}&tab=chat&asset={assetId}`.
**Expected**:
- The side chat drawer opens (button `aria-label` "Chat with Muse" → panel) with a design-asset chip for the asset in the composer and a "Remove attachment" control; the chat is with the Designer role.
- The team-page deep link opens the Chat tab with the same asset chip attached.
**Notes**: sending the message spends an agent turn — not part of this case.

## DSN-017 Open the version history modal
```yaml
priority: P2
surface: [ui]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/design
  - i18n:design
```
**Preconditions**: lineage with ≥ 2 versions (DSN-015 precondition).
**Steps**:
1. Click **History** on the card.
2. Click **Compare with previous** on the newest row; click it again.
3. Press Escape / click outside.
**Expected**:
- Modal titled "History — {name}" lists versions newest first: `v{n}`, status badge, relative time, prompt text (agent versions), "by {agent}" / "uploaded by {name}", "Approved by {name}" or "Rejected by {name}" when reviewed, and a "View request in chat" link (`/team?agent=…&tab=chat&c=…&m=…`) on agent versions.
- Compare shows two side-by-side previews labelled `v{n-1}` and `v{n}`; clicking again hides them.
- The oldest row has no compare button; the current version has no restore button.

## DSN-018 Move an asset lineage to another folder
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-api:PATCH /api/projects/{projectId}/design-assets/{assetId}/folder
  - orkka-api:GET /api/projects/{projectId}/design-assets/{assetId}
  - i18n:folders
```
**Preconditions**: asset in root; folder `brand` exists.
**Steps**:
1. Click **Move** on the card; in "Move to folder" pick `brand`; click **Move**.
2. `GET /api/projects/{p}/design-assets/{id}`.
**Expected**:
- `PATCH …/design-assets/{id}/folder {folderPath:"brand"}` → 200; card leaves the root view immediately and appears under "brand".
- GET → `folderPath: "brand"`; every version of the lineage carries the new folder.
**Notes**: the dialog also accepts a new folder name typed in the "New folder…" field.

## DSN-019 Download the original and a PNG rasterisation of an SVG
```yaml
priority: P2
surface: [ui, api]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/design-assets/{assetId}
  - i18n:design
```
**Preconditions**: upload `fixture.svg` (DSN-008 flow).
**Steps**:
1. Click **Download** on the card.
2. Click the **PNG** button (title "Download as PNG (for platforms that don't accept SVG)").
3. `GET /api/projects/{p}/design-assets/{id}` → fetch `downloadUrl` and `url` with `curl -I`.
**Expected**:
- Download opens `downloadUrl` in a new tab; the response carries `Content-Disposition: attachment; filename="fixture.svg"`.
- PNG button produces a browser download `fixture.png` (client-side rasterisation, no API call beyond the SVG fetch); on failure a toast "Converting to PNG failed — download the SVG instead."
- `url` responds `Content-Type: image/svg+xml` with no attachment disposition (it renders in `<img>`).
**Notes**: the **PNG** button only renders for `kind: "Svg"`.

## DSN-020 Delete an asset after confirming
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:DELETE /api/projects/{projectId}/design-assets/{assetId}
  - orkka-api:GET /api/projects/{projectId}/design-assets/{assetId}
  - i18n:design
```
**Preconditions**: DSN-008 asset.
**Steps**:
1. Click the trash icon (`aria-label` "Delete") on the card.
2. In the dialog titled with the asset name and message "Delete this asset? This cannot be undone." click **Cancel**.
3. Repeat and click **Delete**.
4. `GET /api/projects/{p}/design-assets/{id}`.
**Expected**:
- Cancel leaves the card in place with no request.
- Confirm sends `DELETE …/design-assets/{id}` → 204; the card disappears and counts update.
- GET → 404.

## DSN-021 Highlight the asset named in the ?a= deep link
```yaml
priority: P2
surface: [ui]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/design
```
**Preconditions**: two lineages; one lineage with ≥ 2 versions.
**Steps**:
1. Open `/p/{slug}/design?a={latestAssetId}`.
2. Open `/p/{slug}/design?a={olderVersionIdOfSameLineage}`.
**Expected**:
- Exactly one card carries the highlight ring (`ring-2 ring-primary-400/60`) — the card of the named lineage.
- Naming an older version still highlights that lineage's latest card.
**Notes**: this is the target of the "View in Design gallery" link from agent chat.

## DSN-022 Inject approved assets into frontend dev-task context
```yaml
priority: P3
modes: [hybrid-repo]
surface: [api]
automation: planned
refs:
  - orkka-api:src/Modules/Orkka.Modules.Execution/Orchestration/TaskOrchestrator.cs
  - orkka-api:src/Modules/Orkka.Modules.Agents.Contracts/IAgentsApi.cs
  - orkka-api:GET /api/tasks/{taskId}/executions
```
**Preconditions**: fixture-web project with one Approved asset named `e2e-logo`; runner online.
**Steps**:
1. Create and submit the standard task ("add a list item '<token>' to the features list") on the runner; wait for Done.
2. `GET /api/tasks/{t}/executions` and read `agentLog` of the Developing run.
**Expected**:
- The developer prompt/log contains an "Approved Design Assets" section listing `e2e-logo` (name + signed URL), and no Pending/Rejected asset.
**Notes**: documentation-level coverage of `ListApprovedDesignAssetsAsync` (newest first, capped by `MaxDesignAssetsInContext`); `SkillSeeder` built-ins also refer to this section.

## DSN-023 Show the empty state and go to the team
```yaml
priority: P2
surface: [ui]
automation: planned
refs:
  - orkka-ui:/p/[projectId]/design
  - orkka-ui:/p/[projectId]/team
  - i18n:design
```
**Preconditions**: fresh project with no design assets.
**Steps**:
1. Open `/p/{slug}/design`.
2. Click **Go to Team**.
**Expected**:
- Empty state "No design assets yet" with description "Chat with Muse on the Team page to create logos, mockups, and other visual assets."; no filter pills.
- Navigation lands on `/p/{slug}/team`.

## DSN-024 Serve inline and attachment blob URLs correctly
```yaml
priority: P3
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/design-assets/{assetId}
  - orkka-api:src/Modules/Orkka.Modules.Agents/Features/DesignAssets/DesignAssetDtos.cs
```
**Preconditions**: one PNG asset.
**Steps**:
1. `GET /api/projects/{p}/design-assets/{id}`.
2. `curl -I {url}` and `curl -I {downloadUrl}`.
3. Load `url` inside an `<img>` and an `<iframe>` in a scratch page.
**Expected**:
- `url` → 200 `image/png`, no `Content-Disposition: attachment`; renders in both `<img>` and `<iframe>`.
- `downloadUrl` → 200 with `Content-Disposition: attachment; filename="{fileName}"`; an iframe pointing at it stays blank (browser downloads instead).
**Notes**: a signed SAS URL stamped with attachment disposition blanks an iframe — the gallery deliberately uses `url` for previews and `downloadUrl` only for the Download button.

## DSN-025 Hide review controls and return 403 without DesignReview
```yaml
priority: P1
surface: [ui, api]
automation: planned
refs:
  - orkka-api:POST /api/projects/{projectId}/design-assets/upload
  - orkka-api:POST /api/projects/{projectId}/design-assets/{assetId}/approve
  - orkka-api:POST /api/projects/{projectId}/design-assets/{assetId}/reject
  - orkka-api:POST /api/projects/{projectId}/design-assets/{assetId}/restore
  - orkka-api:PATCH /api/projects/{projectId}/design-assets/{assetId}/folder
  - orkka-api:DELETE /api/projects/{projectId}/design-assets/{assetId}
  - orkka-api:POST /api/projects/{projectId}/design-assets/folders/rename
  - orkka-api:POST /api/projects/{projectId}/design-assets/folders/delete
  - orkka-api:GET /api/projects/{projectId}/design-assets
```
**Preconditions**: API key minted without `DesignReview`; a member session without DesignReview; one Pending and one Approved asset.
**Steps**:
1. With the key call each write endpoint listed in refs (valid bodies) and `GET …/design-assets`.
2. As the member open `/p/{slug}/design`.
**Expected**:
- Every write → 403; GET → 200.
- UI: no **Upload**, **Approve**, **Reject**, **Move**, trash icon, brand **Edit**, or History **Restore this version**; **Iterate**, **History**, **Download** remain.

## DSN-026 Return 404 for assets of another project
```yaml
priority: P3
surface: [api]
automation: planned
refs:
  - orkka-api:GET /api/projects/{projectId}/design-assets/{assetId}
  - orkka-api:POST /api/projects/{projectId}/design-assets/{assetId}/approve
```
**Preconditions**: two projects A and B; asset `x` in A.
**Steps**:
1. `GET /api/projects/{B}/design-assets/{x}`.
2. `POST /api/projects/{B}/design-assets/{x}/approve`.
3. `GET /api/projects/{A}/design-assets/01ZZZZZZZZZZZZZZZZZZZZZZZZ`.
**Expected**:
- All three → 404.
