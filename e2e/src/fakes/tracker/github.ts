/**
 * Fake GitHub GraphQL API (Projects v2 + issues) — exactly the operations the runner's
 * GitHubProjectsClient sends. Served under `/github` so the runner's site URL is
 * `http://127.0.0.1:<port>/github`: with ORKKA_TRACKER_ALLOW_INSECURE_SITE=1 a loopback site serves
 * its API at its own root, so every call is `POST /github/graphql {query, variables}`.
 *
 * Operations, recognised by their query text:
 *   query { viewer { login name } }                                    (tracker add / test; X-OAuth-Scopes)
 *   viewer { … organizations(first: 100, after: $after) … }            (scopes = owners)
 *   repositoryOwner(login: $login) { … projectsV2(first: 100 …) }      (boards = projects)
 *   node(id: $id) { ... on ProjectV2 { id title closed fields(…) } }   (status field + options)
 *   node(id: $id) { ... on ProjectV2 { items(first: N, after: $after) } } + viewer   (intake, paged)
 *   iN: repository(owner: "o", name: "r") { issue(number: n) { … projectItems … } }  (tracking by key;
 *        unknown repo/issue → null + a NOT_FOUND error, like GitHub)
 *   node(id: $id) { ... on Issue { comments(last: $n) } }              (comments)
 *   node(id: $id) { ... on Issue { projectItems(first: 50, includeArchived: true) } }  (write-back lookup)
 *   mutation addProjectV2ItemById / updateProjectV2ItemFieldValue      (write-back)
 *
 * Auth: `Authorization: Bearer <token>` — the fake's token, or `gho_stub_<login>`, which is what the
 * stub gh prints for `gh auth token` (so a "use my gh login" connection signs in as the same
 * developer); anything else → 401 "Bad credentials". Knobs for the
 * runner's error paths: `scopes` (the X-OAuth-Scopes header; drop "project" for the missing-scope
 * message, null for a fine-grained token that sends none), `ssoOrgs` (any query touching one of
 * these owners answers a SAML FORBIDDEN error), `rateLimitNext` (the next request is a secondary
 * rate limit: 403 + Retry-After).
 */
import { send, type Ctx } from "./common.js";

export type GhItemType = "ISSUE" | "DRAFT_ISSUE" | "PULL_REQUEST";

export interface GhComment {
  id: string;
  author: string;
  createdAt: Date;
  body: string;
}

export interface GhIssue {
  id: string;
  /** owner/name */
  repo: string;
  number: number;
  title: string;
  /** Markdown, as GitHub stores it. */
  body: string;
  state: "OPEN" | "CLOSED";
  assignees: string[];
  labels: string[];
  issueType: string | null;
  /** Sub-issue keys (owner/repo#n), in GitHub's order. */
  subIssues: string[];
  /** Issues this one is blocked by (owner/repo#n) — GitHub's issue dependencies. */
  blockedBy: string[];
  comments: GhComment[];
  updatedAt: Date;
}

export interface GhField {
  id: string;
  name: string;
  options: { id: string; name: string }[];
}

export interface GhProject {
  id: string;
  owner: string;
  number: number;
  title: string;
  closed: boolean;
  fields: GhField[];
}

export interface GhItem {
  id: string;
  projectId: string;
  type: GhItemType;
  /** ISSUE only: the issue's key. */
  issueKey?: string;
  /** Field name → option name. */
  values: Record<string, string>;
  archived: boolean;
}

export interface GhStatusChange {
  key: string;
  projectId: string;
  field: string;
  from: string | null;
  to: string;
  at: string;
}

export interface FakeGitHubOptions {
  token?: string;
  login?: string;
  name?: string;
  orgs?: { login: string; name?: string }[];
}

export const GH_STATUS_OPTIONS = ["Todo", "In Progress", "In Review", "Done"];

export const ghKey = (repo: string, n: number) => `${repo}#${n}`;

export class FakeGitHub {
  readonly token: string;
  readonly login: string;
  readonly name: string;
  readonly orgs: { login: string; name: string }[];
  readonly projects = new Map<string, GhProject>();
  readonly issues = new Map<string, GhIssue>();
  readonly items = new Map<string, GhItem>();
  /** Every status the fake set through updateProjectV2ItemFieldValue, in order. */
  readonly statusChanges: GhStatusChange[] = [];
  /** Issues added to a project by addProjectV2ItemById. */
  readonly addedItems: { key: string; projectId: string }[] = [];
  /** X-OAuth-Scopes on every answer; null = none (a fine-grained token). */
  scopes: string | null = "repo, read:org, project";
  /** Owners whose resources answer a SAML SSO FORBIDDEN error. */
  ssoOrgs = new Set<string>();
  /** The next request answers 403 + Retry-After (seconds), once. */
  rateLimitNext: number | null = null;
  private seq = 1;

