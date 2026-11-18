/**
 * Fake Azure DevOps Services REST 7.1 — exactly what the runner's AzureDevOpsClient,
 * TrackerAttachmentCache and TrackerWriteBack call. Served under `/{org}` so the runner's site
 * URL is `http://127.0.0.1:<port>/{org}` (in place of https://dev.azure.com/{org}):
 *
 *   GET   /{org}/_apis/connectionData                                   (tracker add / test)
 *   GET   /{org}/_apis/projects?$top=200&api-version=7.1                (scopes)
 *   POST  /{org}/{project}/_apis/wit/wiql?[$top=N&]api-version=7.1 {query}
 *           FROM WorkItems     → {queryType:"flat", workItems:[{id,url}]}
 *           FROM WorkItemLinks → {queryType:"tree"|"oneHop", workItemRelations:[{rel,source,target}]}
 *   POST  /{org}/_apis/wit/workitemsbatch?api-version=7.1 {ids, fields? | $expand:"Relations", errorPolicy}
 *   GET   /{org}/{project}/_apis/wit/workitemtypes?api-version=7.1
 *   GET   /{org}/{project}/_apis/wit/workitemtypecategories?api-version=7.1
 *   GET   /{org}/{project}/_apis/wit/workItems/{id}/comments?$top&order=desc&$expand=renderedText&api-version=7.1-preview.4
 *   GET   /{org}/_apis/wit/workitems/{id}?fields=System.State&api-version=7.1   (write-back)
 *   PATCH /{org}/_apis/wit/workitems/{id}?api-version=7.1  json-patch [{op:"add",path:"/fields/System.State",value}]
 *   GET   /{org}/_apis/wit/attachments/{guid}?fileName=…                        (attachment download)
 *
 * Auth: Basic base64(":" + pat). A wrong PAT gets 203 + an HTML sign-in page, which is what the
 * real service does and what the runner reads as "bad token".
 */
import { basicOf, parseLiterals, QueryError, send, splitTopLevel, unwrap, type Ctx, type FakeAttachment, type FakeComment } from "./common.js";
import type { AppliedTransition } from "./jira.js";

export type AdoCategory = "Proposed" | "InProgress" | "Resolved" | "Completed" | "Removed";

export interface AdoWorkItem {
  id: number;
  project: string;
  type: string;
  title: string;
  state: string;
  parentId: number | null;
  assignedToMe: boolean;
  descriptionHtml: string | null;
  acceptanceHtml: string | null;
  reproHtml: string | null;
  tags: string[];
  priority: number | null;
  changed: Date;
  rev: number;
  comments: FakeComment[];
  attachments: (FakeAttachment & { guid: string })[];
}

export type AdoWorkItemSeed = Partial<Omit<AdoWorkItem, "id" | "changed" | "rev" | "comments" | "attachments">> & {
  id?: number;
  title: string;
  type: string;
  comments?: Omit<FakeComment, "id" | "created">[];
  attachments?: Omit<FakeAttachment, "id">[];
};

export interface FakeAdoOptions {
  org?: string;
  pat?: string;
  projects?: string[];
  userId?: string;
  displayName?: string;
}

/** The Agile process: states (with categories) per work item type. */
const AGILE: Record<string, { name: string; category: AdoCategory }[]> = {
  Epic: [
    { name: "New", category: "Proposed" },
    { name: "Active", category: "InProgress" },
    { name: "Resolved", category: "Resolved" },
    { name: "Closed", category: "Completed" },
    { name: "Removed", category: "Removed" },
  ],
  Feature: [
    { name: "New", category: "Proposed" },
    { name: "Active", category: "InProgress" },
    { name: "Resolved", category: "Resolved" },
    { name: "Closed", category: "Completed" },
    { name: "Removed", category: "Removed" },
  ],
  "User Story": [
    { name: "New", category: "Proposed" },
    { name: "Active", category: "InProgress" },
    { name: "Resolved", category: "Resolved" },
    { name: "Closed", category: "Completed" },
    { name: "Removed", category: "Removed" },
  ],
  Task: [
    { name: "New", category: "Proposed" },
    { name: "Active", category: "InProgress" },
    { name: "Closed", category: "Completed" },
    { name: "Removed", category: "Removed" },
  ],
  Bug: [
    { name: "New", category: "Proposed" },
    { name: "Active", category: "InProgress" },
    { name: "Resolved", category: "Resolved" },
    { name: "Closed", category: "Completed" },
  ],
};
const CATEGORIES: { referenceName: string; name: string; types: string[] }[] = [
  { referenceName: "Microsoft.EpicCategory", name: "Epic Category", types: ["Epic"] },
  { referenceName: "Microsoft.FeatureCategory", name: "Feature Category", types: ["Feature"] },
  { referenceName: "Microsoft.RequirementCategory", name: "Requirement Category", types: ["User Story"] },
  { referenceName: "Microsoft.TaskCategory", name: "Task Category", types: ["Task"] },
  { referenceName: "Microsoft.BugCategory", name: "Bug Category", types: ["Bug"] },
];
const HIERARCHY_FORWARD = "System.LinkTypes.Hierarchy-Forward";
const HIERARCHY_REVERSE = "System.LinkTypes.Hierarchy-Reverse";

