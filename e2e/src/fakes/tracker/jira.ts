/**
 * Fake Jira Cloud REST v3 — exactly the endpoints the runner's JiraCloudClient,
 * TrackerAttachmentCache and TrackerWriteBack call, with the response shapes they parse:
 *
 *   GET  /rest/api/3/myself                                  (tracker add / test / health)
 *   GET  /rest/api/3/project/search?maxResults&startAt&orderBy (scopes)
 *   GET  /rest/api/3/project/{key}/statuses                  (statuses for the status map)
 *   POST /rest/api/3/search/jql  {jql, fields, maxResults, nextPageToken?, expand?}  (epics, preview sample, children, intake, tracking by key)
 *   POST /rest/api/3/search/approximate-count  {jql}         (preview count)
 *   GET  /rest/api/3/field                                    ("Acceptance Criteria" lookup)
 *   GET  /rest/api/3/issue/{key}/comment?orderBy=-created&maxResults&expand=renderedBody
 *   GET  /rest/api/3/issue/{key}?fields=status               (write-back: current status)
 *   GET  /rest/api/3/issue/{key}/transitions                 (write-back: available moves)
 *   POST /rest/api/3/issue/{key}/transitions {transition:{id}} → 204
 *   GET  /rest/api/3/attachment/content/{id} → 303 → /__media/jira/{id}  (attachment download, redirect like Atlassian's media service)
 *   GET  /rest/agile/1.0/board?projectKeyOrId&maxResults&startAt  (a project's boards — the ticket source's Board select)
 *   GET  /rest/agile/1.0/board/{id}/configuration            (a board's filter id; 404 when the board is gone)
 *   GET  /rest/api/3/filter/{id}                             (the filter's JQL, AND-ed onto intake; 404 when not shared)
 *
 * Auth: Basic base64(email:token); anything else is 401 like Jira. The JQL evaluator understands
 * the clauses the runner builds (JiraQueries) plus `labels`, `priority`, `status`, `issuetype`
 * in the project's extra clause, and `component` in a board's filter; any other clause answers
 * 400 with Jira's error shape.
 */
import { adfOf, basicOf, jiraDate, parseLiterals, QueryError, send, splitTopLevel, unwrap, type Ctx, type FakeAttachment, type FakeComment } from "./common.js";

export type JiraStatusCategory = "new" | "indeterminate" | "done";

export interface JiraIssue {
  key: string;
  id: string;
  summary: string;
  type: string;
  status: string;
  priority: string | null;
  labels: string[];
  descriptionHtml: string | null;
  acceptanceHtml: string | null;
  parent: string | null;
  components: string[];
  /** "me" = the token's owner (currentUser()); any other string is someone else; null = unassigned. */
  assignee: string | null;
  updated: Date;
  comments: FakeComment[];
  attachments: FakeAttachment[];
}

export type JiraIssueSeed = Partial<Omit<JiraIssue, "key" | "id" | "updated" | "comments" | "attachments" | "components">> & {
  components?: string[];
  key: string;
  summary: string;
  comments?: Omit<FakeComment, "id" | "created">[];
  attachments?: Omit<FakeAttachment, "id">[];
};

export interface FakeJiraOptions {
  email?: string;
  token?: string;
  accountId?: string;
  displayName?: string;
  projects?: { key: string; name: string }[];
  statuses?: { name: string; category: JiraStatusCategory }[];
  /** Id of the "Acceptance Criteria" custom field; null = the site has none. */
  acceptanceFieldId?: string | null;
}

/** An agile board: what the board shows is its saved filter's JQL. */
export interface JiraBoard {
  id: number;
  name: string;
  type: "scrum" | "kanban" | "simple";
  /** The project the board is listed under (`projectKeyOrId`). */
  project: string;
  filterId: string;
}

export interface JiraFilter {
  id: string;
  jql: string;
  /** False = not shared with the token's owner: GET /filter/{id} answers 404 like Jira. */
  shared: boolean;
}

export interface AppliedTransition {
  tracker: "jira" | "ado";
  key: string;
  from: string;
  to: string;
  at: string;
}

const PAGE_TOKEN = (offset: number) => Buffer.from(`offset:${offset}`).toString("base64url");
const PAGE_OFFSET = (token: string | undefined) => (token ? Number(Buffer.from(token, "base64url").toString().replace("offset:", "")) || 0 : 0);