  constructor(opts: FakeGitHubOptions = {}) {
    this.token = opts.token ?? `ghp_fake${Math.random().toString(36).slice(2, 14)}`;
    this.login = opts.login ?? "e2e-dev";
    this.name = opts.name ?? "E2E Developer";
    this.orgs = (opts.orgs ?? [{ login: "e2e-local", name: "E2E Local" }]).map((o) => ({ login: o.login, name: o.name ?? o.login }));
  }

  get authorization() {
    return `Bearer ${this.token}`;
  }

  /** The token the stub gh hands out for this developer (`gh auth token`). */
  get ghLoginToken() {
    return `gho_stub_${this.login}`;
  }

  isAuthorized(header: string | null) {
    return header === this.authorization || header === `Bearer ${this.ghLoginToken}`;
  }

  handles(path: string) {
    return path === "/github/graphql" || path.startsWith("/github/");
  }

  // ── State API for specs ────────────────────────────────────────────────

  /** A project with a "Status" single-select (Todo / In Progress / In Review / Done) and a
   *  "Priority" one, so the field picker has a choice. */
  addProject(p: { owner?: string; number?: number; title: string; closed?: boolean; statusOptions?: string[]; statusField?: string }): GhProject {
    const n = p.number ?? this.projects.size + 1;
    const id = `PVT_${(p.owner ?? this.orgs[0].login).replace(/[^A-Za-z0-9]/g, "")}${n}`;
    const options = (p.statusOptions ?? GH_STATUS_OPTIONS).map((name, i) => ({ id: `${id}_s${i}`, name }));
    const project: GhProject = {
      id,
      owner: p.owner ?? this.orgs[0].login,
      number: n,
      title: p.title,
      closed: p.closed ?? false,
      fields: [
        { id: `${id}_f1`, name: p.statusField ?? "Status", options },
        { id: `${id}_f2`, name: "Priority", options: ["P0", "P1", "P2"].map((name, i) => ({ id: `${id}_p${i}`, name })) },
      ],
    };
    this.projects.set(id, project);
    return project;
  }

  addIssue(i: Partial<Omit<GhIssue, "id" | "comments" | "updatedAt">> & { repo: string; number: number; title: string; comments?: Omit<GhComment, "id">[] }): GhIssue {
    const issue: GhIssue = {
      id: `I_${i.repo.replace(/[^A-Za-z0-9]/g, "")}_${i.number}`,
      repo: i.repo,
      number: i.number,
      title: i.title,
      body: i.body ?? "",
      state: i.state ?? "OPEN",
      assignees: i.assignees ?? [this.login],
      labels: i.labels ?? [],
      issueType: i.issueType ?? null,
      subIssues: i.subIssues ?? [],
      blockedBy: i.blockedBy ?? [],
      comments: (i.comments ?? []).map((c) => ({ ...c, id: `IC_${this.seq++}` })),
      updatedAt: new Date(),
    };
    this.issues.set(ghKey(issue.repo, issue.number), issue);
    return issue;
  }

  /** Puts an item on a project: an issue (by key) with a status, or a draft / pull request. */
  addItem(projectId: string, it: { issueKey?: string; type?: GhItemType; status?: string | null; archived?: boolean }): GhItem {
    const project = this.mustProject(projectId);
    const item: GhItem = {
      id: `PVTI_${this.seq++}`,
      projectId: project.id,
      type: it.type ?? "ISSUE",
      issueKey: it.issueKey,
      values: it.status ? { [project.fields[0].name]: it.status } : {},
      archived: it.archived ?? false,
    };
    this.items.set(item.id, item);
    return item;
  }

  /** Moves an issue's item on a project (the tracker-side "someone moved the card"). */
  setStatus(projectId: string, key: string, status: string | null, field = "Status") {
    const item = this.itemFor(projectId, key);
    if (!item) throw new Error(`${key} is not on ${projectId}`);
    if (status === null) delete item.values[field];
    else item.values[field] = status;
    this.touch(key);
  }

  updateIssue(key: string, patch: Partial<Omit<GhIssue, "id" | "repo" | "number">>) {
    const issue = this.mustIssue(key);
    Object.assign(issue, patch);
    issue.updatedAt = new Date();
    return issue;
  }