export class FakeAdo {
  readonly org: string;
  readonly pat: string;
  readonly projects: string[];
  readonly userId: string;
  readonly displayName: string;
  readonly items = new Map<number, AdoWorkItem>();
  readonly transitions: AppliedTransition[] = [];
  private nextId = 100;
  private commentSeq = 9000;

  constructor(opts: FakeAdoOptions = {}) {
    this.org = opts.org ?? "e2eorg";
    this.pat = opts.pat ?? "ado-e2e-pat-" + Math.random().toString(36).slice(2, 10);
    this.projects = opts.projects ?? ["Shop"];
    this.userId = opts.userId ?? "6f1c3a5e-0000-4000-8000-00000000e2e0";
    this.displayName = opts.displayName ?? "E2E Developer";
  }

  get authorization() {
    return basicOf("", this.pat);
  }

  // ── State API for specs ────────────────────────────────────────────────

  add(seed: AdoWorkItemSeed): AdoWorkItem {
    const id = seed.id ?? this.nextId++;
    if (seed.id && seed.id >= this.nextId) this.nextId = seed.id + 1;
    const states = AGILE[seed.type];
    if (!states) throw new Error(`fake ado: unknown work item type ${seed.type}`);
    const item: AdoWorkItem = {
      id,
      project: seed.project ?? this.projects[0],
      type: seed.type,
      title: seed.title,
      state: seed.state ?? states[0].name,
      parentId: seed.parentId ?? null,
      assignedToMe: seed.assignedToMe ?? !["Epic", "Feature"].includes(seed.type),
      descriptionHtml: seed.descriptionHtml ?? null,
      acceptanceHtml: seed.acceptanceHtml ?? null,
      reproHtml: seed.reproHtml ?? null,
      tags: seed.tags ?? [],
      priority: seed.priority ?? 2,
      changed: new Date(),
      rev: 1,
      comments: [],
      attachments: [],
    };
    this.items.set(id, item);
    for (const c of seed.comments ?? []) this.addComment(id, c.bodyHtml, c.author);
    for (const a of seed.attachments ?? []) this.addAttachment(id, a);
    return item;
  }

  item(id: number): AdoWorkItem {
    const i = this.items.get(id);
    if (!i) throw new Error(`fake ado: no work item ${id}`);
    return i;
  }

  update(id: number, patch: Partial<Pick<AdoWorkItem, "title" | "state" | "descriptionHtml" | "acceptanceHtml" | "reproHtml" | "tags" | "priority" | "assignedToMe" | "parentId">>) {
    const i = this.item(id);
    Object.assign(i, patch);
    i.changed = new Date();
    i.rev++;
    return i;
  }

  addComment(id: number, bodyHtml: string, author = "Product Owner"): FakeComment {
    const i = this.item(id);
    const last = i.comments.at(-1)?.created.getTime() ?? 0;
    const c: FakeComment = { id: String(this.commentSeq++), author, created: new Date(Math.max(Date.now(), last + 1000)), bodyHtml };
    i.comments.push(c);
    return c;
  }

  addAttachment(id: number, a: Omit<FakeAttachment, "id">) {
    const i = this.item(id);
    const guid = crypto.randomUUID();
    const att = { ...a, id: guid, guid };
    i.attachments.push(att);
    i.changed = new Date();
    return att;
  }

  remove(id: number) {
    this.items.delete(id);
  }