export class FakeJira {
  readonly email: string;
  readonly token: string;
  readonly accountId: string;
  readonly displayName: string;
  readonly projects: { key: string; name: string }[];
  statuses: { name: string; category: JiraStatusCategory }[];
  acceptanceFieldId: string | null;
  readonly issues = new Map<string, JiraIssue>();
  readonly transitions: AppliedTransition[] = [];
  readonly boards = new Map<number, JiraBoard>();
  readonly filters = new Map<string, JiraFilter>();
  private boardSeq = 1;
  private filterSeq = 10040;
  /** Optional workflow: status → statuses reachable from it. Unset = any other status. */
  workflow: Record<string, string[]> | null = null;
  private seq = 10000;
  private commentSeq = 50000;
  private attachmentSeq = 70000;

  constructor(opts: FakeJiraOptions = {}) {
    this.email = opts.email ?? "dev@e2e.test";
    this.token = opts.token ?? "jira-e2e-token-" + Math.random().toString(36).slice(2, 10);
    this.accountId = opts.accountId ?? "557058:e2e-dev";
    this.displayName = opts.displayName ?? "E2E Developer";
    this.projects = opts.projects ?? [{ key: "SHOP", name: "Shop" }];
    this.statuses = opts.statuses ?? [
      { name: "To Do", category: "new" },
      { name: "In Progress", category: "indeterminate" },
      { name: "In Review", category: "indeterminate" },
      { name: "Done", category: "done" },
    ];
    this.acceptanceFieldId = opts.acceptanceFieldId === undefined ? "customfield_10050" : opts.acceptanceFieldId;
  }

  get authorization() {
    return basicOf(this.email, this.token);
  }

  // ── State API for specs ────────────────────────────────────────────────

  addIssue(seed: JiraIssueSeed): JiraIssue {
    const issue: JiraIssue = {
      key: seed.key.toUpperCase(),
      id: String(this.seq++),
      summary: seed.summary,
      type: seed.type ?? "Task",
      status: seed.status ?? this.statuses[0].name,
      priority: seed.priority ?? "Medium",
      labels: seed.labels ?? [],
      descriptionHtml: seed.descriptionHtml ?? null,
      acceptanceHtml: seed.acceptanceHtml ?? null,
      parent: seed.parent?.toUpperCase() ?? null,
      components: seed.components ?? [],
      assignee: seed.assignee === undefined ? (seed.type === "Epic" ? null : "me") : seed.assignee,
      updated: new Date(),
      comments: [],
      attachments: [],
    };
    this.issues.set(issue.key, issue);
    for (const c of seed.comments ?? []) this.addComment(issue.key, c.bodyHtml, c.author);
    for (const a of seed.attachments ?? []) this.addAttachment(issue.key, a);
    return issue;
  }

  addEpic(key: string, summary: string) {
    return this.addIssue({ key, summary, type: "Epic", assignee: null });
  }

  issue(key: string): JiraIssue {
    const i = this.issues.get(key.toUpperCase());
    if (!i) throw new Error(`fake jira: no issue ${key}`);
    return i;
  }

  update(key: string, patch: Partial<Pick<JiraIssue, "summary" | "descriptionHtml" | "acceptanceHtml" | "status" | "priority" | "labels" | "assignee" | "parent" | "type" | "components">>) {
    const i = this.issue(key);
    Object.assign(i, patch);
    i.updated = new Date();
    return i;
  }

  addComment(key: string, bodyHtml: string, author = "Product Owner"): FakeComment {
    const i = this.issue(key);
    // Strictly increasing timestamps: the runner filters comments by "created after".
    const last = i.comments.at(-1)?.created.getTime() ?? 0;
    const c: FakeComment = { id: String(this.commentSeq++), author, created: new Date(Math.max(Date.now(), last + 1000)), bodyHtml };
    i.comments.push(c);
    i.updated = new Date();
    return c;
  }

  addAttachment(key: string, a: Omit<FakeAttachment, "id">): FakeAttachment {
    const i = this.issue(key);
    const att = { ...a, id: String(this.attachmentSeq++) };
    i.attachments.push(att);
    i.updated = new Date();
    return att;
  }

  remove(key: string) {
    this.issues.delete(key.toUpperCase());
  }