  addComment(key: string, body: string, author = "colleague") {
    const issue = this.mustIssue(key);
    const c: GhComment = { id: `IC_${this.seq++}`, author, createdAt: new Date(), body };
    issue.comments.push(c);
    issue.updatedAt = new Date();
    return c;
  }

  itemFor(projectId: string, key: string): GhItem | undefined {
    return [...this.items.values()].find((i) => i.projectId === projectId && i.issueKey === key);
  }

  private touch(key: string) {
    const issue = this.issues.get(key);
    if (issue) issue.updatedAt = new Date();
  }

  private mustIssue(key: string) {
    const issue = this.issues.get(key);
    if (!issue) throw new Error(`no issue ${key}`);
    return issue;
  }

  private mustProject(id: string) {
    const p = this.projects.get(id);
    if (!p) throw new Error(`no project ${id}`);
    return p;
  }

  private parentOf(key: string): string | null {
    for (const [k, i] of this.issues) if (i.subIssues.includes(key)) return k;
    return null;
  }

  // ── GraphQL ────────────────────────────────────────────────────────────

  handle(ctx: Ctx) {
    if (ctx.method !== "POST" || ctx.path !== "/github/graphql") return send(ctx, 404, { message: "Not Found" });
    if (!this.isAuthorized(ctx.record.authorization)) return send(ctx, 401, { message: "Bad credentials", documentation_url: "https://docs.github.com/graphql" });
    if (this.rateLimitNext !== null) {
      const wait = this.rateLimitNext;
      this.rateLimitNext = null;
      ctx.record.status = 403;
      ctx.res.writeHead(403, { "Content-Type": "application/json", "Retry-After": String(wait) });
      ctx.res.end(JSON.stringify({ message: "You have exceeded a secondary rate limit. Please wait a few minutes before you try again." }));
      return;
    }
    const { query, variables } = (ctx.body ?? {}) as { query?: string; variables?: Record<string, unknown> };
    const q = query ?? "";
    const v = variables ?? {};
    const result = this.execute(ctx, q, v);
    ctx.record.status = 200;
    const payload = Buffer.from(JSON.stringify(result));
    const headers: Record<string, string | number> = { "Content-Type": "application/json; charset=utf-8", "Content-Length": payload.length };
    if (this.scopes !== null) headers["X-OAuth-Scopes"] = this.scopes;
    ctx.res.writeHead(200, headers);
    ctx.res.end(payload);
  }

  private sso(owner: string | undefined) {
    return owner !== undefined && this.ssoOrgs.has(owner)
      ? { data: null, errors: [{ type: "FORBIDDEN", message: `Resource protected by organization SAML enforcement. You must grant your Personal Access token access to the ${owner} organization.` }] }
      : null;
  }