  categoryOf(type: string, state: string): AdoCategory | undefined {
    return AGILE[type]?.find((s) => s.name.toLowerCase() === state.toLowerCase())?.category;
  }

  // ── HTTP ───────────────────────────────────────────────────────────────

  handles(segments: string[]) {
    return segments[0]?.toLowerCase() === this.org.toLowerCase();
  }

  handle(ctx: Ctx) {
    const rest = ctx.segments.slice(1);
    const base = `${ctx.origin}/${this.org}`;
    // Attachments are fetched like any API call (with the PAT) — check auth for them too.
    if (!ctx.record.authOk) {
      return send(ctx, 203, "<html><head><title>Azure DevOps Services | Sign In</title></head><body>Sign in</body></html>", "text/html; charset=utf-8");
    }
    try {
      // /{org}/_apis/…
      if (rest[0] === "_apis") {
        const p = rest.slice(1).join("/");
        if (ctx.method === "GET" && p === "connectionData") {
          return send(ctx, 200, {
            authenticatedUser: { id: this.userId, descriptor: `aad.${this.userId}`, providerDisplayName: this.displayName, isActive: true },
            authorizedUser: { id: this.userId, providerDisplayName: this.displayName },
            instanceId: "00000000-0000-0000-0000-00000000e2e0",
          });
        }
        if (ctx.method === "GET" && p === "projects") {
          const value = this.projects.map((name, i) => ({ id: `proj-${i}`, name, state: "wellFormed", visibility: "private" }));
          return send(ctx, 200, { count: value.length, value });
        }
        if (ctx.method === "POST" && p === "wit/workitemsbatch") return this.batch(ctx, base);
        let m: RegExpExecArray | null;
        if ((m = /^wit\/workitems\/(\d+)$/i.exec(p))) {
          if (ctx.method === "GET") return this.getItem(ctx, Number(m[1]), base);
          if (ctx.method === "PATCH") return this.patchItem(ctx, Number(m[1]), base);
        }
        if (ctx.method === "GET" && (m = /^wit\/attachments\/([^/]+)$/i.exec(p))) return this.attachment(ctx, m[1]);
      }
      // /{org}/{project}/_apis/…
      if (rest.length >= 2 && rest[1] === "_apis") {
        const project = decodeURIComponent(rest[0]);
        if (!this.projects.some((x) => x.toLowerCase() === project.toLowerCase())) {
          return send(ctx, 404, { message: `TF200016: The following project does not exist: ${project}. Verify that the name of the project is correct and that the project exists on the specified Azure DevOps Server.` });
        }
        const p = rest.slice(2).join("/");
        let m: RegExpExecArray | null;
        if (ctx.method === "POST" && p.toLowerCase() === "wit/wiql") return this.wiql(ctx, project, base);
        if (ctx.method === "GET" && p.toLowerCase() === "wit/workitemtypes") return this.types(ctx);
        if (ctx.method === "GET" && p.toLowerCase() === "wit/workitemtypecategories") {
          return send(ctx, 200, { count: CATEGORIES.length, value: CATEGORIES.map((c) => ({ referenceName: c.referenceName, name: c.name, workItemTypes: c.types.map((t) => ({ name: t })) })) });
        }
        if (ctx.method === "GET" && (m = /^wit\/workItems\/(\d+)\/comments$/i.exec(p))) return this.comments(ctx, Number(m[1]));
      }
    } catch (e) {
      if (e instanceof QueryError) return send(ctx, 400, { message: e.message, typeKey: "WorkItemTrackingQueryResultSizeLimitExceededException" });
      throw e;
    }
    return send(ctx, 404, { message: `fake ado: no route ${ctx.method} ${ctx.path}` });
  }

  private types(ctx: Ctx) {
    const value = Object.entries(AGILE).map(([name, states]) => ({
      name,
      referenceName: `Microsoft.VSTS.WorkItemTypes.${name.replace(/\s/g, "")}`,
      isDisabled: false,
      states: states.map((s) => ({ name: s.name, color: "b2b2b2", category: s.category })),
    }));
    return send(ctx, 200, { count: value.length, value });
  }