  /** A board of `project` (default: the first) showing what `jql` matches. */
  addBoard(seed: { name: string; jql: string; project?: string; type?: JiraBoard["type"]; shared?: boolean }): JiraBoard {
    const filter: JiraFilter = { id: String(this.filterSeq++), jql: seed.jql, shared: seed.shared ?? true };
    this.filters.set(filter.id, filter);
    const board: JiraBoard = {
      id: this.boardSeq++,
      name: seed.name,
      type: seed.type ?? "scrum",
      project: (seed.project ?? this.projects[0].key).toUpperCase(),
      filterId: filter.id,
    };
    this.boards.set(board.id, board);
    return board;
  }

  removeBoard(id: number) {
    this.boards.delete(id);
  }

  // ── HTTP ───────────────────────────────────────────────────────────────

  handles(path: string) {
    return path.startsWith("/rest/api/3/") || path.startsWith("/rest/agile/1.0/") || path.startsWith("/__media/jira/");
  }

  handle(ctx: Ctx) {
    if (ctx.path.startsWith("/__media/jira/")) return this.media(ctx);
    if (!ctx.record.authOk) {
      return send(ctx, 401, { errorMessages: ["Client must be authenticated to access this resource."], errors: {} });
    }
    if (ctx.path.startsWith("/rest/agile/1.0/")) return this.agile(ctx);
    const p = ctx.path.slice("/rest/api/3".length);
    const m = ctx.method;
    let match: RegExpExecArray | null;
    try {
      if (m === "GET" && (match = /^\/filter\/([^/]+)$/.exec(p))) return this.filter(ctx, decodeURIComponent(match[1]));
      if (m === "GET" && p === "/myself") return send(ctx, 200, { accountId: this.accountId, displayName: this.displayName, emailAddress: this.email, active: true });
      if (m === "GET" && p === "/project/search") return this.projectSearch(ctx);
      if (m === "GET" && (match = /^\/project\/([^/]+)\/statuses$/.exec(p))) return this.projectStatuses(ctx, decodeURIComponent(match[1]));
      if (m === "POST" && p === "/search/jql") return this.search(ctx);
      if (m === "POST" && p === "/search/approximate-count") {
        const jql = String((ctx.body as { jql?: string })?.jql ?? "");
        return send(ctx, 200, { count: this.query(jql).length });
      }
      if (m === "GET" && p === "/field") return this.fields(ctx);
      if (m === "GET" && (match = /^\/issue\/([^/]+)\/comment$/.exec(p))) return this.comments(ctx, decodeURIComponent(match[1]));
      if (m === "GET" && (match = /^\/issue\/([^/]+)\/transitions$/.exec(p))) return this.listTransitions(ctx, decodeURIComponent(match[1]));
      if (m === "POST" && (match = /^\/issue\/([^/]+)\/transitions$/.exec(p))) return this.doTransition(ctx, decodeURIComponent(match[1]));
      if (m === "GET" && (match = /^\/issue\/([^/]+)$/.exec(p))) return this.getIssue(ctx, decodeURIComponent(match[1]));
      if (m === "GET" && (match = /^\/attachment\/content\/([^/]+)$/.exec(p))) {
        ctx.record.status = 303;
        ctx.res.writeHead(303, { Location: `${ctx.origin}/__media/jira/${match[1]}` });
        ctx.res.end();
        return;
      }
    } catch (e) {
      if (e instanceof QueryError) return send(ctx, 400, { errorMessages: [e.message], errors: {} });
      throw e;
    }
    return send(ctx, 404, { errorMessages: [`fake jira: no route ${m} ${ctx.path}`], errors: {} });
  }