  private execute(ctx: Ctx, q: string, v: Record<string, unknown>): unknown {
    if (q.includes("addProjectV2ItemById")) return this.addItemMutation(v);
    if (q.includes("updateProjectV2ItemFieldValue")) return this.updateMutation(v);
    if (q.includes("organizations(first:")) return this.scopesQuery(v);
    if (q.includes("repositoryOwner(login:")) return this.boardsQuery(v);
    if (/\bi\d+: repository\(/.test(q)) return this.batchQuery(ctx, q, v);
    if (q.includes("items(first:")) return this.itemsQuery(ctx, q, v);
    if (q.includes("comments(last:")) return this.commentsQuery(v);
    if (q.includes("projectItems(first: 50, includeArchived: true)")) return this.issueItemsQuery(v);
    if (q.includes("... on ProjectV2") && q.includes("fields(")) return this.projectQuery(v);
    if (/viewer\s*\{\s*login\s+name\s*\}/.test(q)) return { data: { viewer: { login: this.login, name: this.name } } };
    return { errors: [{ message: `fake GitHub: unknown operation: ${q.slice(0, 120)}` }] };
  }

  private scopesQuery(v: Record<string, unknown>) {
    const after = Number(v.after ?? 0) || 0;
    const page = this.orgs.slice(after, after + 100);
    return {
      data: {
        viewer: {
          login: this.login,
          name: this.name,
          organizations: { pageInfo: { hasNextPage: after + 100 < this.orgs.length, endCursor: String(after + 100) }, nodes: page.map((o) => ({ login: o.login, name: o.name })) },
        },
      },
    };
  }

  private boardsQuery(v: Record<string, unknown>) {
    const login = String(v.login ?? "");
    const sso = this.sso(login);
    if (sso) return sso;
    if (login !== this.login && !this.orgs.some((o) => o.login === login))
      return { data: { repositoryOwner: null }, errors: [{ type: "NOT_FOUND", path: ["repositoryOwner"], message: `Could not resolve to a RepositoryOwner with the login of '${login}'.` }] };
    const all = [...this.projects.values()].filter((p) => p.owner === login);
    const after = Number(v.after ?? 0) || 0;
    const page = all.slice(after, after + 100);
    return {
      data: {
        repositoryOwner: {
          projectsV2: {
            pageInfo: { hasNextPage: after + 100 < all.length, endCursor: String(after + 100) },
            nodes: page.map((p) => ({ id: p.id, number: p.number, title: p.title, closed: p.closed, url: `https://github.com/orgs/${p.owner}/projects/${p.number}` })),
          },
        },
      },
    };
  }

  private projectQuery(v: Record<string, unknown>) {
    const p = this.projects.get(String(v.id ?? ""));
    if (!p) return { data: { node: null }, errors: [{ type: "NOT_FOUND", path: ["node"], message: `Could not resolve to a node with the global id of '${v.id}'` }] };
    const sso = this.sso(p.owner);
    if (sso) return sso;
    return {
      data: {
        node: {
          id: p.id,
          title: p.title,
          closed: p.closed,
          // A text field too: the client must skip anything that is not single-select.
          fields: { nodes: [{}, ...p.fields.map((f) => ({ id: f.id, name: f.name, options: f.options }))] },
        },
      },
    };
  }

  private issueJson(issue: GhIssue) {
    const parent = this.parentOf(ghKey(issue.repo, issue.number));
    const [pRepo, pNum] = parent ? parent.split("#") : [];
    return {
      id: issue.id,
      number: issue.number,
      title: issue.title,
      url: `https://github.com/${issue.repo}/issues/${issue.number}`,
      body: issue.body,
      state: issue.state,
      updatedAt: issue.updatedAt.toISOString(),
      repository: { nameWithOwner: issue.repo, url: `https://github.com/${issue.repo}` },
      assignees: { nodes: issue.assignees.map((login) => ({ login })) },
      labels: { nodes: issue.labels.map((name) => ({ name })) },
      issueType: issue.issueType ? { name: issue.issueType } : null,
      parent: parent
        ? {
            number: Number(pNum),
            repository: { nameWithOwner: pRepo },
            issueType: this.issues.get(parent)?.issueType ? { name: this.issues.get(parent)!.issueType } : null,
          }
        : null,
      subIssues: {
        nodes: issue.subIssues.map((k) => {
          const [repo, n] = k.split("#");
          return { number: Number(n), repository: { nameWithOwner: repo }, subIssuesSummary: { total: this.issues.get(k)?.subIssues.length ?? 0 } };
        }),
      },
      blockedBy: {
        nodes: issue.blockedBy.map((k) => {
          const [repo, n] = k.split("#");
          return { number: Number(n), repository: { nameWithOwner: repo } };
        }),
      },
    };
  }

  private fieldValue(item: GhItem, field: string) {
    const project = this.projects.get(item.projectId)!;
    const f = project.fields.find((x) => x.name.toLowerCase() === field.toLowerCase());
    const name = f ? item.values[f.name] : undefined;
    if (!f || name === undefined) return null;
    return { name, optionId: f.options.find((o) => o.name === name)?.id ?? null };
  }

  private itemsQuery(ctx: Ctx, q: string, v: Record<string, unknown>) {
    const p = this.projects.get(String(v.id ?? ""));
    if (!p) return { data: { viewer: { login: this.login }, node: null }, errors: [{ type: "NOT_FOUND", path: ["node"], message: `Could not resolve to a node with the global id of '${v.id}'` }] };
    const sso = this.sso(p.owner);
    if (sso) return sso;
    const first = Number(/items\(first:\s*(\d+)/.exec(q)?.[1] ?? 50);
    const field = String(v.field ?? "Status");
    const all = [...this.items.values()].filter((i) => i.projectId === p.id);
    const after = Number(v.after ?? 0) || 0;
    const page = all.slice(after, after + first);
    return {
      data: {
        viewer: { login: this.login },
        node: {
          items: {
            pageInfo: { hasNextPage: after + first < all.length, endCursor: String(after + first) },
            nodes: page.map((i) => {
              const issue = i.type === "ISSUE" && i.issueKey ? this.issues.get(i.issueKey) : undefined;
              return {
                id: i.id,
                type: i.type,
                isArchived: i.archived,
                fieldValueByName: this.fieldValue(i, field),
                // A draft or a pull request is not an Issue: the inline fragment yields nothing.
                content: issue ? this.issueJson(issue) : {},
              };
            }),
          },
        },
      },
    };
  }

  private batchQuery(ctx: Ctx, q: string, v: Record<string, unknown>) {
    const field = String(v.field ?? "Status");
    const data: Record<string, unknown> = { viewer: { login: this.login } };
    const errors: unknown[] = [];
    for (const m of q.matchAll(/\b(i\d+): repository\(owner: "([^"]+)", name: "([^"]+)"\) \{ issue\(number: (\d+)\)/g)) {
      const [, alias, owner, name, num] = m;
      const sso = this.sso(owner);
      if (sso) return sso;
      const repo = `${owner}/${name}`;
      const repoKnown = [...this.issues.values()].some((i) => i.repo.toLowerCase() === repo.toLowerCase());
      if (!repoKnown) {
        data[alias] = null;
        errors.push({ type: "NOT_FOUND", path: [alias], message: `Could not resolve to a Repository with the name '${repo}'.` });
        continue;
      }
      const issue = [...this.issues.values()].find((i) => i.repo.toLowerCase() === repo.toLowerCase() && i.number === Number(num));
      if (!issue) {
        data[alias] = { issue: null };
        errors.push({ type: "NOT_FOUND", path: [alias, "issue"], message: `Could not resolve to an issue or pull request with the number of ${num}.` });
        continue;
      }
      const key = ghKey(issue.repo, issue.number);
      const items = [...this.items.values()].filter((i) => i.issueKey === key && !i.archived);
      data[alias] = { issue: { ...this.issueJson(issue), projectItems: { nodes: items.map((i) => ({ id: i.id, project: { id: i.projectId }, fieldValueByName: this.fieldValue(i, field) })) } } };
    }
    return errors.length ? { data, errors } : { data };
  }

  private issueById(id: string) {
    return [...this.issues.values()].find((i) => i.id === id);
  }

  private commentsQuery(v: Record<string, unknown>) {
    const issue = this.issueById(String(v.id ?? ""));
    if (!issue) return { data: { node: null }, errors: [{ type: "NOT_FOUND", path: ["node"], message: "Could not resolve to a node" }] };
    const n = Number(v.n ?? 30);
    return { data: { node: { comments: { nodes: issue.comments.slice(-n).map((c) => ({ id: c.id, author: { login: c.author }, createdAt: c.createdAt.toISOString(), body: c.body })) } } } };
  }

  private issueItemsQuery(v: Record<string, unknown>) {
    const issue = this.issueById(String(v.id ?? ""));
    if (!issue) return { data: { node: null }, errors: [{ type: "NOT_FOUND", path: ["node"], message: "Could not resolve to a node" }] };
    const key = ghKey(issue.repo, issue.number);
    const field = String(v.field ?? "Status");
    const items = [...this.items.values()].filter((i) => i.issueKey === key);
    return { data: { node: { projectItems: { nodes: items.map((i) => ({ id: i.id, project: { id: i.projectId }, fieldValueByName: this.fieldValue(i, field) })) } } } };
  }

  private addItemMutation(v: Record<string, unknown>) {
    const project = this.projects.get(String(v.project ?? ""));
    const issue = this.issueById(String(v.content ?? ""));
    if (!project || !issue) return { data: { addProjectV2ItemById: null }, errors: [{ type: "NOT_FOUND", message: "Could not resolve to a node" }] };
    const key = ghKey(issue.repo, issue.number);
    const item = this.itemFor(project.id, key) ?? this.addItem(project.id, { issueKey: key });
    this.addedItems.push({ key, projectId: project.id });
    return { data: { addProjectV2ItemById: { item: { id: item.id } } } };
  }

  private updateMutation(v: Record<string, unknown>) {
    const project = this.projects.get(String(v.project ?? ""));
    const item = this.items.get(String(v.item ?? ""));
    const field = project?.fields.find((f) => f.id === v.field);
    const option = field?.options.find((o) => o.id === v.option);
    if (!project || !item || !field || !option || item.projectId !== project.id)
      return { data: { updateProjectV2ItemFieldValue: null }, errors: [{ type: "NOT_FOUND", message: "Could not resolve to a node" }] };
    const from = item.values[field.name] ?? null;
    item.values[field.name] = option.name;
    if (item.issueKey) this.touch(item.issueKey);
    this.statusChanges.push({ key: item.issueKey ?? item.id, projectId: project.id, field: field.name, from, to: option.name, at: new Date().toISOString() });
    return { data: { updateProjectV2ItemFieldValue: { projectV2Item: { id: item.id } } } };
  }
}