  private itemJson(i: AdoWorkItem, base: string, fields: string[] | null, relations: boolean) {
    const all: Record<string, unknown> = {
      "System.Id": i.id,
      "System.TeamProject": i.project,
      "System.WorkItemType": i.type,
      "System.Title": i.title,
      "System.State": i.state,
      "System.AssignedTo": i.assignedToMe ? { displayName: this.displayName, id: this.userId } : undefined,
      "System.Description": i.descriptionHtml ?? undefined,
      "Microsoft.VSTS.Common.AcceptanceCriteria": i.acceptanceHtml ?? undefined,
      "Microsoft.VSTS.TCM.ReproSteps": i.reproHtml ?? undefined,
      "System.Tags": i.tags.length ? i.tags.join("; ") : undefined,
      "Microsoft.VSTS.Common.Priority": i.priority ?? undefined,
      "System.ChangedDate": i.changed.toISOString(),
      "System.Parent": i.parentId ?? undefined,
    };
    const f: Record<string, unknown> = {};
    for (const [k, v] of Object.entries(all)) if (v !== undefined && (!fields || fields.includes(k))) f[k] = v;
    const json: Record<string, unknown> = { id: i.id, rev: i.rev, fields: f, url: `${base}/_apis/wit/workItems/${i.id}` };
    if (relations) {
      const rels: unknown[] = [];
      if (i.parentId) rels.push({ rel: HIERARCHY_REVERSE, url: `${base}/_apis/wit/workItems/${i.parentId}`, attributes: { isLocked: false, name: "Parent" } });
      for (const child of this.items.values()) {
        if (child.parentId === i.id) rels.push({ rel: HIERARCHY_FORWARD, url: `${base}/_apis/wit/workItems/${child.id}`, attributes: { isLocked: false, name: "Child" } });
      }
      for (const a of i.attachments) {
        rels.push({ rel: "AttachedFile", url: `${base}/_apis/wit/attachments/${a.guid}?fileName=${encodeURIComponent(a.filename)}`, attributes: { name: a.filename, resourceSize: a.content.length } });
      }
      json.relations = rels;
    }
    return json;
  }

  private batch(ctx: Ctx, base: string) {
    const body = (ctx.body ?? {}) as { ids?: number[]; fields?: string[]; $expand?: string; errorPolicy?: string };
    if (body.fields && body.$expand && body.$expand.toLowerCase() !== "none") {
      return send(ctx, 400, { message: "The expand parameter can not be used with the fields parameter." });
    }
    const ids = body.ids ?? [];
    if (ids.length > 200) return send(ctx, 400, { message: "VS402337: The number of work items requested exceeds the limit of 200." });
    const relations = (body.$expand ?? "").toLowerCase() === "relations" || (body.$expand ?? "").toLowerCase() === "all";
    const value = ids.map((id) => this.items.get(id)).filter((i): i is AdoWorkItem => Boolean(i)).map((i) => this.itemJson(i, base, body.fields ?? null, relations));
    if (body.errorPolicy !== "Omit" && value.length !== ids.length) return send(ctx, 404, { message: "TF401232: Work item does not exist, or you do not have permissions to read it." });
    return send(ctx, 200, { count: value.length, value });
  }

  private getItem(ctx: Ctx, id: number, base: string) {
    const i = this.items.get(id);
    if (!i) return send(ctx, 404, { message: `TF401232: Work item ${id} does not exist, or you do not have permissions to read it.` });
    const fields = ctx.query.fields ? ctx.query.fields.split(",") : null;
    return send(ctx, 200, this.itemJson(i, base, fields, false));
  }

  private patchItem(ctx: Ctx, id: number, base: string) {
    const i = this.items.get(id);
    if (!i) return send(ctx, 404, { message: `TF401232: Work item ${id} does not exist, or you do not have permissions to read it.` });
    const ct = String(ctx.req.headers["content-type"] ?? "");
    if (!ct.includes("application/json-patch+json")) return send(ctx, 415, { message: "The request content type must be application/json-patch+json." });
    const ops = (Array.isArray(ctx.body) ? ctx.body : []) as { op: string; path: string; value: unknown }[];
    for (const op of ops) {
      if (op.path === "/fields/System.State") {
        const target = String(op.value);
        if (!this.categoryOf(i.type, target)) {
          return send(ctx, 400, { message: `TF401320: Rule Error for field State. Error code: Required, HasValues, LimitedToValues, AllowsOldValue, InvalidListValue. '${target}' is not a valid state for ${i.type}.` });
        }
        this.transitions.push({ tracker: "ado", key: String(i.id), from: i.state, to: target, at: new Date().toISOString() });
        i.state = target;
      } else {
        return send(ctx, 400, { message: `fake ado: only /fields/System.State may be patched (got ${op.path})` });
      }
    }
    i.rev++;
    i.changed = new Date();
    return send(ctx, 200, this.itemJson(i, base, null, false));
  }