  private agile(ctx: Ctx) {
    const p = ctx.path.slice("/rest/agile/1.0".length);
    let match: RegExpExecArray | null;
    if (ctx.method === "GET" && p === "/board") {
      const project = (ctx.query.projectKeyOrId ?? "").toUpperCase();
      const max = Number(ctx.query.maxResults ?? 50);
      const start = Number(ctx.query.startAt ?? 0);
      const all = [...this.boards.values()].filter((b) => !project || b.project === project).sort((a, b) => a.id - b.id);
      const values = all.slice(start, start + max).map((b) => ({
        id: b.id,
        self: `${ctx.origin}/rest/agile/1.0/board/${b.id}`,
        name: b.name,
        type: b.type,
        location: { projectKey: b.project, displayName: b.project },
      }));
      return send(ctx, 200, { maxResults: max, startAt: start, total: all.length, isLast: start + values.length >= all.length, values });
    }
    if (ctx.method === "GET" && (match = /^\/board\/(\d+)\/configuration$/.exec(p))) {
      const board = this.boards.get(Number(match[1]));
      if (!board) return send(ctx, 404, { errorMessages: [`Board does not exist or you do not have permission to see it.`], errors: {} });
      return send(ctx, 200, {
        id: board.id,
        name: board.name,
        type: board.type,
        self: `${ctx.origin}/rest/agile/1.0/board/${board.id}/configuration`,
        filter: { id: board.filterId, self: `${ctx.origin}/rest/api/3/filter/${board.filterId}` },
        columnConfig: { columns: this.statuses.map((s) => ({ name: s.name, statuses: [] })) },
      });
    }
    return send(ctx, 404, { errorMessages: [`fake jira: no route ${ctx.method} ${ctx.path}`], errors: {} });
  }

  private filter(ctx: Ctx, id: string) {
    const filter = this.filters.get(id);
    if (!filter || !filter.shared) return send(ctx, 404, { errorMessages: [`The selected filter is not available to you, perhaps it has been deleted or had its permissions changed.`], errors: {} });
    return send(ctx, 200, { id: filter.id, name: `Filter for board ${filter.id}`, jql: filter.jql, self: `${ctx.origin}/rest/api/3/filter/${filter.id}` });
  }

  private projectSearch(ctx: Ctx) {
    const max = Number(ctx.query.maxResults ?? 50);
    const start = Number(ctx.query.startAt ?? 0);
    const sorted = [...this.projects].sort((a, b) => a.name.localeCompare(b.name));
    const values = sorted.slice(start, start + max).map((p, i) => ({ id: String(10000 + i), key: p.key, name: p.name, self: `${ctx.origin}/rest/api/3/project/${p.key}` }));
    return send(ctx, 200, { startAt: start, maxResults: max, total: sorted.length, isLast: start + values.length >= sorted.length, values });
  }

  private projectStatuses(ctx: Ctx, key: string) {
    if (!this.projects.some((p) => p.key.toUpperCase() === key.toUpperCase())) {
      return send(ctx, 404, { errorMessages: [`No project could be found with key '${key}'.`], errors: {} });
    }
    const statuses = this.statuses.map((s, i) => ({ id: String(i + 1), name: s.name, statusCategory: this.category(s.name) }));
    return send(ctx, 200, ["Task", "Bug", "Story", "Epic"].map((name, i) => ({ id: String(i + 1), name, subtask: false, statuses })));
  }

  private fields(ctx: Ctx) {
    const list = [
      { id: "summary", name: "Summary", custom: false },
      { id: "description", name: "Description", custom: false },
      { id: "customfield_10014", name: "Epic Link", custom: true },
    ];
    if (this.acceptanceFieldId) list.push({ id: this.acceptanceFieldId, name: "Acceptance Criteria", custom: true });
    return send(ctx, 200, list);
  }

  private search(ctx: Ctx) {
    const body = (ctx.body ?? {}) as { jql?: string; fields?: string[]; maxResults?: number; nextPageToken?: string; expand?: string };
    const all = this.query(String(body.jql ?? ""));
    const max = Math.min(Number(body.maxResults ?? 50), 5000);
    const offset = PAGE_OFFSET(body.nextPageToken);
    const page = all.slice(offset, offset + max);
    const isLast = offset + page.length >= all.length;
    const fields = body.fields ?? ["summary", "status"];
    const rendered = String(body.expand ?? "").split(",").includes("renderedFields");
    return send(ctx, 200, {
      issues: page.map((i) => this.issueJson(ctx, i, fields, rendered)),
      ...(isLast ? { isLast: true } : { isLast: false, nextPageToken: PAGE_TOKEN(offset + page.length) }),
    });
  }