  private comments(ctx: Ctx, id: number) {
    const i = this.items.get(id);
    if (!i) return send(ctx, 404, { message: `TF401232: Work item ${id} does not exist, or you do not have permissions to read it.` });
    const top = Number(ctx.query.$top ?? 200);
    const desc = (ctx.query.order ?? "asc").toLowerCase() === "desc";
    const sorted = [...i.comments].sort((a, b) => (desc ? b.created.getTime() - a.created.getTime() : a.created.getTime() - b.created.getTime()));
    const page = sorted.slice(0, top);
    return send(ctx, 200, {
      totalCount: i.comments.length,
      count: page.length,
      comments: page.map((c) => ({
        workItemId: id,
        id: Number(c.id),
        version: 1,
        text: c.bodyHtml,
        renderedText: c.bodyHtml,
        createdBy: { displayName: c.author },
        createdDate: c.created.toISOString(),
        isDeleted: Boolean(c.deleted),
      })),
    });
  }

  private attachment(ctx: Ctx, guid: string) {
    for (const i of this.items.values()) {
      const a = i.attachments.find((x) => x.guid === guid);
      if (a) return send(ctx, 200, a.content, a.mimeType || "application/octet-stream");
    }
    return send(ctx, 404, { message: "TF401349: The attachment does not exist." });
  }

  // ── WIQL ───────────────────────────────────────────────────────────────

  private wiql(ctx: Ctx, project: string, base: string) {
    const query = String((ctx.body as { query?: string })?.query ?? "");
    if (!query) return send(ctx, 400, { message: "The query text is empty." });
    const top = ctx.query.$top ? Number(ctx.query.$top) : undefined;
    if (/\bFROM\s+WorkItemLinks\b/i.test(query)) return send(ctx, 200, this.linkQuery(query, base));
    return send(ctx, 200, {
      queryType: "flat",
      asOf: new Date().toISOString(),
      workItems: this.flatQuery(query, project)
        .slice(0, top ?? Infinity)
        .map((i) => ({ id: i.id, url: `${base}/_apis/wit/workItems/${i.id}` })),
    });
  }