  private issueJson(ctx: Ctx, i: JiraIssue, fields: string[], rendered: boolean) {
    const want = (f: string) => fields.includes(f) || fields.includes("*all");
    const f: Record<string, unknown> = {};
    if (want("summary")) f.summary = i.summary;
    if (want("issuetype")) f.issuetype = this.typeJson(i.type);
    if (want("status")) f.status = { name: i.status, statusCategory: this.category(i.status) };
    if (want("priority")) f.priority = i.priority ? { name: i.priority } : null;
    if (want("labels")) f.labels = i.labels;
    if (want("description")) f.description = adfOf(i.descriptionHtml);
    if (want("updated")) f.updated = jiraDate(i.updated);
    if (want("parent")) {
      const parent = i.parent ? this.issues.get(i.parent) : undefined;
      f.parent = i.parent ? { key: i.parent, fields: { summary: parent?.summary ?? "", issuetype: this.typeJson(parent?.type ?? "Epic") } } : undefined;
    }
    // A story's sub-tasks, in board order (here: key order), as Jira's `subtasks` field lists them.
    if (want("subtasks")) {
      f.subtasks = this.childrenOf(i.key)
        .filter((c) => this.isSubtask(c.type))
        .map((c) => ({ id: c.id, key: c.key, fields: { summary: c.summary, status: { name: c.status, statusCategory: this.category(c.status) }, issuetype: this.typeJson(c.type) } }));
    }
    if (want("assignee")) f.assignee = this.assigneeJson(i.assignee);
    if (want("components")) f.components = i.components.map((name, n) => ({ id: String(10100 + n), name }));
    if (want("attachment")) {
      f.attachment = i.attachments.map((a) => ({
        id: a.id,
        filename: a.filename,
        size: a.content.length,
        mimeType: a.mimeType,
        content: `${ctx.origin}/rest/api/3/attachment/content/${a.id}`,
      }));
    }
    if (this.acceptanceFieldId && want(this.acceptanceFieldId)) f[this.acceptanceFieldId] = adfOf(i.acceptanceHtml);
    const json: Record<string, unknown> = { id: i.id, key: i.key, self: `${ctx.origin}/rest/api/3/issue/${i.id}`, fields: f };
    if (rendered) {
      const r: Record<string, unknown> = { description: i.descriptionHtml ?? "" };
      if (this.acceptanceFieldId) r[this.acceptanceFieldId] = i.acceptanceHtml ?? null;
      json.renderedFields = r;
    }
    return json;
  }

  private isSubtask(type: string) {
    return /^sub-?task$/i.test(type);
  }

  private typeJson(type: string) {
    const level = type.toLowerCase() === "epic" ? 1 : this.isSubtask(type) ? -1 : 0;
    return { name: type, subtask: level < 0, hierarchyLevel: level };
  }

  private assigneeJson(assignee: string | null) {
    if (assignee === null) return null;
    if (assignee === "me") return { accountId: this.accountId, displayName: this.displayName, emailAddress: this.email, active: true };
    return { accountId: `acc-${assignee.replace(/\s+/g, "-").toLowerCase()}`, displayName: assignee, active: true };
  }

  childrenOf(key: string): JiraIssue[] {
    const k = key.toUpperCase();
    const num = (x: string) => Number(x.split("-")[1] ?? 0);
    return [...this.issues.values()].filter((c) => c.parent === k).sort((a, b) => num(a.key) - num(b.key));
  }

  private comments(ctx: Ctx, key: string) {
    const i = this.issues.get(key.toUpperCase());
    if (!i) return send(ctx, 404, { errorMessages: ["Issue does not exist or you do not have permission to see it."], errors: {} });
    const max = Number(ctx.query.maxResults ?? 50);
    const newestFirst = (ctx.query.orderBy ?? "").startsWith("-");
    const sorted = [...i.comments].sort((a, b) => (newestFirst ? b.created.getTime() - a.created.getTime() : a.created.getTime() - b.created.getTime()));
    const page = sorted.slice(0, max);
    return send(ctx, 200, {
      startAt: 0,
      maxResults: max,
      total: i.comments.length,
      comments: page.map((c) => ({
        id: c.id,
        author: { displayName: c.author, accountId: `acc-${c.author.replace(/\s+/g, "-").toLowerCase()}` },
        created: jiraDate(c.created),
        updated: jiraDate(c.created),
        body: adfOf(c.bodyHtml),
        renderedBody: c.bodyHtml,
      })),
    });
  }

  private getIssue(ctx: Ctx, key: string) {
    const i = this.issues.get(key.toUpperCase());
    if (!i) return send(ctx, 404, { errorMessages: ["Issue does not exist or you do not have permission to see it."], errors: {} });
    const fields = (ctx.query.fields ?? "summary,status").split(",");
    return send(ctx, 200, this.issueJson(ctx, i, fields, false));
  }

  private reachable(from: string): string[] {
    const others = this.statuses.map((s) => s.name).filter((s) => s !== from);
    return this.workflow ? (this.workflow[from] ?? []) : others;
  }

  private listTransitions(ctx: Ctx, key: string) {
    const i = this.issues.get(key.toUpperCase());
    if (!i) return send(ctx, 404, { errorMessages: ["Issue does not exist or you do not have permission to see it."], errors: {} });
    const transitions = this.reachable(i.status).map((to) => ({
      id: String(this.statuses.findIndex((s) => s.name === to) + 11),
      name: `Move to ${to}`,
      to: { name: to, statusCategory: this.category(to) },
      hasScreen: false,
    }));
    return send(ctx, 200, { expand: "transitions", transitions });
  }

  private doTransition(ctx: Ctx, key: string) {
    const i = this.issues.get(key.toUpperCase());
    if (!i) return send(ctx, 404, { errorMessages: ["Issue does not exist or you do not have permission to see it."], errors: {} });
    const id = String((ctx.body as { transition?: { id?: string } })?.transition?.id ?? "");
    const target = this.statuses[Number(id) - 11]?.name;
    if (!target || !this.reachable(i.status).includes(target)) {
      return send(ctx, 400, { errorMessages: [`Transition id '${id}' is not valid for this issue.`], errors: {} });
    }
    this.transitions.push({ tracker: "jira", key: i.key, from: i.status, to: target, at: new Date().toISOString() });
    i.status = target;
    i.updated = new Date();
    return send(ctx, 204);
  }

  private media(ctx: Ctx) {
    const id = ctx.path.slice("/__media/jira/".length);
    for (const i of this.issues.values()) {
      const a = i.attachments.find((x) => x.id === id);
      if (a) return send(ctx, 200, a.content, a.mimeType);
    }
    return send(ctx, 404, "not found", "text/plain");
  }

  private category(status: string) {
    const key = this.statuses.find((s) => s.name.toLowerCase() === status.toLowerCase())?.category ?? "new";
    return { key, name: key === "new" ? "To Do" : key === "done" ? "Done" : "In Progress" };
  }

  // ── JQL ────────────────────────────────────────────────────────────────

  /** Issues matching `jql`, in its ORDER BY order (default: key ascending). */
  query(jql: string): JiraIssue[] {
    const orderAt = jql.search(/\sORDER BY\s/i);
    const where = orderAt >= 0 ? jql.slice(0, orderAt) : jql;
    const order = orderAt >= 0 ? jql.slice(orderAt).replace(/^\s*ORDER BY\s+/i, "").trim() : "key ASC";
    const predicate = this.compile(where.trim());
    const list = [...this.issues.values()].filter(predicate);
    const [field, dir] = order.split(/\s+/);
    const sign = (dir ?? "ASC").toUpperCase() === "DESC" ? -1 : 1;
    const num = (k: string) => Number(k.split("-")[1] ?? 0);
    list.sort((a, b) => {
      if (field.toLowerCase() === "updated") return sign * (a.updated.getTime() - b.updated.getTime());
      const pa = a.key.split("-")[0];
      const pb = b.key.split("-")[0];
      return sign * (pa === pb ? num(a.key) - num(b.key) : pa.localeCompare(pb));
    });
    return list;
  }

  private compile(expr: string): (i: JiraIssue) => boolean {
    if (!expr) return () => true;
    const ors = splitTopLevel(expr, "OR");
    if (ors.length > 1) {
      const ps = ors.map((o) => this.compile(unwrap(o)));
      return (i) => ps.some((p) => p(i));
    }
    const ands = splitTopLevel(expr, "AND");
    if (ands.length > 1) {
      const ps = ands.map((a) => this.compile(unwrap(a)));
      return (i) => ps.every((p) => p(i));
    }
    return this.atom(unwrap(expr));
  }