  private linkQuery(query: string, base: string) {
    const src = /\[Source\]\.\[System\.Id\]\s+IN\s*\(([^)]*)\)/i.exec(query);
    if (!src) throw new QueryError("TF51005: the fake tracker needs [Source].[System.Id] IN (…) in a link query.");
    if (!new RegExp(`\\[System\\.Links\\.LinkType\\]\\s*=\\s*'${HIERARCHY_FORWARD.replace(/\./g, "\\.")}'`, "i").test(query)) {
      throw new QueryError("TF51005: the fake tracker only answers hierarchy (Hierarchy-Forward) link queries.");
    }
    const recursive = /MODE\s*\(\s*Recursive\s*\)/i.test(query);
    const roots = parseLiterals(src[1]).map(Number).filter((n) => this.items.has(n));
    const relations: unknown[] = [];
    const visit = (parent: number) => {
      for (const child of this.items.values()) {
        if (child.parentId !== parent) continue;
        relations.push({ rel: HIERARCHY_FORWARD, source: { id: parent, url: `${base}/_apis/wit/workItems/${parent}` }, target: { id: child.id, url: `${base}/_apis/wit/workItems/${child.id}` } });
        if (recursive) visit(child.id);
      }
    };
    for (const r of roots) {
      relations.push({ rel: null, source: null, target: { id: r, url: `${base}/_apis/wit/workItems/${r}` } });
      visit(r);
    }
    return { queryType: recursive ? "tree" : "oneHop", queryResultType: "workItemLink", asOf: new Date().toISOString(), workItemRelations: relations };
  }

  /** Work items matching a flat WIQL query, in its ORDER BY order. */
  flatQuery(query: string, project: string): AdoWorkItem[] {
    const where = /\bWHERE\b([\s\S]*?)(\bORDER BY\b[\s\S]*)?$/i.exec(query);
    const predicate = this.compile((where?.[1] ?? "").trim(), project);
    const order = (where?.[2] ?? "").replace(/ORDER BY/i, "").trim();
    const list = [...this.items.values()].filter(predicate);
    if (/ChangedDate\]\s*DESC/i.test(order)) list.sort((a, b) => b.changed.getTime() - a.changed.getTime());
    else list.sort((a, b) => a.id - b.id);
    return list;
  }

  private compile(expr: string, project: string): (i: AdoWorkItem) => boolean {
    if (!expr) return () => true;
    const ors = splitTopLevel(expr, "OR");
    if (ors.length > 1) {
      const ps = ors.map((o) => this.compile(unwrap(o), project));
      return (i) => ps.some((p) => p(i));
    }
    const ands = splitTopLevel(expr, "AND");
    if (ands.length > 1) {
      const ps = ands.map((a) => this.compile(unwrap(a), project));
      return (i) => ps.every((p) => p(i));
    }
    return this.atom(unwrap(expr), project);
  }

  private atom(term: string, project: string): (i: AdoWorkItem) => boolean {
    const t = term.trim();
    let m: RegExpExecArray | null;
    const one = (s: string) => parseLiterals(s)[0] ?? "";
    if (/^\[System\.TeamProject\]\s*=\s*@project$/i.test(t)) return (i) => i.project.toLowerCase() === project.toLowerCase();
    if ((m = /^\[System\.WorkItemType\]\s*(=|<>)\s*(.+)$/i.exec(t))) {
      const v = one(m[2]).toLowerCase();
      const neg = m[1] === "<>";
      return (i) => (i.type.toLowerCase() === v) !== neg;
    }
    // Intake v2 skips containers (epics, features) by type.
    if ((m = /^\[System\.WorkItemType\]\s+(NOT\s+IN|IN)\s*\((.*)\)$/i.exec(t))) {
      const vs = new Set(parseLiterals(m[2]).map((x) => x.toLowerCase()));
      const neg = /NOT/i.test(m[1]);
      return (i) => vs.has(i.type.toLowerCase()) !== neg;
    }
    if ((m = /^\[System\.Id\]\s*=\s*(\d+)$/i.exec(t))) {
      const v = Number(m[1]);
      return (i) => i.id === v;
    }
    if ((m = /^\[System\.Id\]\s+IN\s*\((.*)\)$/i.exec(t))) {
      const ids = new Set(parseLiterals(m[1]).map(Number));
      return (i) => ids.has(i.id);
    }
    if ((m = /^\[System\.Title\]\s+CONTAINS\s+(.+)$/i.exec(t))) {
      const v = one(m[1]).toLowerCase();
      return (i) => i.title.toLowerCase().includes(v);
    }
    if (/^\[System\.AssignedTo\]\s*=\s*@Me$/i.test(t)) return (i) => i.assignedToMe;
    if ((m = /^\[System\.State\]\s+(NOT\s+IN|IN)\s*\((.*)\)$/i.exec(t))) {
      const vs = new Set(parseLiterals(m[2]).map((x) => x.toLowerCase()));
      const neg = /NOT/i.test(m[1]);
      return (i) => vs.has(i.state.toLowerCase()) !== neg;
    }
    if ((m = /^\[System\.State\]\s*(=|<>)\s*(.+)$/i.exec(t))) {
      const v = one(m[2]).toLowerCase();
      const neg = m[1] === "<>";
      return (i) => (i.state.toLowerCase() === v) !== neg;
    }
    if ((m = /^\[System\.Tags\]\s+(NOT\s+CONTAINS|CONTAINS)\s+(.+)$/i.exec(t))) {
      const v = one(m[2]).toLowerCase();
      const neg = /NOT/i.test(m[1]);
      return (i) => i.tags.some((tag) => tag.toLowerCase() === v) !== neg;
    }
    if ((m = /^\[Microsoft\.VSTS\.Common\.Priority\]\s*(=|<>|<=|>=|<|>)\s*(\d+)$/i.exec(t))) {
      const v = Number(m[2]);
      const op = m[1];
      return (i) => {
        const p = i.priority ?? 0;
        return op === "=" ? p === v : op === "<>" ? p !== v : op === "<=" ? p <= v : op === ">=" ? p >= v : op === "<" ? p < v : p > v;
      };
    }
    throw new QueryError(`TF51005: The query references a field that the fake tracker does not support: ${t}`);
  }
}