  private atom(term: string): (i: JiraIssue) => boolean {
    const t = term.trim();
    let m: RegExpExecArray | null;
    const one = (s: string) => parseLiterals(s)[0] ?? "";
    if ((m = /^project\s*=\s*(.+)$/i.exec(t))) {
      const key = one(m[1]).toUpperCase();
      return (i) => i.key.startsWith(`${key}-`);
    }
    if ((m = /^issuetype\s*(=|!=)\s*(.+)$/i.exec(t))) {
      const v = one(m[2]).toLowerCase();
      const neg = m[1] === "!=";
      return (i) => (i.type.toLowerCase() === v) !== neg;
    }
    if ((m = /^key\s*=\s*(.+)$/i.exec(t))) {
      const v = one(m[1]).toUpperCase();
      if (!this.issues.has(v)) throw new QueryError(`An issue with key '${v}' does not exist for field 'key'.`);
      return (i) => i.key === v;
    }
    if ((m = /^key\s+in\s*\((.*)\)$/i.exec(t))) {
      const keys = parseLiterals(m[1]).map((k) => k.toUpperCase());
      // Like Jira: one unknown key fails the whole query.
      const missing = keys.find((k) => !this.issues.has(k));
      if (missing) throw new QueryError(`An issue with key '${missing}' does not exist for field 'key'.`);
      const set = new Set(keys);
      return (i) => set.has(i.key);
    }
    if ((m = /^summary\s*~\s*(.+)$/i.exec(t))) {
      const v = one(m[1]).replace(/\*$/, "").toLowerCase().trim();
      const terms = v.split(/\s+/).filter(Boolean);
      return (i) => terms.every((term) => i.summary.toLowerCase().split(/[^a-z0-9]+/).some((w) => w.startsWith(term)));
    }
    if ((m = /^parent\s+in\s*\((.*)\)$/i.exec(t))) {
      const keys = new Set(parseLiterals(m[1]).map((k) => k.toUpperCase()));
      return (i) => i.parent !== null && keys.has(i.parent);
    }
    if (/^assignee\s*=\s*currentUser\(\)$/i.test(t)) return (i) => i.assignee === "me";
    if (/^assignee\s+is\s+EMPTY$/i.test(t)) return (i) => i.assignee === null;
    if ((m = /^statusCategory\s*(=|!=)\s*(.+)$/i.exec(t))) {
      const v = one(m[2]).toLowerCase();
      const want = v === "done" ? "done" : v === "to do" || v === "new" ? "new" : "indeterminate";
      const neg = m[1] === "!=";
      return (i) => (this.category(i.status).key === want) !== neg;
    }
    if ((m = /^status\s*(=|!=)\s*(.+)$/i.exec(t))) {
      const v = one(m[2]).toLowerCase();
      const neg = m[1] === "!=";
      return (i) => (i.status.toLowerCase() === v) !== neg;
    }
    if ((m = /^status\s+(not\s+in|in)\s*\((.*)\)$/i.exec(t))) {
      const vs = new Set(parseLiterals(m[2]).map((x) => x.toLowerCase()));
      const neg = /not/i.test(m[1]);
      return (i) => vs.has(i.status.toLowerCase()) !== neg;
    }
    if ((m = /^labels\s*(=|!=)\s*(.+)$/i.exec(t))) {
      const v = one(m[2]);
      const neg = m[1] === "!=";
      return (i) => i.labels.includes(v) !== neg;
    }
    if ((m = /^labels\s+(not\s+in|in)\s*\((.*)\)$/i.exec(t))) {
      const vs = parseLiterals(m[2]);
      const neg = /not/i.test(m[1]);
      return (i) => vs.some((v) => i.labels.includes(v)) !== neg;
    }
    if ((m = /^component\s*(=|!=)\s*(.+)$/i.exec(t))) {
      const v = one(m[2]).toLowerCase();
      const neg = m[1] === "!=";
      return (i) => i.components.some((c) => c.toLowerCase() === v) !== neg;
    }
    if ((m = /^component\s+(not\s+in|in)\s*\((.*)\)$/i.exec(t))) {
      const vs = parseLiterals(m[2]).map((x) => x.toLowerCase());
      const neg = /not/i.test(m[1]);
      return (i) => i.components.some((c) => vs.includes(c.toLowerCase())) !== neg;
    }
    if ((m = /^priority\s*(=|!=)\s*(.+)$/i.exec(t))) {
      const v = one(m[2]).toLowerCase();
      const neg = m[1] === "!=";
      return (i) => ((i.priority ?? "").toLowerCase() === v) !== neg;
    }
    throw new QueryError(`Error in the JQL Query: the fake tracker does not understand '${t}' (supported: project, issuetype, key, key in, summary ~, parent in, assignee = currentUser(), statusCategory, status, labels, component, priority).`);
  }
}
